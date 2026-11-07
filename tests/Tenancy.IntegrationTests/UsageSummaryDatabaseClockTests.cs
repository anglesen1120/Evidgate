using System.Globalization;
using System.Net;
using System.Text.Json;
using Evidgate.Modules.Usage.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The tenant usage summary against a real PostgreSQL while the host's own clock runs a month ahead of it: the period
/// and the wait beside a blocked counter are the reading transaction's clock, the clock the gate writes counters with,
/// so the skewed host still answers the month its counters live in.
/// </summary>
[Trait("Category", "Docker")]
public sealed class UsageSummaryDatabaseClockTests(PostgresTenancyFixture fixture)
{
    private const decimal Limit = 10m;

    private readonly PostgresTenancyFixture _fixture = fixture;

    [Fact]
    public async Task Summary_HostClockAMonthAhead_AnswersTheDatabaseMonthAndItsRetryHint()
    {
        await using var world = await UsageReadsWorld.StartAsync(_fixture, OperatorGateMode.Permitted);

        // A host whose clock already names a later month, so only the database can name the month of the counters.
        var ahead = DateTimeOffset.UtcNow.AddMonths(1);
        await using var skewed = world.HostAt(ahead);
        using var skewedClient = skewed.CreateClient();

        var (period, instant) = await world.DatabaseClockAsync();
        Month(ahead).ShouldNotBe(period);
        await world.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), null, period, UsageLedgerMetricNames.Results, Limit, Limit, BlockedAt: instant));

        await AssertDatabaseClockAsync(world, skewedClient, period);

        // The positive control: only the host clock differs, and a host reading the database's own clock answers the
        // same month and the same wait.
        await AssertDatabaseClockAsync(world, world.Client, period);
    }

    /// <summary>
    /// Reads the summary as the world's member on <paramref name="client"/> and asserts the month, the blocked counter
    /// and a wait bracketed by the database instants read around the request.
    /// </summary>
    /// <param name="world">The world whose org, user and route the request names.</param>
    /// <param name="client">The host's client the request is sent on.</param>
    /// <param name="period">The database month the counters live in.</param>
    private static async Task AssertDatabaseClockAsync(UsageReadsWorld world, HttpClient client, string period)
    {
        var (_, before) = await world.DatabaseClockAsync();
        var answer = await client.SendAsync(
            HttpMethod.Get,
            world.SummaryUrl(),
            TenancyApiFactory.Resolved(user: world.User, orgHeader: world.Org));
        var (periodAfter, after) = await world.DatabaseClockAsync();

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("period").GetString().ShouldBe(period);
        periodAfter.ShouldBe(period);

        var results = Metric(answer, UsageLedgerMetricNames.Results);
        results.GetProperty("used").GetDecimal().ShouldBe(Limit);
        results.GetProperty("limit").GetDecimal().ShouldBe(Limit);
        results.GetProperty("blocked_at").GetString().ShouldNotBeNullOrEmpty();

        // The reading transaction's instant lies between the two database reads, so its wait lies between their windows.
        results.GetProperty("retry_after_seconds").GetInt64().ShouldBeInRange(
            ExpectedRetrySeconds(period, after) - 1,
            ExpectedRetrySeconds(period, before) + 1);
    }

    /// <summary>The whole seconds from <paramref name="instant"/> to the next month, the gate's own wait until rollover.</summary>
    private static long ExpectedRetrySeconds(string period, DateTimeOffset instant)
    {
        var year = int.Parse(period.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture);
        var month = int.Parse(period.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        var next = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);

        return (long)Math.Ceiling((next - instant).TotalSeconds);
    }

    private static string Month(DateTimeOffset instant) =>
        instant.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static JsonElement Metric(ApiAnswer answer, string wire) =>
        answer.Body.GetProperty("metrics").EnumerateArray()
            .Single(metric => metric.GetProperty("metric").GetString() == wire);
}
