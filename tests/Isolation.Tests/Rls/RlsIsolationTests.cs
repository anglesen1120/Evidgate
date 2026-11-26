using System.Globalization;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// 02.9.1-T3.1 (story 02.9.1 §2 AC3/AC4, §5, §8 FR-02-11; 00 §5.4.1/§5.4.6/§5.4.7, DECISIONS F5):
/// the row level security layer of tenant isolation, asserted against the live catalogue and the
/// real database — not against a list inside the test.
/// <para>
/// The tenant set is the same auto-discovery the composite-FK suite uses
/// (<see cref="TenantFkScan"/>): every table in the catalogue carrying <c>org_id</c>, minus the
/// tables <c>contracts/rls-exceptions.txt</c> names (02.6.1-T1.4). A table added by a later
/// migration is therefore covered the moment it lands, and one that breaks a rule fails this
/// suite with its own name in the message.
/// </para>
/// <para>
/// Every read and every insert goes through <c>evidgate_app</c> inside a transaction that is
/// rolled back (00 §5.5 C3, §5.7 S-suite): with FORCE row level security the owner's policy is
/// permissive, so an owner read would prove nothing about the path the API uses. The setup
/// assertion pins the role, so a fixture that ever connected as the owner fails loudly instead of
/// turning the suite into a false pass.
/// </para>
/// <para>
/// One seeding exception, and it is a grant fact rather than a shortcut: the tables in
/// <see cref="TenantRowFactory.OwnerProvisionedTables"/> cannot be inserted by the app role at all
/// (<c>0054</c> keeps both NOT NULL caps of <c>healing_llm_budget</c> outside its INSERT grant), so
/// their rows are provisioned on the owner connection, committed (the app-role read below is a
/// different transaction) and removed again before the case ends. Every read still runs as
/// <c>evidgate_app</c>.
/// </para>
/// <para>
/// The exception list's kinds are shape-checked against <c>pg_catalog</c>, not trusted: a
/// <c>platform</c> entry must have no <c>org_id</c> column, a <c>catalogue</c> entry a nullable
/// one, and the <c>mechanism</c> kind is pinned to the three 01 tables — so a kind is a claim the
/// deployed schema verifies, not a label the suite believes.
/// </para>
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed partial class RlsIsolationTests : HarnessTest
{
    /// <summary>Two orgs, as in the rest of the isolation suite; the ids are the organizations' own ids (TL T4).</summary>
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OrgB = Guid.Parse("22222222-2222-7222-8222-222222222222");

    /// <summary>
    /// The M1 business tables the discovery must yield. A floor, not the tenant set: it makes a
    /// broken discovery (an empty <c>information_schema</c> read, a wrong exception subtraction)
    /// fail here instead of letting every per-table case pass vacuously.
    /// </summary>
    private static readonly string[] FloorTables =
    [
        "public.project",
        "public.membership",
        "public.audit_log",
        "public.ci_binding",
        "public.ingest_token",
        "public.api_token",
        "public.test_case",
        "public.manifest",
        "public.policy_revision",
        "public.evaluation",
    ];

    /// <summary>
    /// The four <c>evidgate_*</c> roles <c>migrations/0001_foundation_roles.sql</c> creates: none
    /// may carry <c>BYPASSRLS</c> or <c>SUPERUSER</c> (DECISIONS F5). The owner is included
    /// because FORCE row level security only binds a non-bypass owner — an owner with
    /// <c>BYPASSRLS</c> would read every tenant's rows through its own policy.
    /// </summary>
    private static readonly string[] EvidgateRoles = ["evidgate_owner", "evidgate_app", "evidgate_operator", "evidgate_worker", "evidgate_worker_sched"];

    /// <summary>
    /// The three 01 mechanism tables the exception list's <c>mechanism</c> kind is pinned to
    /// (00 §5.3, migrations 0006-0008). A fourth mechanism entry is a review decision, not a
    /// silent exemption, so it fails the suite by name.
    /// </summary>
    private static readonly string[] PinnedMechanismTables = ["public.schema_migration", "public.outbox", "public.task"];

    /// <summary>
    /// SEC2: base-table privileges the runtime roles may legitimately hold beyond DML. Empty by
    /// construction — <c>0001_foundation_roles.sql:90-95</c> grants <c>evidgate_app</c> only
    /// SELECT/INSERT/UPDATE/DELETE and <c>evidgate_operator</c> only SELECT, and
    /// <c>0001_foundation_roles.sql:112-113</c> revokes TRUNCATE/REFERENCES/TRIGGER on the journal,
    /// so no grant of TRUNCATE, REFERENCES or TRIGGER exists anywhere in <c>migrations/</c>. An
    /// entry added here must cite the migration that grants it.
    /// </summary>
    private static readonly (string Table, string Role, string Privilege)[] TablePrivilegeAllowlist = [];

    /// <summary>
    /// SEC6: relations of kind view/materialized view/foreign table that <c>evidgate_app</c> may
    /// SELECT. The queue's scoped views are the only entries — the task, the outbox, and the keyed
    /// join of the two: they replace the app role's lost read of the mechanism tables, are owned by
    /// the migration role and carry <c>security_barrier</c> with an explicit
    /// <c>org_id = evidgate.current_org_id()</c> predicate (proven behaviourally by the worker-role
    /// grant suite). A view added here must carry <c>security_barrier=true</c>, asserted below.
    /// </summary>
    private static readonly string[] SelectableRelationAllowlist = ["public.task_in_scope", "public.outbox_in_scope", "public.outbox_task_in_scope"];

    private readonly ITestOutputHelper _output;

    /// <summary>Binds the test to the shared harness fixture (container, migrations, Respawn).</summary>
    public RlsIsolationTests(DatabaseFixture fixture, ITestOutputHelper output)
        : base(fixture)
    {
        _output = output;
    }

    /// <summary>
    /// AC3 (RLS half) / FR-02-11: for every tenant table, a read as <c>evidgate_app</c> with no
    /// tenant context returns zero rows — never an error and never every row — while the same
    /// table holds rows for two orgs that are visible under their own tenant setting. The
    /// non-vacuity half is load-bearing: the zero is asserted only after the seeded row has been
    /// shown visible under org A, so an empty table cannot pass the case.
    /// </summary>
    [Fact]
    public async Task Guc_Unset_ZeroRows()
    {
        var scan = await DiscoverAsync();
        await AssertAppRoleAsync();

        var failures = new List<string>();
        var checkedTables = new List<string>();

        foreach (var table in AppSeedableTables(scan))
        {
            // One transaction per table, rolled back on dispose: the factory creates the parent
            // rows it needs (the tenant root above all), and a shared transaction would both
            // re-insert the organization row and leave earlier tables' rows visible in this one.
            await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString, OrgA);
            var factory = new TenantRowFactory(scan.Schema, (sql, parameters) => scope.ExecuteAsync(sql, parameters));

            var rowA = await factory.CreateAsync(table, OrgA, Token);
            await scope.SetAsync(RlsTableCatalog.TenantSetting, OrgB.ToString("D", CultureInfo.InvariantCulture));
            var rowB = await factory.CreateAsync(table, OrgB, Token);

            failures.AddRange(await CheckUnsetThenScopedAsync(scope, table, IdOf(rowA), IdOf(rowB)));
            checkedTables.Add(table);
        }

        // The owner-provisioned tables are seeded last: theirs is the only transaction that commits, and
        // the organization/project rows it creates would collide with the inserts of the app-role loops
        // above if it ran first. The reads below — the only thing this case proves — are the same
        // evidgate_app scope as every other table's, so the case still proves what it names.
        foreach (var table in OwnerProvisionedTables(scan))
        {
            var (idA, idB, cleanup) = await SeedAsOwnerAsync(scan, table);
            await using (cleanup)
            {
                await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString, OrgA);
                failures.AddRange(await CheckUnsetThenScopedAsync(scope, table, idA, idB));
            }

            checkedTables.Add(table);
        }

        failures.ShouldBeEmpty(
            $"the unset-GUC rule is broken for {failures.Count} tenant table(s):{Environment.NewLine}"
            + string.Join(Environment.NewLine, failures));
        checkedTables.Count.ShouldBe(
            scan.TenantTables.Count(table => !TenantRowFactory.TriggerWrittenTables.Contains(table)));
    }

    /// <summary>
    /// The "unset then scoped" half of AC3 for one table and the row it has for each org: zero rows
    /// with the tenant setting reset, zero with it blank, then exactly the org-A row under org A and the
    /// org-B row hidden.
    /// </summary>
    private async Task<IReadOnlyList<string>> CheckUnsetThenScopedAsync(TenantScope scope, string table, Guid idA, Guid idB)
    {
        var failures = new List<string>();

        // 1. The setting reset to its default: the fail-closed "no tenant" read.
        await RlsTableCatalog.ResetTenantAsync(scope);
        var unset = await VisibleIdsAsync(scope, table);
        if (unset.Count != 0)
        {
            failures.Add(
                $"{table}: {unset.Count} row(s) visible with the tenant setting reset to its default "
                + $"(expected 0) — the policy must scope every read through evidgate.current_org_id()");
        }

        // 2. The setting present but blank: NULLIF(…, '') must map it to NULL, not to a tenant.
        await scope.SetAsync(RlsTableCatalog.TenantSetting, string.Empty);
        var empty = await VisibleIdsAsync(scope, table);
        if (empty.Count != 0)
        {
            failures.Add(
                $"{table}: {empty.Count} row(s) visible with an empty tenant setting (expected 0) — "
                + "a blank setting must not open the scope");
        }

        // 3. Non-vacuity: the seeded org-A row is visible under org A, and org B's row is not.
        await scope.SetAsync(RlsTableCatalog.TenantSetting, OrgA.ToString("D", CultureInfo.InvariantCulture));
        var scoped = await VisibleIdsAsync(scope, table);
        if (!scoped.SequenceEqual([idA]))
        {
            failures.Add(
                $"{table}: with the tenant setting at org {OrgA} the visible ids were [{Ids(scoped)}], "
                + $"expected exactly the seeded org-A row {idA} — the zero above is only meaningful when the row exists");
        }

        if (scoped.Contains(idB))
        {
            failures.Add($"{table}: org {OrgB}'s row {idB} is visible under the tenant setting of org {OrgA}");
        }

        _output.WriteLine(
            "{0}: reset -> 0, empty -> 0, org A -> 1 ({1}); org B row {2} hidden",
            table,
            idA,
            idB);

        return failures;
    }

    /// <summary>
    /// AC3 (RLS half): for every tenant table, a read as <c>evidgate_app</c> under one org's
    /// tenant setting returns exactly that org's rows and none of the other org's — the
    /// "set → exact" half of FR-02-11, per table, never sampled.
    /// </summary>
    [Fact]
    public async Task Guc_Set_OwnRowsOnly()
    {
        var scan = await DiscoverAsync();
        await AssertAppRoleAsync();

        var failures = new List<string>();
        var checkedTables = new List<string>();

        foreach (var table in AppSeedableTables(scan))
        {
            // One transaction per table (see Guc_Unset_ZeroRows).
            await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString, OrgA);
            var factory = new TenantRowFactory(scan.Schema, (sql, parameters) => scope.ExecuteAsync(sql, parameters));

            var rowA = await factory.CreateAsync(table, OrgA, Token);
            await scope.SetAsync(RlsTableCatalog.TenantSetting, OrgB.ToString("D", CultureInfo.InvariantCulture));
            var rowB = await factory.CreateAsync(table, OrgB, Token);

            failures.AddRange(await CheckScopedExactlyAsync(scope, table, IdOf(rowA), IdOf(rowB)));
            checkedTables.Add(table);
        }

        // See Guc_Unset_ZeroRows for why the owner-provisioned tables are seeded last, on the owner
        // connection, and only for the seeding half.
        foreach (var table in OwnerProvisionedTables(scan))
        {
            var (idA, idB, cleanup) = await SeedAsOwnerAsync(scan, table);
            await using (cleanup)
            {
                await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString, OrgA);
                failures.AddRange(await CheckScopedExactlyAsync(scope, table, idA, idB));
            }

            checkedTables.Add(table);
        }

        failures.ShouldBeEmpty(
            $"the set-GUC rule is broken for {failures.Count} tenant table(s):{Environment.NewLine}"
            + string.Join(Environment.NewLine, failures));

        // Every tenant table is covered once: by a generated row here, or — for a trigger-written table with no
        // generated row to seed — by the trigger/test that owns it. Asserting both sides keeps an exclusion from
        // silently dropping a table out of the scan.
        foreach (var triggerWritten in TenantRowFactory.TriggerWrittenTables)
        {
            scan.TenantTables.ShouldContain(
                triggerWritten,
                $"{triggerWritten} is excluded from this suite as trigger-written but is not a discovered tenant table");
        }

        checkedTables.Count.ShouldBe(
            scan.TenantTables.Count(table => !TenantRowFactory.TriggerWrittenTables.Contains(table)));
    }

    /// <summary>
    /// The "set → exact" half of AC3 for one table and the row it has for each org: org A's setting
    /// returns exactly the org-A row and org B's exactly the org-B row.
    /// </summary>
    private async Task<IReadOnlyList<string>> CheckScopedExactlyAsync(TenantScope scope, string table, Guid idA, Guid idB)
    {
        var failures = new List<string>();

        await scope.SetAsync(RlsTableCatalog.TenantSetting, OrgA.ToString("D", CultureInfo.InvariantCulture));
        var underA = await VisibleIdsAsync(scope, table);
        if (!underA.SequenceEqual([idA]))
        {
            failures.Add(
                $"{table}: with the tenant setting at org {OrgA} the visible ids were [{Ids(underA)}], "
                + $"expected exactly [{idA}] (org {OrgB}'s row {idB} must not appear)");
        }

        await scope.SetAsync(RlsTableCatalog.TenantSetting, OrgB.ToString("D", CultureInfo.InvariantCulture));
        var underB = await VisibleIdsAsync(scope, table);
        if (!underB.SequenceEqual([idB]))
        {
            failures.Add(
                $"{table}: with the tenant setting at org {OrgB} the visible ids were [{Ids(underB)}], "
                + $"expected exactly [{idB}] (org {OrgA}'s row {idA} must not appear)");
        }

        _output.WriteLine("{0}: org A -> {1}, org B -> {2}", table, idA, idB);
        return failures;
    }

    /// <summary>The discovered tenant tables the app role can insert, in discovery order.</summary>
    private static IEnumerable<string> AppSeedableTables(TenantFkScanResult scan) =>
        scan.TenantTables.Where(table =>
            !TenantRowFactory.OwnerProvisionedTables.Contains(table)
            && !TenantRowFactory.TriggerWrittenTables.Contains(table));

    /// <summary>
    /// The discovered tenant tables only the owner can provision
    /// (<see cref="TenantRowFactory.OwnerProvisionedTables"/>), in discovery order.
    /// </summary>
    private static IEnumerable<string> OwnerProvisionedTables(TenantFkScanResult scan) =>
        scan.TenantTables.Where(TenantRowFactory.OwnerProvisionedTables.Contains);

    /// <summary>The row's own id — the generated tenant key column.</summary>
    private static Guid IdOf(GeneratedRow row) => (Guid)row.Values[TenantFkScan.TenantKeyColumn];

    /// <summary>
    /// Seeds one row per org for an owner-provisioned table, on the owner connection, and commits it:
    /// the case's read half is a separate <c>evidgate_app</c> transaction, so an uncommitted seed would
    /// be invisible. Returns the cleanup that removes the rows (and the organization/project rows the
    /// generator created with them) once the read assertions have run.
    /// </summary>
    private async Task<(Guid IdA, Guid IdB, IAsyncDisposable Cleanup)> SeedAsOwnerAsync(
        TenantFkScanResult scan,
        string table)
    {
        Guid idA, idB;
        Guid[] projects;
        Guid[] users;
        await using (var owner = await TenantScope.BeginAsync(Fixture.OwnerConnectionString))
        {
            var factory = new TenantRowFactory(scan.Schema, (sql, parameters) => owner.ExecuteAsync(sql, parameters));
            var rowA = await factory.CreateAsync(table, OrgA, Token);
            var rowB = await factory.CreateAsync(table, OrgB, Token);

            idA = IdOf(rowA);
            idB = IdOf(rowB);
            projects = SeededIds(rowA, rowB, "project_id");
            // A table whose row needs a platform identity parent (TenantRowFactory.OwnerProvisionedTables) gets one
            // generated per org; the cleanup removes them with the rows so the shared database is left as found.
            users = [.. SeededIds(rowA, rowB, "user_id"), .. SeededIds(rowA, rowB, "operator_user_id")];
            await owner.CommitAsync();
        }

        return (idA, idB, new SeededRowCleanup(Fixture.OwnerConnectionString, table, [idA, idB], projects, users, [OrgA, OrgB]));
    }

    /// <summary>The values both generated rows carry for one parent column; empty when the table has no such column.</summary>
    private static Guid[] SeededIds(GeneratedRow rowA, GeneratedRow rowB, string column) =>
    [
        .. new[] { rowA, rowB }
            .Where(row => row.Values.ContainsKey(column))
            .Select(row => (Guid)row.Values[column])
            .Distinct(),
    ];

    /// <summary>
    /// Removes a committed owner seed child first — the seeded rows, their platform parent, their projects, then
    /// the two organization roots the generator created — so the case leaves the shared harness database as it
    /// found it and the later <c>organization</c> assertions see only the rows they expect.
    /// </summary>
    private sealed class SeededRowCleanup(
        string connectionString,
        string table,
        Guid[] rowIds,
        Guid[] projectIds,
        Guid[] userIds,
        Guid[] orgIds) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using var owner = await TenantScope.BeginAsync(connectionString);

            // A support grant is never deleted (its guard refuses it for every role), so the seed's own table is
            // cleared with the guard trigger off for this one transaction; a failure rolls the change back.
            var guarded = string.Equals(table, "public.support_grant", StringComparison.Ordinal);
            if (guarded)
            {
                await owner.ExecuteAsync("alter table public.support_grant disable trigger support_grant_guard");
            }

            await owner.ExecuteAsync($"delete from {table} where id = any(@ids)", ("ids", rowIds));
            if (guarded)
            {
                await owner.ExecuteAsync("alter table public.support_grant enable trigger support_grant_guard");
            }

            if (userIds.Length > 0)
            {
                await owner.ExecuteAsync("delete from public.\"user\" where id = any(@ids)", ("ids", userIds));
            }

            await owner.ExecuteAsync("delete from public.project where id = any(@ids)", ("ids", projectIds));
            await owner.ExecuteAsync("delete from public.organization where id = any(@ids)", ("ids", orgIds));
            await owner.CommitAsync();
        }
    }
}
