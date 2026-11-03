using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// AC1: `Evidgate.Api` and `Evidgate.Worker` both boot from the same solution
/// and module set as real processes, and each answers its readiness endpoint.
/// </summary>
[Trait("Category", "Docker")]
public sealed class SkeletonBootTests
{
    private readonly PostgresFixture _postgres;

    public SkeletonBootTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Api_And_Worker_Start()
    {
        await using var connectionString = await _postgres.CreateDatabaseAtHeadAsync(PostgresFixture.ExpectedHead);

        var apiPort = HostLocator.FreePort();
        var workerPort = HostLocator.FreePort();

        using var api = ProcessRunner.Start(
            HostLocator.AppHost("Evidgate.Api"),
            new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{apiPort}",
                ["DOTNET_ENVIRONMENT"] = "Production",
                ["EVIDGATE__DATABASE__CONNECTIONSTRING"] = connectionString,
            });

        try
        {
            using var worker = ProcessRunner.Start(
                HostLocator.AppHost("Evidgate.Worker"),
                new Dictionary<string, string>
                {
                    ["DOTNET_ENVIRONMENT"] = "Production",
                    ["EVIDGATE__DATABASE__CONNECTIONSTRING"] = connectionString,
                    ["EVIDGATE__WORKER__HEALTHPORT"] = workerPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["EVIDGATE__WORKER__READINESSPOLLMILLISECONDS"] = "100",
                });

            try
            {
                await AssertBothReadyAsync(apiPort, workerPort);
            }
            finally
            {
                await ProcessRunner.StopAsync(worker);
            }
        }
        finally
        {
            // A Worker that never started must still not leave the Api running.
            await ProcessRunner.StopAsync(api);
        }
    }

    /// <summary>Polls both readiness endpoints until they answer 200.</summary>
    private static async Task AssertBothReadyAsync(int apiPort, int workerPort)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var apiReady = await PollForStatusAsync(
            client, $"http://127.0.0.1:{apiPort}/internal/health/ready", TimeSpan.FromSeconds(30));
        Assert.Equal(200, apiReady);

        var workerReady = await PollForStatusAsync(
            client, $"http://127.0.0.1:{workerPort}/internal/health/ready", TimeSpan.FromSeconds(30));
        Assert.Equal(200, workerReady);
    }

    private static async Task<int> PollForStatusAsync(HttpClient client, string url, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = -1;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(url);
                last = (int)response.StatusCode;
                if (last == 200)
                {
                    return last;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(250);
        }

        return last;
    }

}
