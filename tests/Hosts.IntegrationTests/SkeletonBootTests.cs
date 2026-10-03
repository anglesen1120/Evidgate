using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// AC1: `Evidgate.Api` and `Evidgate.Worker` both boot from the same solution
/// and module set (real processes; the Worker also proves its readiness
/// endpoint answers and its slice consumer logs the same run chain). T1.1/T1.3/T3.3.
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

            // T3.3: the Worker's consumer runs the same command chain once ready
            // and logs the run_id — read the line while the process is alive.
            string workerLog;
            try
            {
                workerLog = await WaitForLineAsync(
                    worker.StandardOutput,
                    "Slice chain complete: run_id ",
                    TimeSpan.FromSeconds(30));
            }
            catch (InvalidOperationException exception)
            {
                // The stream ended: say why (the worker's own error output) instead of only that it ended.
                var errors = worker.HasExited ? await worker.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken) : string.Empty;

                throw new InvalidOperationException($"{exception.Message}; worker exit {(worker.HasExited ? worker.ExitCode : -1)}; stderr: {errors}", exception);
            }
            Assert.Contains("run_id", workerLog, StringComparison.Ordinal);
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

    private static async Task<string> WaitForLineAsync(StreamReader reader, string contains, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        var seen = new List<string>();
        while (true)
        {
            var line = await reader.ReadLineAsync(deadline.Token);
            if (line is null)
            {
                throw new InvalidOperationException($"process output ended before '{contains}' appeared; it said: {string.Join(" | ", seen.TakeLast(20))}");
            }

            seen.Add(line);

            if (line.Contains(contains, StringComparison.Ordinal))
            {
                return line;
            }
        }
    }
}
