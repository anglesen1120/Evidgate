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
/// The exception list's kinds are shape-checked against <c>pg_catalog</c>, not trusted: a
/// <c>platform</c> entry must have no <c>org_id</c> column, a <c>catalogue</c> entry a nullable
/// one, and the <c>mechanism</c> kind is pinned to the three 01 tables — so a kind is a claim the
/// deployed schema verifies, not a label the suite believes.
/// </para>
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class RlsIsolationTests : HarnessTest
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
        "public.test_case",
        "public.manifest",
        "public.policy_revision",
    ];

    /// <summary>
    /// The four <c>evidgate_*</c> roles <c>migrations/0001_foundation_roles.sql</c> creates: none
    /// may carry <c>BYPASSRLS</c> or <c>SUPERUSER</c> (DECISIONS F5). The owner is included
    /// because FORCE row level security only binds a non-bypass owner — an owner with
    /// <c>BYPASSRLS</c> would read every tenant's rows through its own policy.
    /// </summary>
    private static readonly string[] EvidgateRoles = ["evidgate_owner", "evidgate_app", "evidgate_operator", "evidgate_worker_sched"];

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
    /// SELECT. Empty by construction — no migration creates a view, materialized view or foreign
    /// table (a scan of <c>migrations/</c> for <c>create view</c>/<c>create materialized view</c>/
    /// <c>create foreign table</c> returns nothing). A view added here must carry
    /// <c>security_invoker=true</c>, asserted below.
    /// </summary>
    private static readonly string[] SelectableRelationAllowlist = [];

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

        foreach (var table in scan.TenantTables)
        {
            // One transaction per table, rolled back on dispose: the factory creates the parent
            // rows it needs (the tenant root above all), and a shared transaction would both
            // re-insert the organization row and leave earlier tables' rows visible in this one.
            await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString, OrgA);
            var factory = new TenantRowFactory(scan.Schema, (sql, parameters) => scope.ExecuteAsync(sql, parameters));

            var rowA = await factory.CreateAsync(table, OrgA, Token);
            await scope.SetAsync(RlsTableCatalog.TenantSetting, OrgB.ToString("D", CultureInfo.InvariantCulture));
            var rowB = await factory.CreateAsync(table, OrgB, Token);

            var idA = (Guid)rowA.Values[TenantFkScan.TenantKeyColumn];
            var idB = (Guid)rowB.Values[TenantFkScan.TenantKeyColumn];

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

            checkedTables.Add(table);
            _output.WriteLine(
                "{0}: reset -> 0, empty -> 0, org A -> 1 ({1}); org B row {2} hidden",
                table,
                idA,
                idB);
        }

        failures.ShouldBeEmpty(
            $"the unset-GUC rule is broken for {failures.Count} tenant table(s):{Environment.NewLine}"
            + string.Join(Environment.NewLine, failures));
        checkedTables.Count.ShouldBe(scan.TenantTables.Count);
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

        foreach (var table in scan.TenantTables)
        {
            // One transaction per table (see Guc_Unset_ZeroRows).
            await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString, OrgA);
            var factory = new TenantRowFactory(scan.Schema, (sql, parameters) => scope.ExecuteAsync(sql, parameters));

            var rowA = await factory.CreateAsync(table, OrgA, Token);
            await scope.SetAsync(RlsTableCatalog.TenantSetting, OrgB.ToString("D", CultureInfo.InvariantCulture));
            var rowB = await factory.CreateAsync(table, OrgB, Token);

            var idA = (Guid)rowA.Values[TenantFkScan.TenantKeyColumn];
            var idB = (Guid)rowB.Values[TenantFkScan.TenantKeyColumn];

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

            checkedTables.Add(table);
            _output.WriteLine("{0}: org A -> {1}, org B -> {2}", table, idA, idB);
        }

        failures.ShouldBeEmpty(
            $"the set-GUC rule is broken for {failures.Count} tenant table(s):{Environment.NewLine}"
            + string.Join(Environment.NewLine, failures));
        checkedTables.Count.ShouldBe(scan.TenantTables.Count);
    }

    /// <summary>
    /// AC4 / DECISIONS F5 / 00 §5.4.7: <c>relrowsecurity</c> AND <c>relforcerowsecurity</c> are
    /// true on every business table (every table the exception list does not mark
    /// <c>mechanism</c> — platform and catalogue tables included), each carries at least one
    /// policy, and none of the four <c>evidgate_*</c> roles of 0001 carries <c>BYPASSRLS</c> or
    /// <c>SUPERUSER</c> — nor is any of them a member, directly or transitively, of a role that
    /// does. The exception list is asserted exactly: a table that is neither org-scoped nor
    /// listed fails, and each kind is shape-checked against <c>pg_catalog</c> (platform: no
    /// <c>org_id</c>; catalogue: nullable <c>org_id</c>; mechanism: pinned to the three 01
    /// tables), so a forgotten policy cannot hide behind an unlisted table and a kind cannot
    /// drift from the schema.
    /// </summary>
    [Fact]
    public async Task ForcedRls_And_NoBypassRls()
    {
        await using var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token);

        var tables = await RlsTableCatalog.ReadTablesAsync(connection, Token);
        var policies = await RlsTableCatalog.ReadPoliciesAsync(connection, Token);
        var roles = await RlsTableCatalog.ReadRolesAsync(connection, Token);
        var exceptions = RlsExceptionList.Read();

        tables.ShouldNotBeEmpty(
            "the catalogue yielded no base table: the suite would prove nothing about the migrated schema");
        policies.ShouldNotBeEmpty("the catalogue yielded no row level security policy");

        var policiesByTable = policies
            .GroupBy(policy => policy.Table, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var kindByTable = exceptions.ToDictionary(entry => entry.Table, entry => entry.Kind, StringComparer.Ordinal);

        var failures = new List<string>();
        var checkedTables = new List<string>();
        var skippedTables = new List<string>();
        var orgScopedTables = new List<string>();

        foreach (var table in tables)
        {
            var kind = kindByTable.GetValueOrDefault(table.Name);

            // A mechanism table is outside the rule set entirely (00 §5.3, 0006-0008): it carries
            // org_id and holds no policy, because the dispatcher scan and the task claim walk
            // every org by design.
            if (string.Equals(kind, "mechanism", StringComparison.Ordinal))
            {
                skippedTables.Add(table.Name);
                continue;
            }

            checkedTables.Add(table.Name);

            if (!table.RowLevelSecurityEnabled)
            {
                failures.Add($"{table.Name}: row level security is not ENABLED (DECISIONS F5)");
            }

            if (!table.RowLevelSecurityForced)
            {
                failures.Add(
                    $"{table.Name}: row level security is not FORCED — the owner would bypass its policies (DECISIONS F5)");
            }

            var tablePolicies = policiesByTable.GetValueOrDefault(table.Name) ?? [];
            if (tablePolicies.Count == 0)
            {
                failures.Add($"{table.Name}: no policy exists (pg_policy is empty for it)");
            }

            var orgScoped = tablePolicies.Any(policy =>
                RlsTableCatalog.Matches(policy, RlsTableCatalog.TenantPredicate)
                || RlsTableCatalog.Matches(policy, RlsTableCatalog.OrganizationPredicate));

            if (orgScoped)
            {
                orgScopedTables.Add(table.Name);
            }

            // Exception-list exactness: a table that is not listed must be org-scoped. A table
            // absent from both the org-scoped policy set and the exception list is exactly the
            // hole the list exists to make visible.
            if (kind is null && !orgScoped)
            {
                failures.Add(
                    $"{table.Name}: not named by {RlsExceptionList.PathInRepo} and carries no org-scoped policy "
                    + "(USING or WITH CHECK through evidgate.current_org_id()) — a table absent from both the "
                    + "org-scoped policy set and the exception list");
            }

            // A permissive policy the runtime role can use is OR-combined with its siblings, so
            // an unlisted (tenant) table is only org-scoped for a read when every such policy
            // that governs a SELECT filters through the tenant predicate in USING. A policy
            // scoped only by its WITH CHECK — `using (true) with check (org_id = …)` — leaves
            // the read open, so it is refused here; the catalogue kind of the exception list is
            // the only shape where a WITH-CHECK-only scope is accepted (02 §4.5, and
            // RlsPolicyCoverageTests.CoverageRule_CataloguePolicyScopedOnlyByItsWithCheck_IsAccepted).
            if (kind is null)
            {
                foreach (var policy in tablePolicies.Where(RlsTableCatalog.ScopesSelectForApp))
                {
                    if (!RlsTableCatalog.MatchesUsing(policy, RlsTableCatalog.TenantPredicate))
                    {
                        failures.Add(
                            $"{table.Name}: permissive policy '{policy.Name}' reaches evidgate_app for SELECT but its USING "
                            + $"is not '{RlsTableCatalog.TenantPredicate}' — permissive policies OR-combine, so the read is "
                            + "not org-scoped (a WITH CHECK alone does not scope a read)");
                    }
                }
            }
        }

        // T3/T4: the exception list's kinds carry their own shape, not just "a policy exists".
        // A catalogue table must read the platform rows for every org (02 §4.5), and the tenant
        // root must self-scope to the current org (TL T4). Both go through the same structural
        // match the boot self-check uses, so the suite cannot drift from it.
        foreach (var entry in exceptions)
        {
            if (!checkedTables.Contains(entry.Table)
                || !string.Equals(entry.Kind, "catalogue", StringComparison.Ordinal))
            {
                continue;
            }

            var cataloguePolicies = policiesByTable.GetValueOrDefault(entry.Table) ?? [];
            if (!cataloguePolicies.Any(policy => RlsTableCatalog.Matches(policy, RlsTableCatalog.CataloguePredicate)))
            {
                failures.Add(
                    $"{entry.Table}: no policy reads the platform catalogue rows for every org — expected "
                    + $"'{RlsTableCatalog.CataloguePredicate}' (02 §4.5)");
            }
        }

        var organizationPolicies = policiesByTable.GetValueOrDefault("public.organization") ?? [];
        if (!organizationPolicies.Any(policy => RlsTableCatalog.Matches(policy, RlsTableCatalog.OrganizationPredicate)))
        {
            failures.Add(
                $"public.organization: no policy matches '{RlsTableCatalog.OrganizationPredicate}' — the tenant root "
                + "must self-scope to the current org (TL T4)");
        }

        // SEC4: the exception list's kinds are claims about the deployed schema, not trusted
        // labels. A `platform` entry must have no org_id column at all, a `catalogue` entry must
        // carry a nullable org_id, and the `mechanism` kind is pinned to the three 01 tables — so
        // a kind that drifts from the schema, or a fourth mechanism table, fails here by name.
        var orgIdNullability = await ReadOrgIdNullabilityAsync(connection, Token);
        var existingTables = tables.Select(table => table.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var entry in exceptions)
        {
            if (!existingTables.Contains(entry.Table))
            {
                continue; // inert until the migration that creates it lands (contracts/rls-exceptions.txt:11-12)
            }

            if (string.Equals(entry.Kind, "platform", StringComparison.Ordinal)
                && orgIdNullability.ContainsKey(entry.Table))
            {
                failures.Add(
                    $"{entry.Table}: listed as 'platform' but carries an org_id column — a platform table is "
                    + "outside the org scope and must have no org_id (contracts/rls-exceptions.txt:20-25)");
            }

            if (string.Equals(entry.Kind, "catalogue", StringComparison.Ordinal)
                && (!orgIdNullability.TryGetValue(entry.Table, out var orgIdNotNull) || orgIdNotNull))
            {
                failures.Add(
                    $"{entry.Table}: listed as 'catalogue' but its org_id is "
                    + (orgIdNullability.ContainsKey(entry.Table) ? "NOT NULL" : "absent")
                    + " — a catalogue table carries a nullable org_id (built-in rows are NULL) "
                    + "(contracts/rls-exceptions.txt:26-30)");
            }
        }

        var mechanismEntries = exceptions
            .Where(entry => string.Equals(entry.Kind, "mechanism", StringComparison.Ordinal))
            .Select(entry => entry.Table)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var extra in mechanismEntries.Except(PinnedMechanismTables, StringComparer.Ordinal))
        {
            failures.Add(
                $"{extra}: listed as 'mechanism' but is not one of the three 01 mechanism tables "
                + $"[{string.Join(", ", PinnedMechanismTables)}] — the mechanism kind is pinned "
                + "(contracts/rls-exceptions.txt:31-36)");
        }

        foreach (var missing in PinnedMechanismTables.Except(mechanismEntries, StringComparer.Ordinal))
        {
            failures.Add(
                $"{missing}: the pinned mechanism table is not listed as 'mechanism' in {RlsExceptionList.PathInRepo}");
        }

        foreach (var roleName in EvidgateRoles)
        {
            var role = roles.SingleOrDefault(candidate => string.Equals(candidate.Name, roleName, StringComparison.Ordinal));
            if (role is null)
            {
                failures.Add($"{roleName}: the role does not exist in pg_roles (0001 creates it)");
                continue;
            }

            if (role.BypassRls)
            {
                failures.Add($"{roleName}: rolbypassrls is true — the role would skip every policy (DECISIONS F5)");
            }

            if (role.Superuser)
            {
                failures.Add($"{roleName}: rolsuper is true — a superuser skips every policy (DECISIONS F5)");
            }
        }

        // SEC1: a membership is the other way a policy is defeated — a role that inherits a
        // bypass role skips every policy even though its own rolbypassrls is false
        // (0001_foundation_roles.sql:42-79). The Testcontainers bootstrap superuser is not an
        // evidgate_* role, so it is out of scope by construction.
        (await CountBypassRolesAsync(connection, Token)).ShouldBeGreaterThan(
            0,
            "no role carries SUPERUSER or BYPASSRLS: the membership check would be vacuous");

        foreach (var reach in await ReadBypassRoleReachAsync(connection, Token))
        {
            failures.Add(
                $"{reach}: an evidgate role is a member (directly or transitively) of a role with SUPERUSER or "
                + "BYPASSRLS — the membership defeats every policy (DECISIONS F5)");
        }

        // The scan is not vacuous: the M1 business tables were judged, the platform and catalogue
        // tables were judged with them, and the mechanism tables were skipped — which is how the
        // exception list, not a hard-coded name, decides who is exempt.
        checkedTables.ShouldNotBeEmpty("no business table was judged");
        skippedTables.ShouldNotBeEmpty(
            $"no mechanism table was skipped: {RlsExceptionList.PathInRepo} must name the 01 mechanism tables");
        orgScopedTables.ShouldNotBeEmpty("no table carries an org-scoped policy");
        foreach (var floor in FloorTables)
        {
            checkedTables.ShouldContain(floor, $"the M1 business table {floor} was not judged");
        }

        checkedTables.ShouldContain("public.organization", "the platform tenant root must be judged");
        checkedTables.ShouldContain("public.role", "the catalogue tables must be judged");
        skippedTables.ShouldContain("public.schema_migration", "the migration journal is a mechanism table");

        failures.ShouldBeEmpty(
            $"the row level security posture of the migrated schema is broken:{Environment.NewLine}"
            + string.Join(Environment.NewLine, failures));

        foreach (var table in tables)
        {
            _output.WriteLine(
                "{0}: rls={1} force={2} org_id={3} policies={4} kind={5}",
                table.Name,
                table.RowLevelSecurityEnabled,
                table.RowLevelSecurityForced,
                table.HasOrgId,
                (policiesByTable.GetValueOrDefault(table.Name) ?? []).Count,
                kindByTable.GetValueOrDefault(table.Name) ?? "tenant");
        }
    }

    /// <summary>
    /// SEC2 / SEC6 (02.9.1-T3, 00 §5.4.3 least privilege): the runtime roles hold no privilege
    /// that widens the tenant boundary beyond DML. <c>evidgate_app</c> and <c>evidgate_operator</c>
    /// hold neither <c>TRUNCATE</c>, <c>REFERENCES</c> nor <c>TRIGGER</c> on any base table, and no
    /// view, materialized view or foreign table is SELECTable by <c>evidgate_app</c> — a relation
    /// that reads across the tenant boundary without a policy is a hole the table scan cannot see.
    /// </summary>
    [Fact]
    public async Task RoleAndPrivilege_Hardening()
    {
        await using var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token);

        var tables = await RlsTableCatalog.ReadTablesAsync(connection, Token);
        tables.ShouldNotBeEmpty(
            "the catalogue yielded no base table: the privilege scan would prove nothing about the migrated schema");

        var failures = new List<string>();

        var violations = await ReadTablePrivilegeViolationsAsync(connection, Token);
        foreach (var (table, role, privilege) in violations)
        {
            if (TablePrivilegeAllowlist.Contains((table, role, privilege)))
            {
                continue;
            }

            failures.Add(
                $"{table}: {role} holds {privilege} — the runtime roles must hold no privilege beyond DML "
                + "(00 §5.4.3); a legitimate exception must be allowlisted with its migration citation");
        }

        var relations = await ReadRelationFactsAsync(connection, Token);
        relations.ShouldContain(
            facts => facts.Kind is "r" or "p",
            "the relation scan found no base table: the view/foreign-table scan would prove nothing about the migrated schema");

        foreach (var relation in relations.Where(facts => facts.Kind is "v" or "m" or "f"))
        {
            if (SelectableRelationAllowlist.Contains(relation.Name, StringComparer.Ordinal))
            {
                if (string.Equals(relation.Kind, "v", StringComparison.Ordinal)
                    && !relation.Options.Contains("security_invoker=true", StringComparison.Ordinal))
                {
                    failures.Add(
                        $"{relation.Name}: allowlisted view must carry security_invoker=true so it reads with the "
                        + "caller's privileges, not the view owner's");
                }

                continue;
            }

            if (relation.TableSelect || relation.ColumnSelect)
            {
                failures.Add(
                    $"{relation.Name}: relation of kind '{relation.Kind}' is SELECTable by evidgate_app — a view, "
                    + "materialized view or foreign table bypasses the table-level policy scan (00 §5.4.7)");
            }
        }

        failures.ShouldBeEmpty(
            $"the runtime roles hold a privilege that widens the tenant boundary:{Environment.NewLine}"
            + string.Join(Environment.NewLine, failures));

        _output.WriteLine(
            "privilege scan: {0} base table(s), {1} relation(s) of every kind, {2} violation(s)",
            tables.Count,
            relations.Count,
            violations.Count);
    }

    /// <summary>One relation of the live catalogue, with the facts the SEC6 scan needs.</summary>
    private sealed record RelationFacts(string Name, string Kind, string Options, bool TableSelect, bool ColumnSelect);

    /// <summary>
    /// The tenant set of the live database, discovered exactly as the composite-FK suite discovers
    /// it, with the floor asserted so a broken discovery fails here rather than passing vacuously.
    /// </summary>
    private async Task<TenantFkScanResult> DiscoverAsync()
    {
        await using var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token);
        var scan = await TenantFkScan.RunAsync(connection, Token);

        scan.Exceptions.ShouldNotBeEmpty(
            $"{RlsExceptionList.PathInRepo} listed no table — the suite cannot derive the tenant set without it");
        scan.TenantTables.ShouldNotBeEmpty(
            "the catalogue yielded no tenant table (a table with org_id minus the exception list): the suite would prove nothing");
        foreach (var floor in FloorTables)
        {
            scan.TenantTables.ShouldContain(
                floor,
                $"the M1 business table {floor} is missing from the discovered tenant set [{string.Join(", ", scan.TenantTables)}]");
        }

        return scan;
    }

    /// <summary>
    /// AC6 / 00 §5.7 S-suite: the suite runs as <c>evidgate_app</c>, never the owner, and the role
    /// carries no <c>BYPASSRLS</c> — otherwise every per-table case below could pass for the wrong
    /// reason.
    /// </summary>
    private async Task AssertAppRoleAsync()
    {
        await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString);

        (await scope.ScalarAsync<string>("select current_user"))
            .ShouldBe("evidgate_app", "the isolation suite must run as the application role, never the owner");
        (await scope.ScalarAsync<bool>("select rolbypassrls from pg_catalog.pg_roles where rolname = current_user"))
            .ShouldBeFalse("evidgate_app must not carry BYPASSRLS (DECISIONS F5)");
    }

    /// <summary>The ids of one table visible to the scope's current tenant setting.</summary>
    private static async Task<IReadOnlyList<Guid>> VisibleIdsAsync(TenantScope scope, string table) =>
        await scope.ScalarAsync<Guid[]>($"select coalesce(array_agg(id order by id), '{{}}'::uuid[]) from {table}");

    private static string Ids(IReadOnlyList<Guid> ids) =>
        string.Join(", ", ids.Select(id => id.ToString("D", CultureInfo.InvariantCulture)));

    /// <summary>
    /// The <c>org_id</c> nullability of every base table that carries the column, keyed by
    /// <c>schema.table</c>; the value is <c>true</c> when the column is NOT NULL. A table without
    /// the column is absent. Read from <c>pg_attribute</c>, so the exception list's kinds are
    /// checked against the deployed schema rather than trusted.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, bool>> ReadOrgIdNullabilityAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select n.nspname || '.' || c.relname,
                   a.attnotnull
              from pg_catalog.pg_class c
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
              join pg_catalog.pg_attribute a on a.attrelid = c.oid
             where c.relkind in ('r', 'p')
               and n.nspname <> 'information_schema'
               and n.nspname not like 'pg\_%'
               and a.attname = 'org_id'
               and a.attnum > 0
               and not a.attisdropped
            """;

        var nullability = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            nullability[reader.GetString(0)] = reader.GetBoolean(1);
        }

        return nullability;
    }

    /// <summary>
    /// Every <c>evidgate_*</c> role that is a member, directly or transitively, of a role carrying
    /// <c>SUPERUSER</c> or <c>BYPASSRLS</c>, as <c>member -&gt; target</c>. The recursion walks
    /// <c>pg_auth_members</c> so a two-hop membership cannot hide behind a role whose own
    /// <c>rolbypassrls</c> is false (0001_foundation_roles.sql:42-79).
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadBypassRoleReachAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            with recursive reach(roleid, member) as (
              select m.roleid, m.member from pg_catalog.pg_auth_members m
              union
              select r.roleid, a.member
                from pg_catalog.pg_auth_members r
                join reach a on a.roleid = r.member
            )
            select distinct mem.rolname || ' -> ' || tgt.rolname
              from reach
              join pg_catalog.pg_roles mem on mem.oid = reach.member
              join pg_catalog.pg_roles tgt on tgt.oid = reach.roleid
             where mem.rolname like 'evidgate\_%'
               and (tgt.rolsuper or tgt.rolbypassrls)
             order by 1
            """;

        var reach = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            reach.Add(reader.GetString(0));
        }

        return reach;
    }

    /// <summary>The number of roles that carry <c>SUPERUSER</c> or <c>BYPASSRLS</c> — the non-vacuity guard of the membership scan.</summary>
    private static async Task<int> CountBypassRolesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*)::int from pg_catalog.pg_roles where rolsuper or rolbypassrls";
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// SEC2: every (base table, runtime role, privilege) triple where the role holds
    /// <c>TRUNCATE</c>, <c>REFERENCES</c> or <c>TRIGGER</c>. The scan is catalogue-driven, so a
    /// table added by a later migration is covered the moment it lands.
    /// </summary>
    private static async Task<IReadOnlyList<(string Table, string Role, string Privilege)>> ReadTablePrivilegeViolationsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select n.nspname || '.' || c.relname,
                   r.role_name,
                   p.privilege
              from pg_catalog.pg_class c
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
              cross join (values ('evidgate_app'), ('evidgate_operator')) as r(role_name)
              cross join (values ('TRUNCATE'), ('REFERENCES'), ('TRIGGER')) as p(privilege)
             where c.relkind in ('r', 'p')
               and n.nspname <> 'information_schema'
               and n.nspname not like 'pg\_%'
               and has_table_privilege(r.role_name::name, c.oid, p.privilege)
             order by 1, 2, 3
            """;

        var violations = new List<(string Table, string Role, string Privilege)>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            violations.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return violations;
    }

    /// <summary>
    /// SEC6: every relation of a non-system schema with its kind, its <c>reloptions</c> and
    /// whether <c>evidgate_app</c> can SELECT it (table-level, or any column for a view or
    /// foreign table). The scan is catalogue-driven, so a view added by a later migration is
    /// covered the moment it lands.
    /// </summary>
    private static async Task<IReadOnlyList<RelationFacts>> ReadRelationFactsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select n.nspname || '.' || c.relname,
                   c.relkind::text,
                   coalesce(array_to_string(c.reloptions, ','), ''),
                   has_table_privilege('evidgate_app', c.oid, 'SELECT'),
                   case when c.relkind in ('v', 'f')
                        then has_any_column_privilege('evidgate_app', c.oid, 'SELECT')
                        else false
                   end
              from pg_catalog.pg_class c
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
             where c.relkind in ('r', 'p', 'v', 'm', 'f')
               and n.nspname <> 'information_schema'
               and n.nspname not like 'pg\_%'
             order by 1
            """;

        var relations = new List<RelationFacts>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            relations.Add(new RelationFacts(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4)));
        }

        return relations;
    }
}
