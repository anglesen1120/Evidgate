// The gate against a real PostgreSQL when the caller leaves the billing month open: the first statement resolves the
// month of the caller's own transaction and answers it, so a batch is gated, carried and metered in one month without a
// second round trip.
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The month a question is gated in when it names none: the caller's own transaction month, resolved inside the
/// reservation, metered on that month's counter and answered in <see cref="QuotaCheckResult.Period"/>.
/// </summary>
public sealed partial class UsageQuotaGateTests
{
    [Fact]
    public async Task QuotaGateCheck_NullPeriod_IsMeteredAndAnsweredInItsOwnTransactionsMonth()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var token = TestContext.Current.CancellationToken;
        await using var services = world.Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        await using var scope = await provider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, token);

        // transaction_timestamp() is fixed for the whole transaction, so the month read here is the one the gate's own
        // statement resolves in this same transaction, and a month-end rollover cannot change it.
        var month = (await provider.GetRequiredService<ITenantDb>().QuerySingleOrDefaultAsync<string>(
            "select to_char(transaction_timestamp() at time zone 'UTC','YYYY-MM')", cancellationToken: token))!;
        await SeedAsync(world, used: 1m, period: month);

        var answer = await provider.GetRequiredService<IQuotaGate>()
            .CheckAsync(Request(world, 1m, period: null), token);
        await scope.CommitAsync(token);

        answer.Decision.ShouldBe(QuotaDecision.Ok);
        answer.Period.ShouldBe(month, "a call that names no month is gated in the month of its own transaction");
        answer.Used.ShouldBe(2m);
        (await world.CounterAsync(project: world.Project, period: month)).ShouldNotBeNull().Used.ShouldBe(
            2m, "the month the answer names is the counter the call was metered in");
    }

    [Fact]
    public async Task QuotaGateCheck_NullPeriodBlock_NamesTheRefusingCountersMonth()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var token = TestContext.Current.CancellationToken;
        await using var services = world.Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        await using var scope = await provider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, token);

        var month = (await provider.GetRequiredService<ITenantDb>().QuerySingleOrDefaultAsync<string>(
            "select to_char(transaction_timestamp() at time zone 'UTC','YYYY-MM')", cancellationToken: token))!;
        await SeedAsync(world, used: Limit, period: month);

        var answer = await provider.GetRequiredService<IQuotaGate>()
            .CheckAsync(Request(world, 1m, period: null), token);
        await scope.CommitAsync(token);

        answer.Decision.ShouldBe(QuotaDecision.Block);
        answer.Period.ShouldBe(month, "the refusal names the month of the counter that refused");
        answer.Used.ShouldBe(Limit);
        answer.Limit.ShouldBe(Limit);
        answer.RetryAfter.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        (await world.CounterAsync(project: world.Project, period: month)).ShouldNotBeNull().Used.ShouldBe(
            Limit, "a refused call is never counted");
    }

    [Fact]
    public async Task QuotaGateCheck_NullPeriodWithoutCounter_AnswersNoMonthInOneStatement()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        world.ClearStatementLog();

        var answer = await world.CheckAsync(Request(world, 3m, period: null));

        answer.Decision.ShouldBe(QuotaDecision.Ok, "no counter row is no configured limit, not a refusal");
        answer.Period.ShouldBeNull("no counter answered, so no month answers either");
        answer.Used.ShouldBeNull();
        (await world.CounterCountAsync()).ShouldBe(0, "the gate never creates a counter");
        world.CounterStatements().ShouldHaveSingleItem();
        world.Api.Statements.Snapshot().Count(statement =>
                statement.Contains("to_char(transaction_timestamp()", StringComparison.Ordinal))
            .ShouldBe(
                1,
                "the month is resolved inside the reservation, not by a statement of its own before it");
    }

    [Fact]
    public async Task QuotaGateCheck_NullPeriodDisabledSwitch_AnswersOkWithoutMonth()
    {
        await using var world = await UsageQuotaWorld.StartAsync(
            _fixture,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Usage:QuotaGate:Enabled"] = "false",
            });
        var (month, _) = await world.DatabaseMonthsAsync();
        await SeedAsync(world, used: Limit, period: month);
        world.ClearStatementLog();

        var answer = await world.CheckAsync(Request(world, 1m, period: null));

        answer.Decision.ShouldBe(QuotaDecision.Ok, "the operator switch answers Ok explicitly, it is not a stub");
        answer.Period.ShouldBeNull("a switched-off gate reads no counter, so no month answers either");
        world.CounterStatements().ShouldBeEmpty();
    }
}
