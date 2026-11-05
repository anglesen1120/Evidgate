using System.Globalization;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// 02.9.1-T3.1 (story 02.9.1 §2 AC5, §5 "catalogue semantics"; 00 §5.4.1/§5.4.7, 02 §4.5, TL T3):
/// the catalogue tables are readable by every org and writable by none.
/// <para>
/// <c>public.permission</c> is a platform catalogue with no <c>org_id</c> column at all;
/// <c>public.role</c> and <c>public.role_permission</c> carry a nullable <c>org_id</c> whose NULL
/// is the built-in row (0010). All three are on <c>contracts/rls-exceptions.txt</c>, so the
/// tenant-set discovery of 02.9.1-T2 never sees them — which is exactly why AC5 needs its own
/// suite: "not org-scoped" must not mean "unprotected".
/// </para>
/// <para>
/// Every statement runs as <c>evidgate_app</c> inside a transaction that is rolled back, so the
/// suite leaves no row behind and proves the path the API uses (00 §5.5 C3). A write is refused
/// when it raises <c>42501</c> (the missing privilege, or the <c>WITH CHECK</c> of the policy) or,
/// for UPDATE/DELETE, when the policy's <c>USING</c> clause hides the built-in row and the
/// statement silently changes zero rows — both are refusals, and the suite asserts the rows are
/// still there afterwards either way. Each statement gets its own transaction: a refused
/// statement aborts the transaction it ran in (25P02), so a shared one would turn every later
/// probe into a "current transaction is aborted" failure instead of the refusal under test.
/// </para>
/// <para>
/// The built-in rows themselves are migration-owned reference data (02 §8), and the harness's
/// blanket reset empties them like any other table, so the suite re-applies the seed block of
/// <c>migrations/0010_identity_permission_catalogue.sql</c> first — the same step, from the same
/// source, as the 02.2.1 catalogue suites (<c>CatalogueTestBase</c>). The rows are never
/// hand-written here.
/// </para>
/// <para>
/// Two holes the built-in-only probes cannot reach are covered explicitly. CR1: <c>role</c> and
/// <c>role_permission</c> carry a nullable <c>org_id</c>, so a permissive <c>using (true)</c>
/// policy would leak one org's custom rows to another; the suite seeds an org-B custom role and
/// its grant and asserts they are invisible to org A and to a session with no tenant setting, and
/// visible to org B. SEC2: <c>TRUNCATE</c> is not scoped by row level security at all, so the only
/// thing that can refuse it is the missing privilege — the suite probes it alongside the
/// insert/update/delete refusals.
/// </para>
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class CatalogueTests : HarnessTest
{
    /// <summary>The migration that owns the catalogue tables and their seed (02.2.1-T1.2).</summary>
    private const string CatalogueMigration = "0010_identity_permission_catalogue.sql";

    /// <summary>Start of the migration's seed block (one <c>do</c> statement).</summary>
    private const string SeedStart = "do $seed$";

    /// <summary>End of the migration's seed block, including the statement terminator.</summary>
    private const string SeedEnd = "$seed$;";

    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OrgB = Guid.Parse("22222222-2222-7222-8222-222222222222");

    /// <summary>The fixed id of the built-in system switch row this suite seeds for the 0052 surface.</summary>
    private static readonly Guid SystemSwitchId = Guid.Parse("33333333-3333-7333-8333-333333333333");

    /// <summary>The fixed id of the fixture platform price row this suite seeds for the 0073 surface.</summary>
    private static readonly Guid UsagePriceRowId = Guid.Parse("44444444-4444-7444-8444-444444444444");

    /// <summary>
    /// The catalogue surfaces AC5 names, with the clause that selects their built-in rows and the
    /// expression that yields a row key as text. <c>permission</c> has no <c>org_id</c> column, so
    /// every one of its rows is a built-in row.
    /// </summary>
    private static readonly (string Table, string BuiltInWhere, string KeyExpression)[] ReadSurfaces =
    [
        ("public.permission", "true", "code"),
        ("public.role", "org_id is null", "id::text"),
        ("public.role_permission", "org_id is null", "id::text"),
        // 08.6.3 (0052): the system-scope kill switch is the platform-wide built-in row every tenant
        // reads; its tenant/project siblings are the per-org custom rows (a separate suite's subject).
        ("public.healing_switch", "scope = 'system'", "id::text"),
        // 12.3.1 (0073): the platform rate card is the built-in row every tenant reads.
        ("public.usage_price", "org_id is null", "id::text"),
    ];

    private readonly ITestOutputHelper _output;

    /// <summary>Binds the test to the shared harness fixture (container, migrations, Respawn).</summary>
    public CatalogueTests(DatabaseFixture fixture, ITestOutputHelper output)
        : base(fixture)
    {
        _output = output;
    }

    /// <summary>
    /// AC5: the built-in catalogue rows are visible under any tenant setting — the same rows under
    /// org A and org B — and the app role cannot insert, update or delete one. A catalogue table
    /// that exists without a probe fails the suite instead of passing silently, and one that does
    /// not exist yet is reported as inert.
    /// </summary>
    [Fact]
    public async Task ReadableByEveryOrg_NotWritableByApp()
    {
        // The harness reset (HarnessTest.InitializeAsync) truncated the catalogue tables with
        // everything else; restore the migration's own built-in rows before probing them.
        await SeedCatalogueAsync();

        await using var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token);
        var exceptions = RlsExceptionList.Read();

        var failures = new List<string>();

        // 1. The three surfaces AC5 names must exist: the suite cannot prove anything about a
        //    catalogue it cannot see.
        foreach (var surface in ReadSurfaces)
        {
            (await RlsTableCatalog.TableExistsAsync(connection, surface.Table, Token))
                .ShouldBeTrue($"{surface.Table} must exist: AC5 covers its built-in rows");
        }

        // 2. A catalogue table that exists but carries no probe is a hole, not a pass: the probe
        //    set is the one list a later migration must extend, and this makes forgetting it fail.
        var probed = ReadSurfaces.Select(surface => surface.Table).ToHashSet(StringComparer.Ordinal);
        var unprobed = new List<string>();
        var inert = new List<string>();
        foreach (var entry in exceptions.Where(entry => string.Equals(entry.Kind, "catalogue", StringComparison.Ordinal)))
        {
            if (await RlsTableCatalog.TableExistsAsync(connection, entry.Table, Token))
            {
                if (!probed.Contains(entry.Table))
                {
                    unprobed.Add(entry.Table);
                }
            }
            else
            {
                inert.Add(entry.Table);
            }
        }

        unprobed.ShouldBeEmpty(
            $"catalogue table(s) exist with no probe: {string.Join(", ", unprobed)} — extend the probe set of this suite "
            + "so their built-in rows are covered by AC5");

        foreach (var table in inert)
        {
            _output.WriteLine("{0}: inert — the table does not exist yet (the exception list allows an entry before its migration)", table);
        }

        // 3. Read half: the built-in rows are the same under either tenant setting, and there is
        //    at least one of them (an empty catalogue would make the write half vacuous).
        foreach (var (table, builtInWhere, keyExpression) in ReadSurfaces)
        {
            await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString, OrgA);
            var underA = await ReadKeysAsync(scope, table, builtInWhere, keyExpression);
            await scope.SetAsync(RlsTableCatalog.TenantSetting, OrgB.ToString("D", CultureInfo.InvariantCulture));
            var underB = await ReadKeysAsync(scope, table, builtInWhere, keyExpression);

            if (underA.Count == 0)
            {
                failures.Add($"{table}: no built-in row is visible under org {OrgA} — the catalogue read is empty");
            }

            if (!underA.SequenceEqual(underB))
            {
                failures.Add(
                    $"{table}: the built-in rows differ between org {OrgA} [{Keys(underA)}] and org {OrgB} [{Keys(underB)}] — "
                    + "a catalogue row must be readable by every org");
            }

            _output.WriteLine("{0}: {1} built-in row(s), identical under org A and org B", table, underA.Count);
        }

        // 4. Write half: every command is refused, and the built-in rows are unchanged afterwards.
        await CheckPermissionWritesAsync(failures);
        await CheckRoleWritesAsync(failures);
        await CheckRolePermissionWritesAsync(failures);
        await CheckHealingSwitchWritesAsync(failures);
        await CheckUsagePriceWritesAsync(failures);

        failures.ShouldBeEmpty(
            $"the catalogue is not read-only for the application role:{Environment.NewLine}"
            + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// CR1: the catalogue's <em>custom</em> rows are org-scoped. <c>role</c> and
    /// <c>role_permission</c> carry a nullable <c>org_id</c> (0010), and the read policy is
    /// <c>org_id is null or org_id = evidgate.current_org_id()</c> — the built-in-only probes of
    /// <see cref="ReadableByEveryOrg_NotWritableByApp"/> never read a non-null <c>org_id</c>, so a
    /// permissive <c>using (true)</c> policy would leak org B's custom role to org A undetected.
    /// This test seeds one org-B custom role and one org-B grant through the owner seam and asserts
    /// the read half of the policy: invisible to org A, visible to org B, and — with no tenant
    /// setting — only the built-ins remain.
    /// </summary>
    [Fact]
    public async Task CustomRoleRows_AreOrgScoped()
    {
        await SeedCatalogueAsync();
        var (roleId, grantId) = await SeedOrgBCustomRoleAsync();

        var failures = new List<string>();

        await CheckCustomRowScopeAsync("public.role", "id::text", roleId, failures);
        await CheckCustomRowScopeAsync("public.role_permission", "id::text", grantId, failures);

        failures.ShouldBeEmpty(
            $"the catalogue's custom rows are not scoped to their org:{Environment.NewLine}"
            + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Seeds org A, org B and one custom role of org B plus one grant of that role, as
    /// <c>evidgate_owner</c> (the harness's one seeding seam). The values respect the real columns
    /// and constraints of 0010: <c>role.kind</c> is one of <c>member</c>/<c>service</c>/<c>staff</c>
    /// (0010:102), <c>is_builtin = (org_id is null)</c> forces <c>false</c> for a tenant row
    /// (0010:105), the code matches <c>^[a-z][a-z0-9_]*$</c> (0010:91) and the grant's
    /// <c>permission_code</c> is an existing catalogue code (<c>case:read</c>, 0010:318) so the FK
    /// holds. The org rows are not referenced by <c>role.org_id</c> today (0010 declares no FK
    /// there), but the tenant pair is seeded as a real one so a later FK or a policy that joins
    /// <c>organization</c> still holds.
    /// </summary>
    private async Task<(Guid RoleId, Guid GrantId)> SeedOrgBCustomRoleAsync()
    {
        await Fixture.SeedAsync(
            "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
            [("id", OrgA), ("name", "Org A"), ("slug", "org-a-0291t3")],
            Token);
        await Fixture.SeedAsync(
            "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
            [("id", OrgB), ("name", "Org B"), ("slug", "org-b-0291t3")],
            Token);

        var roleId = Guid.NewGuid();
        await Fixture.SeedAsync(
            "insert into public.role (id, org_id, code, name, is_builtin, kind) values (@id, @org_id, @code, @name, false, 'member')",
            [
                ("id", roleId),
                ("org_id", OrgB),
                ("code", "custom_probe_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]),
                ("name", "Custom probe role"),
            ],
            Token);

        var grantId = Guid.NewGuid();
        await Fixture.SeedAsync(
            "insert into public.role_permission (id, org_id, role_id, permission_code) values (@id, @org_id, @role_id, @permission_code)",
            [
                ("id", grantId),
                ("org_id", OrgB),
                ("role_id", roleId),
                ("permission_code", "case:read"),
            ],
            Token);

        return (roleId, grantId);
    }

    /// <summary>
    /// One table's custom-row scope: the org-B row is invisible under org A, visible under org B,
    /// and gone (only the built-ins remain) with no tenant setting. Every failure names the table.
    /// </summary>
    private async Task CheckCustomRowScopeAsync(
        string table,
        string keyExpression,
        Guid orgBKey,
        List<string> failures)
    {
        var expected = new[] { orgBKey.ToString("D", CultureInfo.InvariantCulture) };

        // The built-ins are the reference set the unset-GUC read must reduce to, and their
        // presence keeps the "only built-ins" assertion from passing on an empty table.
        var builtIns = await ReadKeysAsync(table, "org_id is null", keyExpression);
        if (builtIns.Count == 0)
        {
            failures.Add($"{table}: no built-in row is visible — the custom-row scope probe would be vacuous");
        }

        await using (var scopeA = await TenantScope.BeginAsync(Fixture.AppConnectionString, OrgA))
        {
            var underA = await ReadOrgKeysAsync(scopeA, table, keyExpression, OrgB);
            if (underA.Count != 0)
            {
                failures.Add(
                    $"{table}: org {OrgA} sees org {OrgB}'s custom row(s) [{Keys(underA)}] — the catalogue read is not org-scoped");
            }
        }

        await using (var scopeB = await TenantScope.BeginAsync(Fixture.AppConnectionString, OrgB))
        {
            var underB = await ReadOrgKeysAsync(scopeB, table, keyExpression, OrgB);
            if (!underB.SequenceEqual(expected))
            {
                failures.Add(
                    $"{table}: org {OrgB} does not see its own custom row [{Keys(underB)}], expected [{Keys(expected)}]");
            }
        }

        await using (var unset = await TenantScope.BeginAsync(Fixture.AppConnectionString))
        {
            var visible = await ReadAllKeysAsync(unset, table, keyExpression);
            if (!visible.SequenceEqual(builtIns))
            {
                failures.Add(
                    $"{table}: with no tenant setting the visible rows are [{Keys(visible)}], expected only the built-ins [{Keys(builtIns)}]");
            }
        }
    }

    /// <summary>
    /// Re-applies the seed block of <c>migrations/0010_identity_permission_catalogue.sql</c> as
    /// <c>evidgate_owner</c> — the same step, read from the same source, as the 02.2.1 catalogue
    /// suites (<c>tests/Identity.IntegrationTests/CatalogueTestBase.cs</c>). The rows are never
    /// hand-written here, so the suite asserts against the very bytes the database is seeded from.
    /// <para>
    /// Why it is needed: <see cref="DatabaseFixture.ResetAsync"/> is Respawn's blanket
    /// <c>truncate … cascade</c> over every application table, which empties <c>permission</c>,
    /// <c>role</c> and <c>role_permission</c> exactly like any other table. Those rows are
    /// migration-owned reference data (built-ins are never deleted, 02 §8), not test fixtures.
    /// </para>
    /// <para>
    /// Why it commits instead of running inside the rolled-back probe transaction: every probe
    /// below opens its own <see cref="TenantScope"/> — a separate connection and transaction
    /// (00 §5.5 C3) — so an uncommitted seed would be invisible to them. The block is the
    /// migration's own idempotent seed, and every test of <see cref="HarnessSuite"/> resets the
    /// database before it runs, so the committed rows never reach another suite.
    /// </para>
    /// </summary>
    private async Task SeedCatalogueAsync()
    {
        var path = Path.Combine(DatabaseFixture.MigrationsPath, CatalogueMigration);
        var migration = await File.ReadAllTextAsync(path, Token);

        var start = migration.IndexOf(SeedStart, StringComparison.Ordinal);
        var end = migration.IndexOf(SeedEnd, StringComparison.Ordinal);

        if (start < 0 || end < start)
        {
            throw new InvalidOperationException(
                $"'{CatalogueMigration}' no longer carries its seed block ('{SeedStart}' … '{SeedEnd}'): "
                + "this suite re-applies it and must fail loudly when it moves.");
        }

        await Fixture.SeedAsync(migration[start..(end + SeedEnd.Length)], [], Token);

        // 08.6.3 (0052) seeds no row: a database with no switch row resolves to the `shadow` posture,
        // so the built-in row this suite probes is created by the operator's own write path (the
        // tenant-less system row, which the app role cannot write directly), once, as the owner seam.
        await Fixture.SeedAsync(
            """
            insert into public.healing_switch (id, scope, org_id, project_id, mode, reason, set_by, at)
            values (@id, 'system', null, null, 'shadow', 'catalogue probe', @set_by, now())
            on conflict do nothing
            """,
            [("id", SystemSwitchId), ("set_by", OrgA)],
            Token);

        // 12.3.1 (0073): the platform rate card is written by the definer; the probe row is a fixture
        // price (no real number), created through the owner seam like the switch row above.
        await Fixture.SeedAsync(
            """
            insert into public.usage_price (id, org_id, metric, unit_price, currency, effective_from, rate_card_version)
            values (@id, null, 'results', 1, 'USD', '2026-01-01T00:00:00Z', 'catalogue-probe')
            on conflict do nothing
            """,
            [("id", UsagePriceRowId)],
            Token);
    }

    /// <summary><c>public.permission</c>: 0010 revokes INSERT/UPDATE/DELETE from the app role, so every command is a 42501.</summary>
    private async Task CheckPermissionWritesAsync(List<string> failures)
    {
        const string table = "public.permission";
        var before = await ReadKeysAsync(table, "true", "code");
        before.ShouldNotBeEmpty($"{table}: the built-in catalogue is empty — the write probe would have nothing to target");

        var insert = await RefusalAsync(
            "insert into public.permission (code, resource, action, description) values (@key, 'probe', 'probe', 'probe')",
            ("key", "probe:probe"));
        AssertRefused(table, "INSERT of a built-in row", insert, silentAllowed: false, failures);

        var update = await RefusalAsync(
            "update public.permission set description = description where code = @key",
            ("key", before[0]));
        AssertRefused(table, "UPDATE of a built-in row", update, silentAllowed: true, failures);

        var delete = await RefusalAsync(
            "delete from public.permission where code = @key",
            ("key", before[0]));
        AssertRefused(table, "DELETE of a built-in row", delete, silentAllowed: true, failures);

        // SEC2: TRUNCATE is not scoped by row level security, so the only refusal available is the
        // missing privilege — 0010 never grants it and 0001's default privileges do not include it.
        var truncate = await RefusalAsync("truncate public.permission");
        AssertRefused(table, "TRUNCATE", truncate, silentAllowed: false, failures);

        await AssertUnchangedAsync(table, "true", "code", before, failures);
        _output.WriteLine("{0}: insert/update/delete/truncate refused; {1} built-in row(s) unchanged", table, before.Count);
    }

    /// <summary>
    /// <c>public.role</c>: the app role may write only rows of its own org, so an insert of a
    /// built-in row (<c>org_id</c> NULL) fails the <c>WITH CHECK</c> and an update of one changes
    /// zero rows; DELETE is revoked outright.
    /// </summary>
    private async Task CheckRoleWritesAsync(List<string> failures)
    {
        const string table = "public.role";
        var before = await ReadKeysAsync(table, "org_id is null", "id::text");
        before.ShouldNotBeEmpty($"{table}: the built-in catalogue is empty — the write probe would have nothing to target");

        var insert = await RefusalAsync(
            "insert into public.role (id, org_id, code, name, is_builtin, kind) values (@key::uuid, null, @code, 'Probe', true, 'member')",
            ("key", Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
            ("code", "probe" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]));
        AssertRefused(table, "INSERT of a built-in row", insert, silentAllowed: false, failures);

        var update = await RefusalAsync(
            "update public.role set name = name where id::text = @key",
            ("key", before[0]));
        AssertRefused(table, "UPDATE of a built-in row", update, silentAllowed: true, failures);

        var delete = await RefusalAsync(
            "delete from public.role where id::text = @key",
            ("key", before[0]));
        AssertRefused(table, "DELETE of a built-in row", delete, silentAllowed: true, failures);

        // SEC2: TRUNCATE is not scoped by row level security, so the only refusal available is the
        // missing privilege — 0010 never grants it and 0001's default privileges do not include it.
        var truncate = await RefusalAsync("truncate public.role");
        AssertRefused(table, "TRUNCATE", truncate, silentAllowed: false, failures);

        await AssertUnchangedAsync(table, "org_id is null", "id::text", before, failures);
        _output.WriteLine("{0}: insert/update/delete/truncate refused; {1} built-in row(s) unchanged", table, before.Count);
    }

    /// <summary>
    /// <c>public.role_permission</c>: same posture as <c>role</c>, plus the W7-SEC-05 tie that the
    /// referenced role must itself be an org row of the caller's org — so a grant can never be
    /// attached to a built-in role. The insert uses a (built-in role, permission) pair that is not
    /// already granted, so the only possible refusal is the policy, never the unique key.
    /// </summary>
    private async Task CheckRolePermissionWritesAsync(List<string> failures)
    {
        const string table = "public.role_permission";
        var before = await ReadKeysAsync(table, "org_id is null", "id::text");
        before.ShouldNotBeEmpty($"{table}: the built-in catalogue is empty — the write probe would have nothing to target");

        var pair = await ReadUnGrantedPairAsync();
        pair.ShouldNotBeNullOrWhiteSpace(
            "no un-granted (built-in role, permission) pair exists: the INSERT probe cannot be built");

        var parts = pair!.Split('|', 2);
        var insert = await RefusalAsync(
            "insert into public.role_permission (id, org_id, role_id, permission_code) values (@key::uuid, null, @role_id::uuid, @permission_code)",
            ("key", Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
            ("role_id", parts[0]),
            ("permission_code", parts[1]));
        AssertRefused(table, "INSERT of a built-in row", insert, silentAllowed: false, failures);

        var update = await RefusalAsync(
            "update public.role_permission set permission_code = permission_code where id::text = @key",
            ("key", before[0]));
        AssertRefused(table, "UPDATE of a built-in row", update, silentAllowed: true, failures);

        var delete = await RefusalAsync(
            "delete from public.role_permission where id::text = @key",
            ("key", before[0]));
        AssertRefused(table, "DELETE of a built-in row", delete, silentAllowed: true, failures);

        // SEC2: TRUNCATE is not scoped by row level security, so the only refusal available is the
        // missing privilege — 0010 never grants it and 0001's default privileges do not include it.
        var truncate = await RefusalAsync("truncate public.role_permission");
        AssertRefused(table, "TRUNCATE", truncate, silentAllowed: false, failures);

        await AssertUnchangedAsync(table, "org_id is null", "id::text", before, failures);
        _output.WriteLine("{0}: insert/update/delete/truncate refused; {1} built-in row(s) unchanged", table, before.Count);
    }

    /// <summary>
    /// <c>public.healing_switch</c> (08.6.3/0052): the system row is the platform's, so an app-role
    /// INSERT of another one fails the <c>WITH CHECK</c> (<c>org_id</c> NULL never equals the current
    /// org), an UPDATE of it matches zero rows, and DELETE is revoked outright.
    /// </summary>
    private async Task CheckHealingSwitchWritesAsync(List<string> failures)
    {
        const string table = "public.healing_switch";
        var before = await ReadKeysAsync(table, "scope = 'system'", "id::text");
        before.ShouldNotBeEmpty($"{table}: the system switch row is missing — the write probe would have nothing to target");

        var insert = await RefusalAsync(
            "insert into public.healing_switch (id, scope, org_id, project_id, mode, set_by, at) values (@key::uuid, 'system', null, null, 'disabled', @key::uuid, now())",
            ("key", Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)));
        AssertRefused(table, "INSERT of a second system row", insert, silentAllowed: false, failures);

        var update = await RefusalAsync(
            "update public.healing_switch set mode = 'disabled' where scope = 'system'");
        AssertRefused(table, "UPDATE of the system row", update, silentAllowed: true, failures);

        var delete = await RefusalAsync(
            "delete from public.healing_switch where scope = 'system'");
        AssertRefused(table, "DELETE of the system row", delete, silentAllowed: true, failures);

        // SEC2: TRUNCATE is not scoped by row level security, so the only refusal available is the
        // missing privilege — 0052 never grants it and 0001's default privileges do not include it.
        var truncate = await RefusalAsync("truncate public.healing_switch");
        AssertRefused(table, "TRUNCATE", truncate, silentAllowed: false, failures);

        await AssertUnchangedAsync(table, "scope = 'system'", "id::text", before, failures);
        _output.WriteLine("{0}: insert/update/delete/truncate refused; {1} built-in row(s) unchanged", table, before.Count);
    }

    /// <summary>
    /// <c>public.usage_price</c>: the platform rate-card rows (<c>org_id</c> NULL) are readable by every org and the app
    /// role can neither insert one (the <c>WITH CHECK</c> never matches a NULL org), nor update or delete (no privilege).
    /// </summary>
    private async Task CheckUsagePriceWritesAsync(List<string> failures)
    {
        const string table = "public.usage_price";
        var before = await ReadKeysAsync(table, "org_id is null", "id::text");
        before.ShouldNotBeEmpty($"{table}: the platform price row is missing — the write probe would have nothing to target");

        var insert = await RefusalAsync(
            "insert into public.usage_price (id, org_id, metric, unit_price, currency, effective_from, rate_card_version) values (@key::uuid, null, 'egress_gb', 1, 'USD', now(), 'probe')",
            ("key", Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)));
        AssertRefused(table, "INSERT of a platform row", insert, silentAllowed: false, failures);

        var update = await RefusalAsync("update public.usage_price set unit_price = 2 where org_id is null");
        AssertRefused(table, "UPDATE of a platform row", update, silentAllowed: true, failures);

        var delete = await RefusalAsync("delete from public.usage_price where org_id is null");
        AssertRefused(table, "DELETE of a platform row", delete, silentAllowed: true, failures);

        var truncate = await RefusalAsync("truncate public.usage_price");
        AssertRefused(table, "TRUNCATE", truncate, silentAllowed: false, failures);

        await AssertUnchangedAsync(table, "org_id is null", "id::text", before, failures);
        _output.WriteLine("{0}: insert/update/delete/truncate refused; {1} built-in row(s) unchanged", table, before.Count);
    }

    /// <summary>
    /// A (built-in role, permission) pair no <c>role_permission</c> row grants yet, as
    /// <c>role_id|permission_code</c>. The built-in catalogue is large enough that one always
    /// exists; the probe asserts that rather than assuming it.
    /// </summary>
    private async Task<string?> ReadUnGrantedPairAsync()
    {
        await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString, OrgA);
        return await scope.ScalarAsync<string?>(
            """
            select r.id::text || '|' || p.code
              from public.role r
              cross join public.permission p
             where r.org_id is null
               and not exists (select 1
                                 from public.role_permission rp
                                where rp.role_id = r.id
                                  and rp.permission_code = p.code)
             order by r.code, p.code
             limit 1
            """);
    }

    /// <summary>
    /// Runs one write as the app role, in its own rolled-back transaction, and returns how it was
    /// refused: the SQLSTATE, or <c>"0 rows affected"</c> for a silent no-op, or null when the
    /// statement went through.
    /// </summary>
    private async Task<string?> RefusalAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString, OrgA);
        try
        {
            var affected = await scope.ExecuteAsync(sql, parameters);
            return affected == 0 ? "0 rows affected" : null;
        }
        catch (PostgresException exception)
        {
            return exception.SqlState;
        }
    }

    private static void AssertRefused(
        string table,
        string command,
        string? refusal,
        bool silentAllowed,
        List<string> failures)
    {
        if (refusal is null)
        {
            failures.Add($"{table}: the app role's {command} was NOT refused — the statement succeeded and changed rows");
            return;
        }

        if (string.Equals(refusal, "42501", StringComparison.Ordinal))
        {
            return;
        }

        if (silentAllowed && string.Equals(refusal, "0 rows affected", StringComparison.Ordinal))
        {
            return;
        }

        failures.Add(
            $"{table}: the app role's {command} was refused with SQLSTATE {refusal}, expected 42501"
            + (silentAllowed ? " or a silent 0-row no-op" : string.Empty));
    }

    /// <summary>The built-in rows are still exactly the ones read before the writes.</summary>
    private async Task AssertUnchangedAsync(
        string table,
        string builtInWhere,
        string keyExpression,
        IReadOnlyList<string> before,
        List<string> failures)
    {
        var after = await ReadKeysAsync(table, builtInWhere, keyExpression);
        if (!after.SequenceEqual(before))
        {
            failures.Add(
                $"{table}: the built-in rows changed after the refused writes — before [{Keys(before)}], after [{Keys(after)}]");
        }
    }

    private async Task<IReadOnlyList<string>> ReadKeysAsync(string table, string builtInWhere, string keyExpression)
    {
        await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString, OrgA);
        return await ReadKeysAsync(scope, table, builtInWhere, keyExpression);
    }

    /// <summary>The keys of one org's rows, as the app role sees them under the scope's tenant setting.</summary>
    private static async Task<IReadOnlyList<string>> ReadOrgKeysAsync(
        TenantScope scope,
        string table,
        string keyExpression,
        Guid orgId) =>
        await scope.ScalarAsync<string[]>(
            $"select coalesce(array_agg({keyExpression} order by {keyExpression}), '{{}}'::text[]) from {table} where org_id = @org_id",
            ("org_id", orgId));

    /// <summary>Every key the app role can see in the table, under the scope's tenant setting.</summary>
    private static async Task<IReadOnlyList<string>> ReadAllKeysAsync(
        TenantScope scope,
        string table,
        string keyExpression) =>
        await scope.ScalarAsync<string[]>(
            $"select coalesce(array_agg({keyExpression} order by {keyExpression}), '{{}}'::text[]) from {table}");

    private static async Task<IReadOnlyList<string>> ReadKeysAsync(
        TenantScope scope,
        string table,
        string builtInWhere,
        string keyExpression) =>
        await scope.ScalarAsync<string[]>(
            $"select coalesce(array_agg({keyExpression} order by {keyExpression}), '{{}}'::text[]) from {table} where {builtInWhere}");

    private static string Keys(IReadOnlyList<string> keys) => string.Join(", ", keys);
}
