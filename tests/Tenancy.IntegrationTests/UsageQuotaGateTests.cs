// 12.3.1-T4: the quota gate against a real PostgreSQL — one conditional statement that both checks and reserves, the
// warning line, the boundary that admits the last unit, 50 racing callers that never overshoot, the fresh counter of a
// new period, the tenant and project scope, the operator switch, and the caller's own transaction boundary. The gate runs
// inside an explicit tenant scope through the registered IQuotaGate, exactly as the ingest hot path resolves it.
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 12.3.1 §2 AC4 and §5: a call within the limit proceeds, the first crossing of <c>warn_ratio</c> warns once, the last
/// unit inside the limit is still admitted, and a call that would pass it is blocked with the retry and raise details
/// while <c>used</c> stays where it was. The counter is written in the caller's own transaction, so a rolled-back caller
/// leaves no reservation behind.
/// </summary>
[Trait("Category", "Docker")]
public sealed class UsageQuotaGateTests
{
    private const string Period = "2026-10";
    private const string NextPeriod = "2026-11";
    private const decimal Limit = 10m;
    private const decimal WarnRatio = 0.8m;

    private static readonly DateTimeOffset OccurredAt = new(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);

    private readonly PostgresTenancyFixture _fixture;

    public UsageQuotaGateTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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
        DateTimeOffset? warnedAt = null,
        DateTimeOffset? blockedAt = null) =>
        world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(),
            world.Project,
            period,
            "results",
            used,
            limit,
            WarnRatio,
            warnedAt,
            blockedAt));

    // ------------------------------------------------------------------ AC4: warn once, block at the limit

    [Fact]
    public async Task QuotaGate_WarnThenBlock()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 0m);

        var below = await world.CheckAsync(Request(world, 5m));
        below.Decision.ShouldBe(QuotaDecision.Ok);
        below.Used.ShouldBe(5m);
        below.Limit.ShouldBe(Limit);
        below.RetryAfter.ShouldBeNull();
        below.RaisePath.ShouldBeNull();

        var atWarnLine = await world.CheckAsync(Request(world, 3m));
        atWarnLine.Decision.ShouldBe(QuotaDecision.Warn);
        atWarnLine.Used.ShouldBe(8m);

        var warnedAt = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().WarnedAt;
        warnedAt.ShouldNotBeNull("the call that crossed the warning line stamps it");

        // The last unit inside the limit is still admitted, and it is still a warning, not a block.
        var atLimit = await world.CheckAsync(Request(world, 2m));
        atLimit.Decision.ShouldBe(QuotaDecision.Warn);
        atLimit.Used.ShouldBe(Limit);

        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().WarnedAt
            .ShouldBe(warnedAt, "the stamp is written once, not on every warning call");

        var blocked = await world.CheckAsync(Request(world, 1m));
        blocked.Decision.ShouldBe(QuotaDecision.Block);
        blocked.Used.ShouldBe(Limit);
        blocked.Limit.ShouldBe(Limit);
        blocked.RetryAfter.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        blocked.RaisePath.ShouldBe($"/internal/operator/billing/orgs/{world.Org:D}/quota-raise");

        var stored = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        stored.Used.ShouldBe(Limit, "a refused quantity is never counted");
        stored.BlockedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task QuotaGate_ExactlyAtLimit_Boundary()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 9m);

        var lastUnit = await world.CheckAsync(Request(world, 1m));
        lastUnit.Decision.ShouldNotBe(QuotaDecision.Block);
        lastUnit.Used.ShouldBe(Limit);

        var overTheLine = await world.CheckAsync(Request(world, 1m));
        overTheLine.Decision.ShouldBe(QuotaDecision.Block);
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Used.ShouldBe(Limit);

        // A whole batch that exactly fills an empty counter is one reservation, not a refusal.
        await world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), world.Project, Period, "ai_tokens", 0m, Limit, WarnRatio, null, null));
        var exact = await world.CheckAsync(Request(world, Limit, metric: UsageMetric.AiTokens));
        exact.Decision.ShouldBe(QuotaDecision.Warn);
        exact.Used.ShouldBe(Limit);
    }

    [Fact]
    public async Task QuotaGate_ConcurrentBatches_NoOvershoot()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 0m);

        var answers = await Task.WhenAll(Enumerable.Range(0, 50).Select(
            _ => world.CheckAsync(Request(world, 1m))));

        var accepted = answers.Count(answer => answer.Decision != QuotaDecision.Block);
        accepted.ShouldBe((int)Limit, "every unit the period had room for is granted exactly once");
        answers.ShouldContain(answer => answer.Decision == QuotaDecision.Block);

        var stored = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        stored.Used.ShouldBe(accepted);
        stored.Used.ShouldBeLessThanOrEqualTo(Limit);
        stored.BlockedAt.ShouldNotBeNull("the refused callers stamped the refusal");
    }

    [Fact]
    public async Task QuotaGate_ZeroOrNegativeQuantity_Refused()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 0m);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => world.CheckAsync(Request(world, 0m)));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => world.CheckAsync(Request(world, -1m)));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => world.CheckAsync(Request(world, 1m, period: "2026-13")));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => world.CheckAsync(Request(world, 1m, period: "1999-12")));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => world.CheckAsync(Request(world, 1m, period: "3000-01")));

        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Used.ShouldBe(0m);
        world.CounterStatements().ShouldBeEmpty("a caller bug is refused before any statement runs");
    }

    [Fact]
    public async Task QuotaGate_NoCounterRow_IsOk_NoWrite()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);

        var answer = await world.CheckAsync(Request(world, 3m));

        answer.Decision.ShouldBe(QuotaDecision.Ok, "no counter row is no configured limit, not a refusal");
        answer.Used.ShouldBeNull();
        answer.Limit.ShouldBeNull();
        answer.RetryAfter.ShouldBeNull();
        answer.RaisePath.ShouldBeNull();

        (await world.CounterCountAsync()).ShouldBe(0, "the gate never creates a counter");
        world.CounterStatements().Count.ShouldBe(1, "one statement answers both the check and the absence");
        world.CounterStatements().ShouldNotContain(
            statement => statement.Contains("insert", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task QuotaGate_AcceptedCall_IsOneStatement()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 0m);

        world.ClearStatementLog();
        var answer = await world.CheckAsync(Request(world, 1m));

        answer.Decision.ShouldBe(QuotaDecision.Ok);
        world.CounterStatements().Count.ShouldBe(1, "a reservation is the check, never a read followed by a write");
    }

    [Fact]
    public async Task QuotaGate_RepeatedRefusal_StampsOnceAndKeepsTheVersion()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: Limit);

        var first = await world.CheckAsync(Request(world, 1m));

        first.Decision.ShouldBe(QuotaDecision.Block);
        var stamped = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        stamped.BlockedAt.ShouldNotBeNull("the first refusal stamps when the scope was blocked");
        stamped.Version.ShouldBe(2, "one write: the stamp");

        world.ClearStatementLog();
        var again = await world.CheckAsync(Request(world, 1m));

        again.Decision.ShouldBe(QuotaDecision.Block);
        again.Used.ShouldBe(Limit);
        again.Limit.ShouldBe(Limit);
        again.RetryAfter.ShouldNotBeNull();
        world.CounterStatements().Count.ShouldBe(
            2,
            "the reservation statement answers the scope and the refusal statement answers it without a "
            + "write; a third command would be a separate re-check of a counter that already refused");
        (await world.CounterAsync(project: world.Project)).ShouldBe(
            stamped,
            "a refusal whose stamp already exists writes nothing, so it cannot bump the version");
    }

    [Fact]
    public async Task QuotaGate_PeriodUpperBound_BlockStillAnswersRetry()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        const string lastPeriod = "2999-12";
        await SeedAsync(world, used: Limit, period: lastPeriod);

        var answer = await world.CheckAsync(Request(world, 1m, period: lastPeriod));

        answer.Decision.ShouldBe(QuotaDecision.Block);
        answer.Used.ShouldBe(Limit);
        answer.RetryAfter.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        (await world.CounterAsync(project: world.Project, period: lastPeriod)).ShouldNotBeNull()
            .BlockedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task QuotaGate_RaiseBeforeTheStamp_LeavesNoBlockOnTheRaisedCounter()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: Limit - 1m);

        // The operator's raise commits between the gate reading the scope and its refusal stamp: here the
        // raise holds the row lock, so the gate's statement has to wait and re-check the capacity it saw.
        await using var raising = await world.BeginAppScopeAsync();
        (await raising.ExecuteAsync(
            "update public.quota_counter set quota_limit = 20, blocked_at = null "
            + "where org_id = @org_id and project_id = @project_id and period = @period and metric = 'results'",
            ("org_id", world.Org),
            ("project_id", world.Project),
            ("period", Period))).ShouldBe(1);
        var blocker = await raising.ScalarAsync<int>("select pg_backend_pid()");

        var check = world.CheckAsync(Request(world, 2m));
        await world.WaitForBlockedByAsync(blocker);
        await raising.CommitAsync();

        var answer = await check;
        answer.Decision.ShouldBe(QuotaDecision.Ok, "the raise left room for the same call");
        var stored = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        stored.Limit.ShouldBe(20m);
        stored.Used.ShouldBe(
            Limit - 1m + 2m,
            "the call the retry statement served is counted, so a raised counter never loses admitted usage");
        stored.BlockedAt.ShouldBeNull("a counter with room is never left blocked by the racing refusal");
    }

    [Fact]
    public async Task QuotaGate_PeriodRollover_UsesFreshCounter()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: Limit, blockedAt: OccurredAt);

        var nextMonth = await world.CheckAsync(Request(world, 4m, period: NextPeriod));

        nextMonth.Decision.ShouldBe(QuotaDecision.Ok, "the closed period's block does not carry");
        nextMonth.Used.ShouldBe(4m, "the new period counts only what it was asked for");
        nextMonth.Limit.ShouldBe(Limit, "the limit carries into the new period");

        var october = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        october.Used.ShouldBe(Limit, "a new period never spends the closed period's usage");
        october.BlockedAt.ShouldBe(OccurredAt, "and never lifts the closed period's block");

        (await world.CounterCountAsync()).ShouldBe(2L, "the new period gets its own counter, next to the closed one");
    }

    [Fact]
    public async Task QuotaGate_CrossOrg_CountersIsolated()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();
        await SeedAsync(world, used: 0m);
        await world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), otherProject, Period, "results", 9m, Limit, WarnRatio, Org: otherOrg));

        var own = await world.CheckAsync(Request(world, Limit));
        own.Decision.ShouldBe(QuotaDecision.Warn);
        own.Used.ShouldBe(Limit);

        var foreign = await world.CheckAsync(Request(world, 1m, project: otherProject));
        foreign.Decision.ShouldBe(QuotaDecision.Ok, "another org's project is not a counter of this tenant");
        foreign.Used.ShouldBeNull();

        (await world.CounterAsync(org: otherOrg, project: otherProject)).ShouldNotBeNull().Used.ShouldBe(9m);
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Used.ShouldBe(Limit);
    }

    [Fact]
    public async Task QuotaGate_Disabled_ReturnsOk_WithoutWrite()
    {
        await using var world = await UsageQuotaWorld.StartAsync(
            _fixture,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Usage:QuotaGate:Enabled"] = "false",
            });
        await SeedAsync(world, used: Limit);

        var answer = await world.CheckAsync(Request(world, 1m));

        answer.Decision.ShouldBe(QuotaDecision.Ok, "the operator switch answers Ok explicitly, it is not a stub");
        answer.Used.ShouldBeNull();

        var stored = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        stored.Used.ShouldBe(Limit);
        stored.BlockedAt.ShouldBeNull();
        world.CounterStatements().ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ the caller's transaction boundary

    [Fact]
    public async Task QuotaGate_RolledBackCaller_LeavesUsedUnchanged()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 0m);

        var rolledBack = await world.CheckAsync(Request(world, 3m), commit: false);
        rolledBack.Decision.ShouldBe(QuotaDecision.Ok);
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Used.ShouldBe(0m);

        var committed = await world.CheckAsync(Request(world, 3m));
        committed.Decision.ShouldBe(QuotaDecision.Ok);
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Used.ShouldBe(3m);
    }

    [Fact]
    public async Task QuotaGate_CommittedBatch_Completes()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 0m);

        var accepted = await world.CheckAndRecordAsync(
            Request(world, Limit),
            new UsageEntryInput("quota:committed:1", UsageMetric.Results, world.Project, Limit, "batch", Period, OccurredAt));
        accepted.Decision.ShouldBe(QuotaDecision.Warn);

        var ledgerRows = await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.usage_ledger_entry where org_id = '{world.Org:D}'");
        ledgerRows.ShouldBe(1);

        var blocked = await world.CheckAsync(Request(world, 1m));
        blocked.Decision.ShouldBe(QuotaDecision.Block);

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.usage_ledger_entry where org_id = '{world.Org:D}'"))
            .ShouldBe(1, "a block refuses the next batch; it never rolls back work already committed");
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Used.ShouldBe(Limit);
    }
}
