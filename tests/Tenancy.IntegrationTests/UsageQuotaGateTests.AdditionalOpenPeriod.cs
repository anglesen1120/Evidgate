using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class UsageQuotaGateTests
{
    [Fact]
    public async Task QuotaGate_NullPeriodWithFullAdjacentMonths_CarriesOnlyThePreviousLimit()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var token = TestContext.Current.CancellationToken;
        await using var services = world.Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        await using var scope = await provider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, token);
        var month = (await provider.GetRequiredService<ITenantDb>().QuerySingleOrDefaultAsync<string>(
            "select to_char(transaction_timestamp() at time zone 'UTC','YYYY-MM')", cancellationToken: token))
            .ShouldNotBeNull();
        var instant = DateTimeOffset.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
        var previous = instant.AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var next = instant.AddMonths(1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        await SeedAsync(world, Limit, period: previous, blockedAt: OccurredAt);
        await SeedAsync(world, 2m, limit: 2m, period: next, blockedAt: OccurredAt);
        var old = (await world.CounterAsync(project: world.Project, period: previous)).ShouldNotBeNull();
        var future = (await world.CounterAsync(project: world.Project, period: next)).ShouldNotBeNull();
        var gate = provider.GetRequiredService<IQuotaGate>();
        var request = Request(world, 1m, period: null);
        world.ClearStatementLog();

        var peek = await gate.PeekAsync(request, token);

        peek.Blocked.ShouldBeFalse("the advisory read cannot treat either adjacent month as this month");
        peek.Period.ShouldBeNull("a clear peek names no refusing counter");
        world.CounterStatements().ShouldHaveSingleItem();
        (await world.CounterAsync(project: world.Project, period: month)).ShouldBeNull();
        world.ClearStatementLog();
        var reserved = await gate.CheckAsync(request, token);
        await scope.CommitAsync(token);

        reserved.Decision.ShouldBe(QuotaDecision.Ok);
        reserved.Period.ShouldBe(month);
        reserved.Limit.ShouldBe(Limit, "carry-forward uses the previous month, never a future counter");
        reserved.Used.ShouldBe(1m, "a closed month's full usage and blocked stamp never carry forward");
        var carried = (await world.CounterAsync(project: world.Project, period: month)).ShouldNotBeNull();
        carried.BlockedAt.ShouldBeNull();
        carried.WarnedAt.ShouldBeNull();
        (await world.CounterAsync(project: world.Project, period: previous)).ShouldNotBeNull().ShouldBe(old);
        (await world.CounterAsync(project: world.Project, period: next)).ShouldNotBeNull().ShouldBe(future);
        world.CounterStatements().Count.ShouldBe(3, "reserve, carry, reserve; no standalone period query");
    }

    [Fact]
    public async Task QuotaGate_NullPeriodWhileHostCrossesMonthBoundary_UsesOneFixedDatabaseTransactionMonth()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var token = TestContext.Current.CancellationToken;
        var (month, _) = await world.DatabaseMonthsAsync();
        var boundary = DateTimeOffset.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal).AddMonths(1);
        var clock = new FakeTimeProvider(boundary.AddSeconds(-1));
        await using var host = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(world.Database),
            services: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        await using var services = host.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        await using var scope = await provider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, token);
        var transactionMonth = (await provider.GetRequiredService<ITenantDb>().QuerySingleOrDefaultAsync<string>(
            "select to_char(transaction_timestamp() at time zone 'UTC','YYYY-MM')", cancellationToken: token))
            .ShouldNotBeNull();
        await SeedAsync(world, Limit, period: transactionMonth);
        var before = (await world.CounterAsync(project: world.Project, period: transactionMonth)).ShouldNotBeNull();
        var request = Request(world, 1m, period: null);
        var gate = provider.GetRequiredService<IQuotaGate>();
        host.Statements.Clear();

        var first = await gate.PeekAsync(request, token);
        clock.Advance(TimeSpan.FromSeconds(2));
        var second = await gate.PeekAsync(request, token);

        first.Blocked.ShouldBeTrue();
        second.Blocked.ShouldBeTrue();
        first.Period.ShouldBe(transactionMonth);
        second.ShouldBe(first, "the transaction clock remains fixed when the host clock crosses a month boundary");
        second.Period.ShouldNotBe(clock.GetUtcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture));
        host.Statements.Snapshot().Count.ShouldBe(2, "each peek resolves the month in its one counter statement");
        await scope.CommitAsync(token);
        (await world.CounterAsync(project: world.Project, period: transactionMonth)).ShouldNotBeNull().ShouldBe(before);
    }
}
