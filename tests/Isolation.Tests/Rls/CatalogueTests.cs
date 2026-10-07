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

        failures.ShouldBeEmpty(
            $"the catalogue is not read-only for the application role:{Environment.NewLine}"
            + string.Join(Environment.NewLine, failures));
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

        await AssertUnchangedAsync(table, "true", "code", before, failures);
        _output.WriteLine("{0}: insert/update/delete refused; {1} built-in row(s) unchanged", table, before.Count);
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

        await AssertUnchangedAsync(table, "org_id is null", "id::text", before, failures);
        _output.WriteLine("{0}: insert/update/delete refused; {1} built-in row(s) unchanged", table, before.Count);
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

        await AssertUnchangedAsync(table, "org_id is null", "id::text", before, failures);
        _output.WriteLine("{0}: insert/update/delete refused; {1} built-in row(s) unchanged", table, before.Count);
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

    private static async Task<IReadOnlyList<string>> ReadKeysAsync(
        TenantScope scope,
        string table,
        string builtInWhere,
        string keyExpression) =>
        await scope.ScalarAsync<string[]>(
            $"select coalesce(array_agg({keyExpression} order by {keyExpression}), '{{}}'::text[]) from {table} where {builtInWhere}");

    private static string Keys(IReadOnlyList<string> keys) => string.Join(", ", keys);
}
