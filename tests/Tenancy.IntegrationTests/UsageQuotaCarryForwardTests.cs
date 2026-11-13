// A configured limit must not lapse when the month rolls over. The gate's first statement of a new period
// finds no counter for it and an earlier one for the same scope, copies that limit and warning line into the
// new period, and answers the call against it in the same tenant transaction.
using Evidgate.Modules.Usage.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The carry-forward of a quota limit across a period boundary: the first call of a period with an earlier
/// counter gets the same limit (usage and threshold stamps start empty), scopes carry independently,
/// another org's counter is never read, and concurrent first callers of a month converge on one carried row.
/// </summary>
[Trait("Category", "Docker")]
public sealed class UsageQuotaCarryForwardTests : IClassFixture<TenancyHostPool>
{
    private const string Period = "2026-10";
    private const string NextPeriod = "2026-11";
    private const decimal Limit = 10m;
    private const decimal WarnRatio = 0.8m;

    private static readonly DateTimeOffset OccurredAt = new(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);

    private readonly PostgresTenancyFixture _fixture;
    // Reuse one API host per class; each world still owns its tenant data.
    // Dedicated databases and overlapping worlds keep private hosts.
    private readonly TenancyHostPool _hosts;

    public UsageQuotaCarryForwardTests(PostgresTenancyFixture fixture, TenancyHostPool hosts)
    {
        _fixture = fixture;
        _hosts = hosts;
    }

    private static QuotaCheckRequest Request(
        UsageQuotaWorld world,
        decimal quantity,
        string period = Period,
        Guid? project = null,
        UsageMetric metric = UsageMetric.Results) =>
        new(metric, period, project ?? world.Project, quantity);

    private static Task<Guid> SeedAsync(
        UsageQuotaWorld world,
        decimal used,
        decimal limit = Limit,
        string period = Period,
        Guid? project = null,
        string metric = "results") =>
        world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), project ?? world.Project, period, metric, used, limit, WarnRatio));

    [Fact]
    public async Task QuotaGate_NewPeriod_CarriesTheLimit()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        await world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), world.Project, Period, "results", Limit, Limit, WarnRatio));

        var answer = await world.CheckAsync(Request(world, 4m, NextPeriod));

        answer.Decision.ShouldBe(QuotaDecision.Ok);
        answer.Used.ShouldBe(4m);
        answer.Limit.ShouldBe(Limit, "the new period starts with the limit the scope already agreed");

        var carried = (await world.CounterAsync(project: world.Project, period: NextPeriod)).ShouldNotBeNull();
        carried.Used.ShouldBe(4m);
        carried.Limit.ShouldBe(Limit);
        carried.WarnRatio.ShouldBe(WarnRatio);

        var closed = (await world.CounterAsync(project: world.Project))
            .ShouldNotBeNull("the closed period keeps its own row");
        closed.Used.ShouldBe(Limit);
        (await world.CounterCountAsync()).ShouldBe(2L, "a rollover adds the new period, it never reuses the old one");

        world.CounterStatements().Count.ShouldBe(
            3,
            "one answer for the month without a counter, one carry-forward, one reservation of the call");
    }

    [Fact]
    public async Task QuotaGate_NewPeriod_CarriedBlockAndWarnStampsDoNotCarry()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        await world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(),
            world.Project,
            Period,
            "results",
            Limit,
            Limit,
            WarnRatio,
            OccurredAt,
            OccurredAt));

        var firstCall = await world.CheckAsync(Request(world, 1m, NextPeriod));

        firstCall.Decision.ShouldBe(QuotaDecision.Ok, "a closed period's block does not block the new one");
        firstCall.Used.ShouldBe(1m);

        var carried = (await world.CounterAsync(project: world.Project, period: NextPeriod)).ShouldNotBeNull();
        carried.Used.ShouldBe(1m);
        carried.WarnedAt.ShouldBeNull("the warning line of the new period has not been reached");
        carried.BlockedAt.ShouldBeNull("the refusal stamp of the closed period is not carried");

        var closed = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        closed.Used.ShouldBe(Limit);
        closed.WarnedAt.ShouldBe(OccurredAt);
        closed.BlockedAt.ShouldBe(OccurredAt);
    }

    [Fact]
    public async Task QuotaGate_NewPeriod_YearRolloverCarriesDecemberIntoJanuary()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        await SeedAsync(world, used: 0m, limit: 5m, period: "2026-12");

        var answer = await world.CheckAsync(Request(world, 1m, period: "2027-01"));

        answer.Decision.ShouldBe(QuotaDecision.Ok);
        answer.Used.ShouldBe(1m);
        answer.Limit.ShouldBe(5m);

        var january = (await world.CounterAsync(project: world.Project, period: "2027-01")).ShouldNotBeNull();
        january.Limit.ShouldBe(5m);
        (await world.CounterAsync(project: world.Project, period: "2026-12")).ShouldNotBeNull().Used.ShouldBe(0m);
        (await world.CounterCountAsync()).ShouldBe(2L);
    }

    [Fact]
    public async Task QuotaGate_PeriodComesFromTheRequest_SoTheFirstCallAfterMidnightCarries()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        await SeedAsync(world, used: Limit);

        // The gate keys every statement on the caller's period, so the instant of the call decides nothing:
        // a call naming the new month is the first call of that month, whether it is 23:59:59.999 or
        // 00:00:00.000 UTC. The closed month still answers its own callers with its own block.
        var closed = await world.CheckAsync(Request(world, 1m));
        closed.Decision.ShouldBe(QuotaDecision.Block);

        var fresh = await world.CheckAsync(Request(world, 2m, NextPeriod));

        fresh.Decision.ShouldBe(QuotaDecision.Ok);
        fresh.Used.ShouldBe(2m);
        fresh.Limit.ShouldBe(Limit);
        (await world.CounterAsync(project: world.Project, period: NextPeriod)).ShouldNotBeNull().Used.ShouldBe(2m);
    }

    [Fact]
    public async Task QuotaGate_NewPeriod_ProjectAndTenantScopesCarryIndependently()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        await SeedAsync(world, used: 0m, limit: 10m);
        await world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), ProjectId: null, Period, "results", 0m, 20m, WarnRatio));

        var projectCall = await world.CheckAsync(Request(world, 1m, NextPeriod));
        var tenantCall = await world.CheckAsync(new QuotaCheckRequest(UsageMetric.Results, NextPeriod, null, 1m));

        projectCall.Limit.ShouldBe(10m);
        tenantCall.Limit.ShouldBe(20m, "the tenant-level limit is its own counter, not the project's");

        (await world.CounterAsync(project: world.Project, period: NextPeriod)).ShouldNotBeNull().Limit.ShouldBe(10m);
        (await world.CounterAsync(project: null, period: NextPeriod)).ShouldNotBeNull()
            .Limit.ShouldBe(20m);
        (await world.CounterCountAsync()).ShouldBe(4L, "two scopes, two periods");
    }

    [Fact]
    public async Task QuotaGate_NewPeriod_NeverCopiesAForeignOrgCounter()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();
        await SeedAsync(world, used: 0m, limit: 10m);
        await world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), otherProject, Period, "results", 0m, 30m, WarnRatio, Org: otherOrg));

        var own = await world.CheckAsync(Request(world, 1m, NextPeriod));
        var foreign = await world.CheckAsync(
            Request(world, 1m, NextPeriod, project: otherProject), org: otherOrg);

        own.Limit.ShouldBe(10m);
        foreign.Limit.ShouldBe(30m, "each tenant carries from its own counter");

        (await world.CounterAsync(project: world.Project, period: NextPeriod)).ShouldNotBeNull().Limit.ShouldBe(10m);
        (await world.CounterAsync(org: otherOrg, project: otherProject, period: NextPeriod)).ShouldNotBeNull()
            .Limit.ShouldBe(30m);
        (await world.CounterCountAsync()).ShouldBe(2L);
        (await world.CounterCountAsync(otherOrg)).ShouldBe(2L);
    }

    [Fact]
    public async Task QuotaGate_NewPeriod_NoEarlierCounterStaysUnlimited()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        await SeedAsync(world, used: 0m);

        // The limit is per metric: an earlier month of `results` says nothing about `ai_tokens`.
        var unmetered = await world.CheckAsync(Request(world, 1m, NextPeriod, metric: UsageMetric.AiTokens));

        unmetered.Decision.ShouldBe(QuotaDecision.Ok);
        unmetered.Used.ShouldBeNull();
        unmetered.Limit.ShouldBeNull();
        (await world.CounterCountAsync()).ShouldBe(1L, "no counter is carried across a metric");

        var metered = await world.CheckAsync(Request(world, 1m, NextPeriod));
        metered.Limit.ShouldBe(Limit, "the positive control: the metric that had a limit still carries it");
    }

    [Fact]
    public async Task QuotaGate_NewPeriod_ConcurrentFirstCallers_OneCarriedRowAndNoOvershoot()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, hosts: _hosts);
        await SeedAsync(world, used: 0m);

        var answers = await Task.WhenAll(Enumerable.Range(0, 50).Select(
            _ => world.CheckAsync(Request(world, 1m, NextPeriod))));

        var accepted = answers.Count(answer => answer.Decision != QuotaDecision.Block);
        accepted.ShouldBe((int)Limit, "every unit the carried limit has room for is granted exactly once");
        answers.ShouldContain(answer => answer.Decision == QuotaDecision.Block);

        var carried = (await world.CounterAsync(project: world.Project, period: NextPeriod)).ShouldNotBeNull();
        carried.Used.ShouldBe(accepted);
        carried.Used.ShouldBeLessThanOrEqualTo(Limit);
        carried.Limit.ShouldBe(Limit);
        (await world.CounterCountAsync()).ShouldBe(2L, "concurrent first callers share one carried counter");
    }
}
