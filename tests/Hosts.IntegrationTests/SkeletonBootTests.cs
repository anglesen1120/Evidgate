using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// AC1: `Evidgate.Api` and `Evidgate.Worker` both boot from the same solution
/// and module set as real processes, and each answers its readiness endpoint.
/// </summary>
[Collection(PostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class SkeletonBootTests
{
    private readonly PostgresFixture _postgres;

    public SkeletonBootTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Api_And_Worker_Start()
    {
        var connectionString = await _postgres.CreateDatabaseAtHeadAsync(PostgresFixture.ExpectedHead);

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
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            var apiReady = await PollForStatusAsync(
                client, $"http://127.0.0.1:{apiPort}/internal/health/ready", TimeSpan.FromSeconds(30));
            Assert.Equal(200, apiReady);

            var workerReady = await PollForStatusAsync(
                client, $"http://127.0.0.1:{workerPort}/internal/health/ready", TimeSpan.FromSeconds(30));
            Assert.Equal(200, workerReady);
        }
        finally
        {
            foreach (var process in new[] { api, worker })
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
        }
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
