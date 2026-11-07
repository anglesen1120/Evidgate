// The refusal wait is measured from the database's transaction clock, the clock that named the period, so a host with
// a clock a day ahead still names the month the database is in.
using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The quota gate against a real PostgreSQL while the host's own clock runs a day ahead of it: both the charging
/// <c>Check</c> refusal and the advisory <c>Peek</c> refusal name the wait the month of the transaction they gate in has
/// left, measured on that same transaction, and a host reading the database answers the same wait.
/// </summary>
[Trait("Category", "Docker")]
public sealed class UsageQuotaRetryAfterTests(PostgresTenancyFixture fixture)
{
    private const decimal Limit = 10m;

    private readonly PostgresTenancyFixture _fixture = fixture;

    [Fact]
    public async Task CheckAsync_HostClockADayAhead_RetryAfterIsTheGatedTransactionsMonthRemaining()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var databaseNow = await DatabaseInstantAsync(world);
        await SeedFullCountersAsync(world, databaseNow);
        await using var host = HostWithClockAhead(world, databaseNow);

        var (blocked, retryAfter, expected) = await GateAsync(world, host, peek: false);

        blocked.ShouldBeTrue();
        // The wait the skewed host answered is the one this transaction's own clock leaves, to the half second.
        retryAfter.ShouldNotBeNull().TotalSeconds.ShouldBe(expected, 0.5);

        // The positive control: a host reading the database's clock answers the wait of its own transaction.
        var (unskewed, unskewedRetryAfter, unskewedExpected) = await GateAsync(world, world.Api, peek: false);

        unskewed.ShouldBeTrue();
        unskewedRetryAfter.ShouldNotBeNull().TotalSeconds.ShouldBe(unskewedExpected, 0.5);
    }

    [Fact]
    public async Task PeekAsync_HostClockADayAhead_RetryAfterIsTheGatedTransactionsMonthRemaining()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        var databaseNow = await DatabaseInstantAsync(world);
        await SeedFullCountersAsync(world, databaseNow);
        await using var host = HostWithClockAhead(world, databaseNow);

        var (blocked, retryAfter, expected) = await GateAsync(world, host, peek: true);

        blocked.ShouldBeTrue();
        retryAfter.ShouldNotBeNull().TotalSeconds.ShouldBe(expected, 0.5);

        // The positive control: only the host clock differs, and the wait follows the transaction all the same.
        var (unskewed, unskewedRetryAfter, unskewedExpected) = await GateAsync(world, world.Api, peek: true);

        unskewed.ShouldBeTrue();
        unskewedRetryAfter.ShouldNotBeNull().TotalSeconds.ShouldBe(unskewedExpected, 0.5);
    }

    /// <summary>
    /// Asks the gate one question in a tenant transaction of its own and measures that transaction's month remaining on
    /// the same transaction, so the expected wait is the gate's own clock and no lapse between test steps can skew it.
    /// </summary>
    private static async Task<(bool Blocked, TimeSpan? RetryAfter, double ExpectedSeconds)> GateAsync(
        UsageQuotaWorld world,
        TenancyApiFactory host,
        bool peek)
    {
        var token = TestContext.Current.CancellationToken;
        await using var services = host.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        await using var scope = await provider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, token);

        var (period, secondsToNext) = await MonthRemainingAsync(scope, token);
        var request = new QuotaCheckRequest(UsageMetric.Results, period, world.Project, 1m);
        var gate = provider.GetRequiredService<IQuotaGate>();

        bool blocked;
        TimeSpan? retryAfter;
        if (peek)
        {
            var peeked = await gate.PeekAsync(request, token);
            blocked = peeked.Blocked;
            retryAfter = peeked.RetryAfter;
        }
        else
        {
            var checkedAnswer = await gate.CheckAsync(request, token);
            blocked = checkedAnswer.Decision == QuotaDecision.Block;
            retryAfter = checkedAnswer.RetryAfter;
        }

        await scope.CommitAsync(token);

        return (blocked, retryAfter, Math.Max(1, secondsToNext));
    }

    /// <summary>The scope's own month and the seconds it has left, read on that scope's transaction.</summary>
    private static async Task<(string Period, double SecondsToNext)> MonthRemainingAsync(
        ITenantScope scope,
        CancellationToken token)
    {
        await using var command = scope.Connection.CreateCommand();
        command.Transaction = scope.Transaction;
        command.CommandText =
            """
            select to_char(transaction_timestamp() at time zone 'UTC','YYYY-MM'),
                   extract(epoch from (
                       date_trunc('month', transaction_timestamp() at time zone 'UTC') + interval '1 month'
                           - (transaction_timestamp() at time zone 'UTC')
                   ))::double precision
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);

        return (reader.GetString(0), reader.GetDouble(1));
    }

    /// <summary>Fills the month of the read instant and the one after it, so a rollover during the test blocks too.</summary>
    private static async Task SeedFullCountersAsync(UsageQuotaWorld world, DateTimeOffset databaseNow)
    {
        await FullCounterAsync(world, Month(databaseNow));
        await FullCounterAsync(world, Month(databaseNow.AddMonths(1)));
    }

    private static string Month(DateTimeOffset instant) => instant.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static Task<Guid> FullCounterAsync(UsageQuotaWorld world, string period) =>
        world.SeedCounterAsync(
            new CounterSeed(Guid.CreateVersion7(), world.Project, period, "results", Limit, Limit));

    private static async Task<DateTimeOffset> DatabaseInstantAsync(UsageQuotaWorld world) =>
        DateTimeOffset.FromUnixTimeMilliseconds(
            await world.ScalarAsync<long>("select (extract(epoch from now()) * 1000)::bigint"));

    /// <summary>A host over the same database whose clock is a day ahead of that database's own instant.</summary>
    private TenancyApiFactory HostWithClockAhead(UsageQuotaWorld world, DateTimeOffset databaseNow) =>
        TenancyApiFactory.RealResolver(
            _fixture.AppConnectionString(world.Database),
            services: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(databaseNow.AddDays(1)));
            });
}
