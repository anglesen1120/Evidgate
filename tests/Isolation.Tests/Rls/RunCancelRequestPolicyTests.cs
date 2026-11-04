using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// 05.4.1-T1 (00 §5.4.1/§5.4.7, plan §4/§8): <c>run_cancel_request</c> is a tenant table like every other
/// module table — ENABLE and FORCE row level security, one permissive policy that reaches only
/// <c>evidgate_app</c> and carries the tenant predicate in both clauses, and the migration role's own policy
/// because FORCE binds the owner too.
/// <para>
/// The generic <see cref="RlsIsolationTests"/> discovery already probes the row-level behaviour of every tenant
/// table; this suite pins the two facts its discovery cannot name for one table: which role the policy targets
/// (a <c>TO public</c> policy reaches every tenant) and which privileges the write path needs.
/// </para>
/// </summary>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class RunCancelRequestPolicyTests : HarnessTest
{
    private const string Table = "public.run_cancel_request";
    private const string OwnerRole = "evidgate_owner";
    private const string OperatorRole = "evidgate_operator";
    private const string SchedulerRole = "evidgate_worker_sched";

    public RunCancelRequestPolicyTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// 00 §5.4.1/§5.4.6: the table exists, carries <c>org_id</c>, and has row level security both enabled and
    /// forced — without FORCE the migration role's own reads would silently bypass the tenant predicate.
    /// </summary>
    [Fact]
    public async Task RunCancelRequest_ForcesRowLevelSecurityOnATenantTable()
    {
        await using var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token);

        (await RlsTableCatalog.TableExistsAsync(connection, Table, Token)).ShouldBeTrue(
            $"{Table} is the durable cancel request record the cancel route writes");

        var table = (await RlsTableCatalog.ReadTablesAsync(connection, Token))
            .Single(candidate => string.Equals(candidate.Name, Table, StringComparison.Ordinal));

        table.HasOrgId.ShouldBeTrue("the tenant of a cancel request is the run's org");
        table.RowLevelSecurityEnabled.ShouldBeTrue($"{Table} must enable row level security");
        table.RowLevelSecurityForced.ShouldBeTrue($"{Table} must force row level security (00 §5.4.1)");
    }

    /// <summary>
    /// 00 §5.4.7/T2, D1: exactly one policy reaches the runtime role, it governs every command, and both its
    /// clauses carry <c>org_id = evidgate.current_org_id()</c> — a policy scoped only by its WITH CHECK would
    /// still expose every tenant's rows to SELECT. No policy reaches PUBLIC.
    /// </summary>
    [Fact]
    public async Task RunCancelRequest_AppPolicyCarriesTheTenantPredicateForTheApplicationRole()
    {
        await using var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token);
        var policies = (await RlsTableCatalog.ReadPoliciesAsync(connection, Token))
            .Where(policy => string.Equals(policy.Table, Table, StringComparison.Ordinal))
            .ToList();

        policies.ShouldNotBeEmpty($"{Table} carries no policy at all, so RLS would deny every read and write");

        policies.ShouldNotContain(
            policy => policy.Roles.Contains(RlsTableCatalog.PublicRole, StringComparer.Ordinal),
            $"{Table} has a policy that reaches PUBLIC, which no tenant policy may do (D1)");

        var app = policies
            .Where(policy => policy.Roles.Contains(RlsTableCatalog.ApplicationRole, StringComparer.Ordinal))
            .ToList();
        app.Count.ShouldBe(1, $"{Table} must have exactly one policy for {RlsTableCatalog.ApplicationRole}");

        var policy = app[0];
        policy.Permissive.ShouldBeTrue("a restrictive policy only narrows the permissive ones");
        policy.IsAllCommands.ShouldBeTrue("one FOR ALL policy, not a command-by-command set a reader can miss");
        RlsTableCatalog.MatchesUsing(policy, RlsTableCatalog.TenantPredicate).ShouldBeTrue(
            $"the USING clause must scope the read: {policy.Using}");
        policy.WithCheck.ShouldNotBeNullOrEmpty($"the WITH CHECK clause must scope the write: {policy.Using}");
        RlsTableCatalog.MatchesWithCheck(policy, RlsTableCatalog.TenantPredicate).ShouldBeTrue(
            $"the WITH CHECK clause too — a scoped USING with 'with check (true)' leaves the write open: {policy.WithCheck}");

        var owner = policies
            .Where(candidate => candidate.Roles.Contains(OwnerRole, StringComparer.Ordinal))
            .ToList();
        owner.Count.ShouldBe(
            1,
            $"FORCE row level security binds {OwnerRole} too, so the migration role needs its own policy");
    }

    /// <summary>
    /// The write path's grants, as every module table of this chain declares them (0049/0059/0066/0067): the app role
    /// reads and inserts the request columns, and may move <c>state</c> alone — the one column the stop stages own —
    /// so a recorded request can never be repointed, re-reasoned or deleted. The operator keeps read only and the
    /// scheduler holds nothing on this table.
    /// </summary>
    [Fact]
    public async Task RunCancelRequest_GrantsTheAppRoleTheWritePathOnly()
    {
        await using var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token);

        (await RlsTableCatalog.HasTablePrivilegeAsync(connection, RlsTableCatalog.ApplicationRole, Table, "SELECT", Token))
            .ShouldBeTrue("the handler reads the run's existing request before it inserts");
        (await RlsTableCatalog.HasAnyColumnPrivilegeAsync(connection, RlsTableCatalog.ApplicationRole, Table, "INSERT", Token))
            .ShouldBeTrue("the handler inserts the one request row");

        // Column-scoped grants: the table-level privilege stays false, so only the column form proves each fact.
        (await RlsTableCatalog.HasColumnPrivilegeAsync(
                connection, RlsTableCatalog.ApplicationRole, Table, "reason", "INSERT", Token))
            .ShouldBeTrue("the request carries the caller's reason");
        (await RlsTableCatalog.HasColumnPrivilegeAsync(
                connection, RlsTableCatalog.ApplicationRole, Table, "state", "UPDATE", Token))
            .ShouldBeTrue("the soft-stop and kill stages move the request's state, and only it");
        (await RlsTableCatalog.HasColumnPrivilegeAsync(
                connection, RlsTableCatalog.ApplicationRole, Table, "reason", "UPDATE", Token))
            .ShouldBeFalse("a recorded request can never be re-reasoned");
        (await RlsTableCatalog.HasTablePrivilegeAsync(connection, RlsTableCatalog.ApplicationRole, Table, "DELETE", Token))
            .ShouldBeFalse("a cancel request is never deleted");

        (await RlsTableCatalog.HasTablePrivilegeAsync(connection, OperatorRole, Table, "SELECT", Token))
            .ShouldBeTrue("the operator keeps the standing read every module table grants it");
        (await RlsTableCatalog.HasTablePrivilegeAsync(connection, OperatorRole, Table, "INSERT", Token))
            .ShouldBeFalse("the operator never writes a tenant row");
        (await RlsTableCatalog.HasTablePrivilegeAsync(connection, OperatorRole, Table, "UPDATE", Token))
            .ShouldBeFalse();

        (await RlsTableCatalog.HasTablePrivilegeAsync(connection, SchedulerRole, Table, "SELECT", Token))
            .ShouldBeFalse("the scheduler holds nothing on this table");
        (await RlsTableCatalog.HasTablePrivilegeAsync(connection, SchedulerRole, Table, "INSERT", Token))
            .ShouldBeFalse();
        (await RlsTableCatalog.HasTablePrivilegeAsync(connection, SchedulerRole, Table, "UPDATE", Token))
            .ShouldBeFalse();
    }
}
