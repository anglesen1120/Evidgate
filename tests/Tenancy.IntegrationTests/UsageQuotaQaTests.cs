using System.Globalization;
using System.Net;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Usage.Application.Commands;
using Evidgate.Modules.Usage.Application.Quota;
using Evidgate.Modules.Usage.Contracts;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class UsageQuotaAdversarialTests(PostgresTenancyFixture fixture)
{
    private const string Period = "2026-10";
    private const string AuditAction = "usage.quota_raise";
    private const string Reason = "additional capacity for the pilot";

    [Fact]
    public async Task QuotaGate_FiftyMixedBatchesStraddleLimit_CountsOnlyAcceptedQuantities()
    {
        await using var world = await UsageQuotaWorld.StartAsync(fixture);
        const decimal initial = 7.5m;
        const decimal limit = 13.75m;
        await SeedAsync(world, initial, limit);
        using var start = new SemaphoreSlim(0, 50);
        var quantities = Enumerable.Range(0, 50).Select(i => i % 2 == 0 ? 0.25m : 0.5m).ToArray();
        var checks = quantities.Select(quantity => AfterSignalAsync(
            start, () => world.CheckAsync(Request(world, quantity)))).ToArray();

        start.Release(50);
        var results = await Task.WhenAll(checks);

        var accepted = quantities.Where((_, i) => results[i].Decision != QuotaDecision.Block).Sum();
        var stored = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        stored.Used.ShouldBe(initial + accepted);
        stored.Used.ShouldBe(limit, "the available quarter-unit capacity must all be usable");
        results.ShouldContain(result => result.Decision == QuotaDecision.Block);
        results.ShouldContain(result => result.Decision == QuotaDecision.Warn);
        foreach (var result in results)
        {
            result.Used.ShouldNotBeNull().ShouldBeLessThanOrEqualTo(limit);
        }
        stored.BlockedAt.ShouldNotBeNull();
        stored.WarnedAt.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9.125)]
    public async Task QuotaGate_FractionalBatchExactlyFillsLimit_AdmitsThenRefusesNext(double alreadyUsed)
    {
        await using var world = await UsageQuotaWorld.StartAsync(fixture);
        var initial = (decimal)alreadyUsed;
        await SeedAsync(world, initial, 10m);

        var accepted = await world.CheckAsync(Request(world, 10m - initial));

        accepted.Decision.ShouldBe(QuotaDecision.Warn);
        accepted.Used.ShouldBe(10m);
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().BlockedAt.ShouldBeNull();

        var refused = await world.CheckAsync(Request(world, 0.001m));
        refused.Decision.ShouldBe(QuotaDecision.Block);
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Used.ShouldBe(10m);
    }

    [Fact]
    public async Task QuotaGate_UtcMidnightRollover_RetryIsAtLeastOneSecondAndNewPeriodIsFresh()
    {
        await using var world = await UsageQuotaWorld.StartAsync(fixture);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 31, 23, 59, 59, 750, TimeSpan.Zero));
        await using var host = TenancyApiFactory.RealResolver(
            fixture.AppConnectionString(world.Database),
            services: services => services.AddSingleton<TimeProvider>(clock));
        await SeedAsync(world, 10m, 10m);
        await world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), world.Project, "2026-11", "results", 0m, 10m));
        host.Services.GetRequiredService<TimeProvider>().ShouldBeSameAs(clock);

        var before = await CheckAsync(world, host, Request(world, 1m));
        before.Decision.ShouldBe(QuotaDecision.Block);
        before.RetryAfter.ShouldBe(TimeSpan.FromSeconds(1));
        var oldCounter = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();

        clock.Advance(TimeSpan.FromMilliseconds(250));
        var expired = await CheckAsync(world, host, Request(world, 1m));
        expired.Decision.ShouldBe(QuotaDecision.Block);
        expired.RetryAfter.ShouldBe(TimeSpan.FromSeconds(1));
        var currentPeriod = clock.GetUtcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture);
        currentPeriod.ShouldBe("2026-11");
        var fresh = await CheckAsync(world, host, Request(world, 2.5m, currentPeriod));

        fresh.Decision.ShouldBe(QuotaDecision.Ok);
        fresh.Used.ShouldBe(2.5m);
        fresh.RetryAfter.ShouldBeNull();
        (await world.CounterAsync(project: world.Project)).ShouldBe(oldCounter);
        var next = (await world.CounterAsync(project: world.Project, period: currentPeriod)).ShouldNotBeNull();
        next.Used.ShouldBe(2.5m);
        next.BlockedAt.ShouldBeNull();
        next.WarnedAt.ShouldBeNull();
    }

    [Fact]
    public async Task QuotaGate_ForeignProjectAndTenantCounters_AreUntouched()
    {
        await using var world = await UsageQuotaWorld.StartAsync(fixture);
        var (otherOrg, otherProject) = await world.AddOtherOrgAsync();
        await SeedAsync(world, 0m, 10m);
        var stamp = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        await world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), otherProject, Period, "results", 8m, 10m, 0.8m, stamp, stamp, otherOrg));
        await world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), null, Period, "results", 9m, 10m, Org: otherOrg));
        var foreignProject = await world.CounterAsync(org: otherOrg, project: otherProject);
        var foreignTenant = await world.CounterAsync(org: otherOrg);

        var own = await world.CheckAsync(Request(world, 2m));
        own.Decision.ShouldBe(QuotaDecision.Ok);
        own.Used.ShouldBe(2m);
        var foreign = await world.CheckAsync(new QuotaCheckRequest(UsageMetric.Results, Period, otherProject, 5m));
        foreign.Decision.ShouldBe(QuotaDecision.Ok);
        foreign.Used.ShouldBeNull();
        var tenant = await world.CheckAsync(new QuotaCheckRequest(UsageMetric.Results, Period, null, 5m));
        tenant.Used.ShouldBeNull();

        (await world.CounterAsync(org: otherOrg, project: otherProject)).ShouldBe(foreignProject);
        (await world.CounterAsync(org: otherOrg)).ShouldBe(foreignTenant);
        (await world.CounterCountAsync(otherOrg)).ShouldBe(2L);
    }

    [Fact]
    public async Task RaiseQuotaLimit_MemberWithoutBillingWrite_RefusesWithoutCounterOrAuditChange()
    {
        await using var world = await UsageQuotaWorld.StartAsync(fixture);
        await SeedAsync(world, 5m, 10m);
        var before = await world.CounterAsync(project: world.Project);
        await using var deniedHost = TenancyApiFactory.RealEvaluator(fixture.AppConnectionString(world.Database));

        var denied = await SendRaiseAsync(world, deniedHost, 20m);

        denied.Error.ShouldNotBeNull().Code.ShouldBe("forbidden");
        (await world.CounterAsync(project: world.Project)).ShouldBe(before);
        (await world.AuditCountAsync(null, AuditAction)).ShouldBe(0L);

        var allowed = await SendRaiseAsync(world, world.Commands, 20m);
        allowed.IsSuccess.ShouldBeTrue(allowed.Error?.Message);
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Limit.ShouldBe(20m);
        (await world.AuditCountAsync(null, AuditAction)).ShouldBe(1L);
    }

    [Fact]
    public async Task RaiseQuotaLimit_RacesFiftyChecks_UsedEqualsAcceptedSumAndNeverExceedsFinalLimit()
    {
        await using var world = await UsageQuotaWorld.StartAsync(fixture, OperatorGateMode.Permitted);
        await SeedAsync(world, 9.5m, 10m);
        using var start = new SemaphoreSlim(0, 51);
        var checks = Enumerable.Range(0, 50).Select(_ => AfterSignalAsync(
            start, () => world.CheckAsync(Request(world, 0.5m)))).ToArray();
        var raise = AfterSignalAsync(start, () => PostRaiseAsync(world, 20m));

        start.Release(51);
        var results = await Task.WhenAll(checks);
        (await raise).Status.ShouldBe(HttpStatusCode.OK);

        var stored = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        stored.Limit.ShouldBe(20m);
        stored.Used.ShouldBe(9.5m + (0.5m * results.Count(result => result.Decision != QuotaDecision.Block)));
        stored.Used.ShouldBeLessThanOrEqualTo(stored.Limit);
        (await world.AuditCountAsync(null, AuditAction)).ShouldBe(1L);
        if (stored.Used < stored.Limit)
        {
            var remainder = await world.CheckAsync(Request(world, stored.Limit - stored.Used));
            remainder.Decision.ShouldNotBe(QuotaDecision.Block);
        }
        (await world.CheckAsync(Request(world, 0.5m))).Decision.ShouldBe(QuotaDecision.Block);
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Used.ShouldBe(20m);
    }

    [Fact]
    public async Task RaiseQuotaLimit_PreviouslyRefusedBatch_ClearsBlockAndAllowsRetry()
    {
        await using var world = await UsageQuotaWorld.StartAsync(fixture, OperatorGateMode.Permitted);
        await SeedAsync(world, 9m, 10m);
        var refused = await world.CheckAsync(Request(world, 2m));
        refused.Decision.ShouldBe(QuotaDecision.Block);
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().BlockedAt.ShouldNotBeNull();

        (await PostRaiseAsync(world, 20m)).Status.ShouldBe(HttpStatusCode.OK);

        var raised = (await world.CounterAsync(project: world.Project)).ShouldNotBeNull();
        raised.Limit.ShouldBe(20m);
        raised.Used.ShouldBe(9m);
        raised.BlockedAt.ShouldBeNull();
        var retry = await world.CheckAsync(Request(world, 2m));
        retry.Decision.ShouldBe(QuotaDecision.Ok);
        retry.Used.ShouldBe(11m);
        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().BlockedAt.ShouldBeNull();
        var rows = await world.AuditRowsAsync(null, AuditAction);
        rows.Count.ShouldBe(1);
        rows[0].ShouldContain(UsageQuotaWorld.OperatorId.ToString("D"));
        rows[0].ShouldContain($"\"reason\": \"{Reason}\"");
    }

    private static QuotaCheckRequest Request(UsageQuotaWorld world, decimal quantity, string period = Period) =>
        new(UsageMetric.Results, period, world.Project, quantity);

    private static Task<Guid> SeedAsync(UsageQuotaWorld world, decimal used, decimal limit) =>
        world.SeedCounterAsync(new CounterSeed(Guid.CreateVersion7(), world.Project, Period, "results", used, limit));

    private static async Task<T> AfterSignalAsync<T>(SemaphoreSlim signal, Func<Task<T>> action)
    {
        await signal.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return await action().ConfigureAwait(false);
    }

    private static async Task<QuotaCheckResult> CheckAsync(
        UsageQuotaWorld world,
        TenancyApiFactory host,
        QuotaCheckRequest request)
    {
        var token = TestContext.Current.CancellationToken;
        await using var services = host.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        await using var scope = await provider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, token);
        var result = await provider.GetRequiredService<IQuotaGate>().CheckAsync(request, token);
        await scope.CommitAsync(token);
        return result;
    }

    private static async Task<Result<QuotaRaiseResult>> SendRaiseAsync(
        UsageQuotaWorld world,
        TenancyApiFactory host,
        decimal limit)
    {
        await using var services = host.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        using var tenant = provider.GetRequiredService<IRequestTenantContextSetter>().Publish(world.Org, null);
        using var principal = provider.GetRequiredService<IRequestPrincipalSetter>()
            .Publish(RequestPrincipal.Member(world.User));
        return await provider.GetRequiredService<IMediator>().Send(
            new RaiseQuotaLimit(world.Project, Period, UsageMetric.Results, limit, Reason),
            TestContext.Current.CancellationToken);
    }

    private static Task<ApiAnswer> PostRaiseAsync(UsageQuotaWorld world, decimal limit) =>
        world.PostRaiseAsync(new
        {
            project_id = world.Project,
            period = Period,
            metric = "results",
            new_limit = limit,
            reason = Reason,
        });
}
