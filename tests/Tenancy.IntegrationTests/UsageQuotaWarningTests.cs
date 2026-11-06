// The first crossing of a counter's warning line emits one warning task through the outbox, inside the
// metering transaction: the counter change and the announcement commit or roll back together, repeated or
// concurrent warning calls announce once, and a scope with no limit (or the switched-off gate) announces
// nothing.
using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Application.Quota;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The warning task of the quota gate: one <c>usage.quota.warn</c> row per counter per period, written in
/// the caller's own transaction by the statement that stamps <c>warned_at</c>, with the counter it names.
/// </summary>
[Trait("Category", "Docker")]
public sealed class UsageQuotaWarningTests
{
    private const string Period = "2026-10";
    private const string TaskType = "usage.quota.warn";
    private const decimal Limit = 10m;
    private const decimal WarnRatio = 0.8m;

    private readonly PostgresTenancyFixture _fixture;

    public UsageQuotaWarningTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static QuotaCheckRequest Request(
        UsageQuotaWorld world,
        decimal quantity,
        string period = Period,
        Guid? project = null) =>
        new(UsageMetric.Results, period, project ?? world.Project, quantity);

    private static Task<Guid> SeedAsync(
        UsageQuotaWorld world,
        decimal used,
        decimal limit = Limit,
        Guid? project = null,
        Guid? org = null) =>
        world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), project ?? world.Project, Period, "results", used, limit, WarnRatio, Org: org));

    [Fact]
    public async Task QuotaGate_FirstWarn_EnqueuesOneTaskInSameTransaction()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 0m);
        var counterId = (await world.CounterIdAsync(project: world.Project)).ShouldNotBeNull();

        QuotaWarningTask.TaskType.ShouldBe(TaskType, "the registered task type is the wire key a consumer claims");

        var crossing = await world.CheckAsync(Request(world, Limit * WarnRatio));

        crossing.Decision.ShouldBe(QuotaDecision.Warn);

        var rows = await world.OutboxRowsAsync(TaskType);
        rows.Count.ShouldBe(1);

        var parts = rows[0].Split('|', 4);
        parts[0].ShouldBe(TaskType);
        parts[1].ShouldBe("quota_counter");
        parts[2].ShouldBe(counterId.ToString("D"), "the task names the counter it announces");

        using var payload = JsonDocument.Parse(parts[3]);
        payload.RootElement.GetProperty("counter_id").GetString().ShouldBe(counterId.ToString("D"));
        payload.RootElement.GetProperty("period").GetString().ShouldBe(Period);
        payload.RootElement.GetProperty("metric").GetString().ShouldBe("results");
        payload.RootElement.GetProperty("project_id").GetString().ShouldBe(world.Project.ToString("D"));
        payload.RootElement.GetProperty("used").GetDecimal().ShouldBe(Limit * WarnRatio);
        payload.RootElement.GetProperty("quota_limit").GetDecimal().ShouldBe(Limit);
        payload.RootElement.GetProperty("warn_ratio").GetDecimal().ShouldBe(WarnRatio);

        // Later warning calls of the same period are the same crossing: the stamp already exists.
        (await world.CheckAsync(Request(world, 1m))).Decision.ShouldBe(QuotaDecision.Warn);
        (await world.CheckAsync(Request(world, 1m))).Decision.ShouldBe(QuotaDecision.Warn);
        (await world.OutboxCountAsync(TaskType)).ShouldBe(1L);
    }

    [Fact]
    public async Task QuotaGate_WarnRollback_LeavesNoTask()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 0m);

        var rolledBack = await world.CheckAsync(Request(world, Limit * WarnRatio), commit: false);

        rolledBack.Decision.ShouldBe(QuotaDecision.Warn);
        (await world.OutboxCountAsync(TaskType)).ShouldBe(0L, "the outbox row is in the caller's transaction");
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Used.ShouldBe(0m);

        var committed = await world.CheckAsync(Request(world, Limit * WarnRatio));

        committed.Decision.ShouldBe(QuotaDecision.Warn);
        (await world.OutboxCountAsync(TaskType)).ShouldBe(1L, "the committed crossing announces once");
    }

    [Fact]
    public async Task QuotaGate_ConcurrentWarnCalls_EnqueueOneTask()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 0m);

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => world.CheckAsync(Request(world, 1m))));

        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().WarnedAt.ShouldNotBeNull();
        (await world.OutboxCountAsync(TaskType)).ShouldBe(
            1L,
            "only the statement that moves warned_at from null announces; the rest serialise behind it");
    }

    [Fact]
    public async Task QuotaGate_OkCall_EnqueuesNoTask()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 0m);

        var answer = await world.CheckAsync(Request(world, 1m));

        answer.Decision.ShouldBe(QuotaDecision.Ok);
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().WarnedAt.ShouldBeNull();
        (await world.OutboxCountAsync(TaskType)).ShouldBe(0L);
    }

    [Fact]
    public async Task QuotaGate_BlockedFromBelowTheWarnLine_EnqueuesNoTask()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 7m);

        // The call is refused before any unit is counted, so the period never reached the warning line: a
        // block is not a warning, and the counter keeps no stamp to announce.
        var answer = await world.CheckAsync(Request(world, 5m));

        answer.Decision.ShouldBe(QuotaDecision.Block);
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().WarnedAt.ShouldBeNull();
        (await world.OutboxCountAsync(TaskType)).ShouldBe(0L);
    }

    [Fact]
    public async Task QuotaGate_Disabled_EnqueuesNoTask()
    {
        await using var world = await UsageQuotaWorld.StartAsync(
            _fixture,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Usage:QuotaGate:Enabled"] = "false",
            });
        await SeedAsync(world, used: Limit * WarnRatio);

        var answer = await world.CheckAsync(Request(world, 5m));

        answer.Decision.ShouldBe(QuotaDecision.Ok);
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Used.ShouldBe(Limit * WarnRatio);
        (await world.OutboxCountAsync(TaskType)).ShouldBe(0L, "a switched-off gate writes nothing at all");
    }

    [Fact]
    public async Task QuotaGate_WarnInOneOrg_IsNeverEnqueuedForAnother()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();
        await SeedAsync(world, used: Limit * WarnRatio);
        await SeedAsync(world, used: Limit * WarnRatio - 1m, project: otherProject, org: otherOrg);

        var own = await world.CheckAsync(Request(world, 1m));
        var foreign = await world.CheckAsync(Request(world, 1m, project: otherProject), org: otherOrg);

        own.Decision.ShouldBe(QuotaDecision.Warn);
        foreign.Decision.ShouldBe(QuotaDecision.Warn);
        (await world.OutboxCountAsync(TaskType)).ShouldBe(1L);
        (await world.OutboxCountAsync(TaskType, otherOrg)).ShouldBe(1L);
        (await world.OutboxRowsAsync(TaskType, otherOrg))[0]
            .Split('|', 4)[2].ShouldBe(
                (await world.CounterIdAsync(org: otherOrg, project: otherProject)).ShouldNotBeNull().ToString("D"));
    }

    [Fact]
    public async Task QuotaGate_StampRefusal_ReservingAfterARaise_ReportsTheWarnTransition()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 7m);
        var counterId = (await world.CounterIdAsync(project: world.Project)).ShouldNotBeNull();

        // The second statement settles a call the first did not grant; when an operator raise committed in
        // between it reserves the call, and a reservation that reaches the warning line stamps it once. That
        // transition is what the gate turns into one warning task.
        await using var services = world.Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(
            world.Org, null, false, TestContext.Current.CancellationToken);
        (await provider.GetRequiredService<ITenantDb>().ExecuteAsync(
            $"update public.quota_counter set quota_limit = 14 where id = '{counterId:D}'",
            cancellationToken: TestContext.Current.CancellationToken)).ShouldBe(1);

        var settled = (await provider.GetRequiredService<QuotaCounterStore>().SettleAsync(
            world.Org, Period, "results", [world.Project], 5m, TestContext.Current.CancellationToken))
            .ShouldHaveSingleItem();

        settled.Blocked.ShouldBeFalse("the raised counter has room for the call");
        settled.Used.ShouldBe(12m);
        settled.WarnedNow.ShouldBeTrue("12 of 14 is past the warning line of a counter that had not warned");
        (await world.OutboxCountAsync(TaskType)).ShouldBe(
            0L,
            "the statement reports the transition; announcing it is the gate's call, never the store's");
        await scope.CommitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task QuotaGate_RaceOfCallsAndARaise_AnnouncesTheWarningOnce()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, OperatorGateMode.Permitted);
        await SeedAsync(world, used: 7m);
        using var start = new SemaphoreSlim(0, 21);
        var checks = Enumerable.Range(0, 20).Select(_ => AfterSignalAsync(
            start, () => world.CheckAsync(Request(world, 1m)))).ToArray();
        var raise = AfterSignalAsync(start, () => world.PostRaiseAsync(new
        {
            project_id = world.Project,
            period = Period,
            metric = "results",
            new_limit = 20m,
            reason = "additional capacity for the pilot",
        }));

        start.Release(21);
        await Task.WhenAll(checks);
        (await raise).Status.ShouldBe(HttpStatusCode.OK);

        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().WarnedAt.ShouldNotBeNull();
        (await world.OutboxCountAsync(TaskType)).ShouldBe(
            1L,
            "the crossing announces once, whichever gate statement made it");
    }

    private static async Task<T> AfterSignalAsync<T>(SemaphoreSlim signal, Func<Task<T>> action)
    {
        await signal.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return await action().ConfigureAwait(false);
    }
}
