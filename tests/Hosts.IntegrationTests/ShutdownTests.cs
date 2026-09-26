using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// AC1 + T1.4: a real Api or Worker process sent SIGTERM must exit cleanly
/// (code 0) well inside the 10 s <c>HostOptions.ShutdownTimeout</c>. The probe
/// URL is the liveness endpoint — shutdown must not depend on the database.
/// </summary>
public sealed class ShutdownTests
{
    private const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=x;Password=y;Database=evidgate;Timeout=2";

    [Fact]
    public async Task Sigterm_Exits_Under_10s()
    {
        var apiPort = HostLocator.FreePort();
        var apiResult = await ProcessRunner.RunThenSigtermAsync(
            HostLocator.AppHost("Evidgate.Api"),
            new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{apiPort}",
                ["DOTNET_ENVIRONMENT"] = "Production",
                ["EVIDGATE__DATABASE__CONNECTIONSTRING"] = DeadConnectionString,
            },
            $"http://127.0.0.1:{apiPort}/internal/health/live",
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, apiResult.ExitCode);
        Assert.True(
            apiResult.ShutdownElapsed < TimeSpan.FromSeconds(10),
            $"Api SIGTERM -> exit took {apiResult.ShutdownElapsed.TotalSeconds:0.##}s, must stay under 10 s");

        var workerPort = HostLocator.FreePort();
        var workerResult = await ProcessRunner.RunThenSigtermAsync(
            HostLocator.AppHost("Evidgate.Worker"),
            new Dictionary<string, string>
            {
                ["DOTNET_ENVIRONMENT"] = "Production",
                ["EVIDGATE__DATABASE__CONNECTIONSTRING"] = DeadConnectionString,
                ["EVIDGATE__WORKER__HEALTHPORT"] =
                    workerPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["EVIDGATE__WORKER__READINESSPOLLMILLISECONDS"] = "100",
            },
            $"http://127.0.0.1:{workerPort}/internal/health/live",
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, workerResult.ExitCode);
        Assert.True(
            workerResult.ShutdownElapsed < TimeSpan.FromSeconds(10),
            $"Worker SIGTERM -> exit took {workerResult.ShutdownElapsed.TotalSeconds:0.##}s, must stay under 10 s");
    }
}
