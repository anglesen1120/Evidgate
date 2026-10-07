using System.Globalization;
using Evidgate.Testing;
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

    /// <summary>The two runtime roles AC4 names: neither may carry <c>BYPASSRLS</c> or <c>SUPERUSER</c> (DECISIONS F5).</summary>
    private static readonly string[] RuntimeRoles = ["evidgate_app", "evidgate_operator"];

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
    /// policy, and neither <c>evidgate_app</c> nor <c>evidgate_operator</c> carries
    /// <c>BYPASSRLS</c> or <c>SUPERUSER</c>. The exception list is asserted exactly: a table that
    /// is neither org-scoped nor listed fails, so a forgotten policy cannot hide behind an
    /// unlisted table.
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

        foreach (var roleName in RuntimeRoles)
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
}
