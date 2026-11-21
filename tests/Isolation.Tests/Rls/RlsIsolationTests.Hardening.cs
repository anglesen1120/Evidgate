using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

public sealed partial class RlsIsolationTests
{
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
        relations.Any(facts => facts.Kind is "r" or "p").ShouldBeTrue(
            "the relation scan found no base table: the view/foreign-table scan would prove nothing about the migrated schema");

        foreach (var relation in relations.Where(facts => facts.Kind is "v" or "m" or "f"))
        {
            if (SelectableRelationAllowlist.Contains(relation.Name, StringComparer.Ordinal))
            {
                if (string.Equals(relation.Kind, "v", StringComparison.Ordinal)
                    && !relation.Options.Contains("security_barrier=true", StringComparison.Ordinal))
                {
                    failures.Add(
                        $"{relation.Name}: allowlisted view must carry security_barrier=true so its tenant "
                        + "predicate cannot be reordered around");
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
}
