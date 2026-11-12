using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// AC1: `Evidgate.Api` and `Evidgate.Worker` both boot from the same solution
/// and module set as real processes, and each answers its readiness endpoint.
/// </summary>
/// <remarks>
/// The database here is the skeleton of <see cref="PostgresFixture.CreateDatabaseAtHeadAsync"/>: the journal and
/// nothing else. The fail-closed boot policy (the RLS catalogue self-check and the Data Protection ring of the
/// shared-keys migration) therefore cannot pass on it — neither table exists — so both processes opt out with the
/// same setting every other database-less host test uses, and that policy stays proven where it has a schema:
/// <see cref="IsolationSelfCheckBootTests"/> (real processes, migrated database) and
/// <see cref="SharedKeyRingTests"/> (a host whose key store is unreachable refuses to start). Both children's
/// redirected output is read and reported when a host does not answer: an undrained pipe can stall a host, and the
/// captured text is the only evidence of why it stopped serving.
/// </remarks>
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
                [IsolationSelfCheckEnvironment.Variable] = IsolationSelfCheckEnvironment.Disabled,
                ["EVIDGATE__RUNS__CANCEL__SOFTSTOPDELAY"] = "00:00:30",
                ["EVIDGATE__RUNS__CANCEL__KILLDELAY"] = "00:02:00",
            });

        try
        {
            using var worker = ProcessRunner.Start(
                HostLocator.AppHost("Evidgate.Worker"),
                WorkerChildEnvironment.Build(
                    connectionString, connectionString.WorkerConnectionString, workerPort, isolationSelfCheckDisabled: true));

            try
            {
                await AssertBothReadyAsync(api, worker, apiPort, workerPort);
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

    /// <summary>
    /// Reads both children's redirected pipes and polls both readiness endpoints until they answer 200. A host that
    /// never answers is stopped first, then the assertion carries that host's captured stdout and stderr: the pipes
    /// are redirected, so an unread stream can stall a host and the text is the only evidence of why it did not serve.
    /// </summary>
    private static async Task AssertBothReadyAsync(System.Diagnostics.Process api, System.Diagnostics.Process worker, int apiPort, int workerPort)
    {
        var apiOutput = ProcessRunner.Drain(api);
        var workerOutput = ProcessRunner.Drain(worker);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var apiReady = await PollForStatusAsync(
            client, $"http://127.0.0.1:{apiPort}/internal/health/ready", TimeSpan.FromSeconds(30));
        if (apiReady != 200)
        {
            await ProcessRunner.StopAsync(worker);
            await ProcessRunner.StopAsync(api);
            Assert.Fail(
                $"the Api answered {apiReady} instead of 200 on /internal/health/ready within 30 s.{Environment.NewLine}"
                + $"--- Api stdout ---{Environment.NewLine}{await apiOutput.Stdout}{Environment.NewLine}"
                + $"--- Api stderr ---{Environment.NewLine}{await apiOutput.Stderr}{Environment.NewLine}"
                + $"--- Worker stdout ---{Environment.NewLine}{await workerOutput.Stdout}{Environment.NewLine}"
                + $"--- Worker stderr ---{Environment.NewLine}{await workerOutput.Stderr}");
        }

        var workerReady = await PollForStatusAsync(
            client, $"http://127.0.0.1:{workerPort}/internal/health/ready", TimeSpan.FromSeconds(30));
        if (workerReady != 200)
        {
            await ProcessRunner.StopAsync(worker);
            await ProcessRunner.StopAsync(api);
            Assert.Fail(
                $"the Worker answered {workerReady} instead of 200 on /internal/health/ready within 30 s.{Environment.NewLine}"
                + $"--- Worker stdout ---{Environment.NewLine}{await workerOutput.Stdout}{Environment.NewLine}"
                + $"--- Worker stderr ---{Environment.NewLine}{await workerOutput.Stderr}");
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
