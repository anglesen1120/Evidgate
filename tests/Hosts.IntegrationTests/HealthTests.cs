using System.Net;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// AC3 (T1.3): <c>/internal/health/live</c> always 200; <c>/internal/health/ready</c>
/// is green only when the DB is reachable AND the journal head is at or beyond
/// the build's expected head — a head strictly older (or a missing/unreadable
/// journal) is 503 with a distinct reason, and "not ready" never creates the
/// journal. The probe runs as the least-privilege <c>evidgate_app</c> role.
/// </summary>
[Collection(PostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class HealthTests
{
    private readonly PostgresFixture _postgres;

    public HealthTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Ready_503_When_Db_Down()
    {
        // Valid shape, nothing listening on the port.
        var deadConnectionString = "Host=127.0.0.1;Port=1;Username=x;Password=y;Database=evidgate;Timeout=2";

        await using var factory = new EvidgateApiFactory(deadConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/internal/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("database_unreachable", body, StringComparison.Ordinal);

        // 00 §9.2: a 503 carries Retry-After.
        Assert.Equal("5", response.Headers.RetryAfter?.ToString());
    }

    [Fact]
    public async Task Ready_503_When_Journal_Behind()
    {
        // The head "0000_behind.sql" sorts before any real migration (foundations
        // own 0001+), so it is always behind the baked expected head.
        var connectionString = await _postgres.CreateDatabaseAtHeadAsync("0000_behind.sql");

        await using var factory = new EvidgateApiFactory(connectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/internal/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("journal_behind", body, StringComparison.Ordinal);
        Assert.Equal("5", response.Headers.RetryAfter?.ToString());
    }

    [Fact]
    public async Task Ready_200_When_Journal_Ahead()
    {
        // A journal newer than this build is the normal state during/after an
        // expand/contract rollout (01.4.1 §11): the old code keeps serving (AC3).
        var connectionString = await _postgres.CreateDatabaseAtHeadAsync("9999_foundation_future.sql");

        await using var factory = new EvidgateApiFactory(connectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/internal/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"status\":\"ready\"", body, StringComparison.Ordinal);
        Assert.Contains("journal_ahead", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ready_503_Check_Failed_When_Journal_Grant_Revoked()
    {
        // The database is reachable and the head matches, but the application role
        // cannot read the journal: that is a distinct, actionable reason (D8), not
        // "database_unreachable".
        var connectionString = await _postgres.CreateDatabaseAtHeadAsync(PostgresFixture.ExpectedHead);
        await PostgresFixture.RevokeJournalSelectAsync(connectionString);

        await using var factory = new EvidgateApiFactory(connectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/internal/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("check_failed", body, StringComparison.Ordinal);
        Assert.DoesNotContain("database_unreachable", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Live_Always_200()
    {
        // No database at all — liveness must still answer 200 (AC3).
        await using var factory = new EvidgateApiFactory("Host=127.0.0.1;Port=1;Username=x;Password=y;Database=evidgate;Timeout=2");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/internal/health/live", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
