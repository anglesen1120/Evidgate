using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// AC1 (Api side) / AC3 (ready case) in one boot (T3.4): the Api on a real
/// PostgreSQL at the journal head is ready, and `POST /internal/slice/run` walks
/// the whole stub chain manifest → run → execution plan → event batch →
/// completeness → decision.
/// </summary>
[Collection(PostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class SkeletonSliceTests
{
    private readonly PostgresFixture _postgres;

    public SkeletonSliceTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task End_To_End()
    {
        var connectionString = await _postgres.CreateDatabaseAtHeadAsync(PostgresFixture.ExpectedHead);

        await using var factory = new EvidgateApiFactory(connectionString);
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync("/internal/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        using var response = await client.PostAsJsonAsync(
            "/internal/slice/run",
            new { project_ref = "slice/test", event_count = 2 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;

        var manifestId = root.GetProperty("manifest_id").GetString();
        var runId = root.GetProperty("run_id").GetString();
        Assert.False(string.IsNullOrEmpty(manifestId));
        Assert.False(string.IsNullOrEmpty(runId));
        Assert.True(Guid.TryParse(manifestId, out _), "manifest_id is a UUIDv7 string");
        Assert.True(Guid.TryParse(runId, out _), "run_id is a UUIDv7 string");
        Assert.StartsWith("sha256:", root.GetProperty("manifest_hash").GetString(), StringComparison.Ordinal);

        // Two stub suite revisions → two planned executions.
        Assert.Equal(2, root.GetProperty("execution_ids").GetArrayLength());
        Assert.Equal(2, root.GetProperty("accepted_count").GetInt32());
        Assert.Equal("incomplete", root.GetProperty("completeness").GetString());
        Assert.Equal("pending", root.GetProperty("decision").GetString());
    }
}
