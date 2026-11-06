// The second half of the gate's scope rule: one call against the organization's counter and the project's at once,
// tenant row locked first, both charged or neither, per-scope carry-forward and warnings. The single-scope cases live
// beside this file in the same subject class.
using Evidgate.Modules.Usage.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The two-scope admission: an org-wide cap applies to work one project commits, a refusal in either scope charges
/// neither, and each scope carries its limit and warns on its own.
/// </summary>
public sealed partial class UsageQuotaGateTests
{
    private const string WarningTask = "usage.quota.warn";

    /// <summary>The billing month before <see cref="Period"/>: the month a carry-forward inherits a limit from.</summary>
    private const string PreviousPeriod = "2026-09";

    /// <summary>A call that charges the tenant's counter as well as the project's, as the ingest route does.</summary>
    private static QuotaCheckRequest OrgRequest(
        UsageQuotaWorld world,
        decimal quantity,
        string period = Period) =>
        new(UsageMetric.Results, period, world.Project, quantity, IncludeTenantScope: true);

    private static Task<Guid> SeedTenantAsync(
        UsageQuotaWorld world,
        decimal used,
        decimal limit,
        string period = Period) =>
        world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), ProjectId: null, period, "results", used, limit, WarnRatio));

    [Fact]
    public async Task QuotaGate_TenantCapBelowProjectCap_BlocksAndLeaksNothing()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 0m, limit: 100m);
        await SeedTenantAsync(world, used: 8m, limit: Limit);

        var blocked = await world.CheckAsync(OrgRequest(world, 5m));

        blocked.Decision.ShouldBe(QuotaDecision.Block, "the organization cap has room for two of the five units");
        blocked.RefusedScope.ShouldBe(QuotaScope.Tenant);
        blocked.Used.ShouldBe(8m);
        blocked.Limit.ShouldBe(Limit);
        blocked.RetryAfter.ShouldNotBeNull();

        (await world.CounterAsync(project: null)).ShouldNotBeNull().Used.ShouldBe(
            8m, "a refused quantity is never counted in the scope that refused");
        var project = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        project.Used.ShouldBe(0m, "and never leaks into the scope that had room");
        project.BlockedAt.ShouldBeNull("a counter with room is not the one the refusal names");

        // The positive control: one unit fits both scopes, and both are charged.
        var fits = await world.CheckAsync(OrgRequest(world, 1m));

        fits.Decision.ShouldBe(QuotaDecision.Ok);
        (await world.CounterAsync(project: null)).ShouldNotBeNull().Used.ShouldBe(9m);
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Used.ShouldBe(1m);
    }

    [Fact]
    public async Task QuotaGate_ProjectCapBelowTenantCap_BlocksAndLeaksNothing()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 9m, limit: Limit);
        await SeedTenantAsync(world, used: 0m, limit: 100m);

        var blocked = await world.CheckAsync(OrgRequest(world, 2m));

        blocked.Decision.ShouldBe(QuotaDecision.Block, "the project's counter has room for one of the two units");
        blocked.RefusedScope.ShouldBe(QuotaScope.Project);
        blocked.Used.ShouldBe(9m);
        blocked.Limit.ShouldBe(Limit);

        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Used.ShouldBe(9m);
        (await world.CounterAsync(project: null)).ShouldNotBeNull().Used.ShouldBe(
            0m, "the organization counter is never charged for work it refused");

        var fits = await world.CheckAsync(OrgRequest(world, 1m));

        fits.Decision.ShouldBe(QuotaDecision.Ok);
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Used.ShouldBe(Limit);
        (await world.CounterAsync(project: null)).ShouldNotBeNull().Used.ShouldBe(1m);
    }

    [Fact]
    public async Task QuotaGate_BothScopes_AdmittedIncrementsBoth()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 0m, limit: 100m);
        await SeedTenantAsync(world, used: 0m, limit: 100m);

        world.ClearStatementLog();
        var answer = await world.CheckAsync(OrgRequest(world, 4m));

        answer.Decision.ShouldBe(QuotaDecision.Ok);
        answer.Used.ShouldBe(4m);
        answer.RefusedScope.ShouldBeNull();

        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Used.ShouldBe(
            4m, "the project is billed for the work it committed");
        (await world.CounterAsync(project: null)).ShouldNotBeNull().Used.ShouldBe(
            4m, "and the organization's counter is billed the same units");
        world.CounterStatements().Count.ShouldBe(1, "both scopes are checked and charged in one statement");
    }

    [Fact]
    public async Task QuotaGate_TenantOnlyCounter_AppliesToAProjectCall()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedTenantAsync(world, used: 0m, limit: Limit);

        var admitted = await world.CheckAsync(OrgRequest(world, 4m));

        admitted.Decision.ShouldBe(QuotaDecision.Ok, "a scope with no counter row has no configured limit");
        (await world.CounterAsync(project: null)).ShouldNotBeNull().Used.ShouldBe(4m);
        (await world.CounterAsync(project: world.Project)).ShouldBeNull("the gate never creates a counter");

        var refused = await world.CheckAsync(OrgRequest(world, 7m));

        refused.Decision.ShouldBe(QuotaDecision.Block, "the organization's cap covers the project's work");
        refused.RefusedScope.ShouldBe(QuotaScope.Tenant);
        (await world.CounterAsync(project: null)).ShouldNotBeNull().Used.ShouldBe(4m);
    }

    [Fact]
    public async Task QuotaGate_ConcurrentCallers_TenantCap_NeverOvershootsEitherScope()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 0m, limit: 100m);
        await SeedTenantAsync(world, used: 0m, limit: Limit);

        var answers = await Task.WhenAll(Enumerable.Range(0, 50).Select(
            _ => world.CheckAsync(OrgRequest(world, 1m))));

        var accepted = answers.Count(answer => answer.Decision != QuotaDecision.Block);
        accepted.ShouldBe((int)Limit, "every unit the organization cap has room for is granted exactly once");
        answers.ShouldContain(answer => answer.Decision == QuotaDecision.Block);

        var tenant = (await world.CounterAsync(project: null)).ShouldNotBeNull();
        tenant.Used.ShouldBe(accepted);
        tenant.Used.ShouldBeLessThanOrEqualTo(Limit);
        tenant.BlockedAt.ShouldNotBeNull("the refused callers stamped the organization counter");

        var project = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        project.Used.ShouldBe(
            accepted, "the project is charged exactly the units the organization admitted, never the refused ones");
    }

    [Fact]
    public async Task QuotaGate_NewPeriod_CarriesBothScopesIndependently()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 0m, limit: 10m);
        await SeedTenantAsync(world, used: 0m, limit: 20m);

        var nextMonth = await world.CheckAsync(OrgRequest(world, 1m, NextPeriod));

        nextMonth.Decision.ShouldBe(QuotaDecision.Ok);
        nextMonth.Limit.ShouldBe(10m, "the project's counter is the tighter one, so it answers the call");

        (await world.CounterAsync(project: world.Project, period: NextPeriod)).ShouldNotBeNull().Limit.ShouldBe(
            10m, "each scope carries its own previous limit");
        (await world.CounterAsync(project: null, period: NextPeriod)).ShouldNotBeNull().Limit.ShouldBe(
            20m, "the organization's limit is never borrowed from the project's counter");
        (await world.CounterCountAsync()).ShouldBe(4L, "two scopes, two periods");
    }

    [Fact]
    public async Task QuotaGate_WarnLine_EachScopeWarnsOncePerPeriod()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 0m, limit: Limit);
        await SeedTenantAsync(world, used: 0m, limit: Limit);
        var tenantCounter = (await world.CounterIdAsync(project: null)).ShouldNotBeNull();

        var crossing = await world.CheckAsync(OrgRequest(world, 8m));

        crossing.Decision.ShouldBe(QuotaDecision.Warn, "8 of 10 crosses both scopes' warning line");
        var project = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        var tenant = (await world.CounterAsync(project: null)).ShouldNotBeNull();
        project.WarnedAt.ShouldNotBeNull("each scope stamps its own warning line");
        tenant.WarnedAt.ShouldNotBeNull();
        (await world.OutboxCountAsync(WarningTask)).ShouldBe(2L, "one task per counter that warned");

        await world.CheckAsync(OrgRequest(world, 1m));

        (await world.CounterAsync(project: null)).ShouldNotBeNull().WarnedAt.ShouldBe(
            tenant.WarnedAt, "the stamp is written once per scope, not on every warning call");
        (await world.OutboxCountAsync(WarningTask)).ShouldBe(2L, "and only the crossing announces");
        (await world.OutboxRowsAsync(WarningTask)).ShouldContain(
            row => row.Contains(tenantCounter.ToString("D"), StringComparison.Ordinal),
            "the organization counter's own task names it");
    }

    // ---------------------------------------------------------------- one scope carried, one current

    [Fact]
    public async Task QuotaGate_CurrentTenantCounterAndPreviousProjectCounter_ChargesTheTenantOnce()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedTenantAsync(world, used: 0m, limit: 100m);
        await world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), world.Project, PreviousPeriod, "results", 0m, 10m, WarnRatio));

        var answer = await world.CheckAsync(OrgRequest(world, 2m));

        answer.Decision.ShouldBe(QuotaDecision.Ok);
        (await world.CounterAsync(project: null, period: Period)).ShouldNotBeNull().Used.ShouldBe(
            2m, "the scope that already had a counter is charged once, not once before the carry and once after");
        (await world.CounterAsync(project: world.Project, period: Period)).ShouldNotBeNull().Used.ShouldBe(
            2m, "and the carried scope pays the same units");
        (await world.CounterCountAsync()).ShouldBe(4L, "two scopes, two periods");
    }

    [Fact]
    public async Task QuotaGate_CurrentProjectCounterAndPreviousTenantCounter_ChargesTheProjectOnce()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 0m, limit: 100m);
        await world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), ProjectId: null, PreviousPeriod, "results", 0m, 10m, WarnRatio));

        var answer = await world.CheckAsync(OrgRequest(world, 2m));

        answer.Decision.ShouldBe(QuotaDecision.Ok);
        (await world.CounterAsync(project: world.Project, period: Period)).ShouldNotBeNull().Used.ShouldBe(
            2m, "the current counter is charged once for the call");
        (await world.CounterAsync(project: null, period: Period)).ShouldNotBeNull().Used.ShouldBe(
            2m, "and the carried organization counter pays the same units");
        (await world.CounterCountAsync()).ShouldBe(4L, "two scopes, two periods");
    }

    [Fact]
    public async Task QuotaGate_CarriedScopeTooSmall_ChargesNeitherScope()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedTenantAsync(world, used: 0m, limit: 100m);
        await world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), world.Project, PreviousPeriod, "results", 0m, 1m, WarnRatio));

        var blocked = await world.CheckAsync(OrgRequest(world, 2m));

        blocked.Decision.ShouldBe(QuotaDecision.Block, "the carried project limit has room for one of the two units");
        blocked.RefusedScope.ShouldBe(QuotaScope.Project);

        (await world.CounterAsync(project: null, period: Period)).ShouldNotBeNull().Used.ShouldBe(
            0m, "the statement before the carry charged nothing, so the refusal leaks nothing into the tenant scope");
        var carried = (await world.CounterAsync(project: world.Project, period: Period)).ShouldNotBeNull();
        carried.Used.ShouldBe(0m);
        carried.BlockedAt.ShouldNotBeNull("the refusal is stamped on the scope that refused");

        var fits = await world.CheckAsync(OrgRequest(world, 1m));

        fits.Decision.ShouldBe(QuotaDecision.Ok);
        (await world.CounterAsync(project: null, period: Period)).ShouldNotBeNull().Used.ShouldBe(1m);
        (await world.CounterAsync(project: world.Project, period: Period)).ShouldNotBeNull().Used.ShouldBe(1m);
    }

    [Fact]
    public async Task QuotaGate_CurrentScopeTooSmall_CarriedScopeHasRoom_ChargesNeither()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedTenantAsync(world, used: 0m, limit: 1m);
        await world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), world.Project, PreviousPeriod, "results", 0m, 100m, WarnRatio));

        var blocked = await world.CheckAsync(OrgRequest(world, 2m));

        blocked.Decision.ShouldBe(QuotaDecision.Block);
        blocked.RefusedScope.ShouldBe(QuotaScope.Tenant);

        (await world.CounterAsync(project: world.Project, period: Period)).ShouldNotBeNull().Used.ShouldBe(
            0m, "the carried scope with room is never charged for work the other scope refused");
        (await world.CounterAsync(project: null, period: Period)).ShouldNotBeNull().Used.ShouldBe(0m);
    }
}
