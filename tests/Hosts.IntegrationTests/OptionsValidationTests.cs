using System.Globalization;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// AC2 (T1.2, NFR-01-2): both hosts fail fast with a non-zero exit and a named
/// key when required <c>EVIDGATE__*</c> configuration is missing <em>or invalid</em>
/// — the invalid value must take the same clean, stack-trace-free path (D4).
/// </summary>
public sealed class OptionsValidationTests
{
    private const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=y;Database=evidgate;Timeout=2";

    [Fact]
    public async Task Missing_ConnectionString_Fails_Startup()
    {
        var apiResult = await RunUntilExitAsync(
            HostLocator.AppHost("Evidgate.Api"),
            new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{HostLocator.FreePort()}",
                [IsolationSelfCheckEnvironment.Variable] = IsolationSelfCheckEnvironment.Disabled,
            });
        Assert.NotEqual(0, apiResult.ExitCode);
        Assert.Contains("EVIDGATE__DATABASE__CONNECTIONSTRING", apiResult.Stdout + apiResult.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", apiResult.Stdout + apiResult.Stderr, StringComparison.Ordinal);

        var workerResult = await RunUntilExitAsync(
            HostLocator.AppHost("Evidgate.Worker"),
            new Dictionary<string, string>
            {
                ["EVIDGATE__WORKER__HEALTHPORT"] = HostLocator.FreePort().ToString(CultureInfo.InvariantCulture),
                [IsolationSelfCheckEnvironment.Variable] = IsolationSelfCheckEnvironment.Disabled,
            });
        Assert.NotEqual(0, workerResult.ExitCode);
        Assert.Contains("EVIDGATE__DATABASE__CONNECTIONSTRING", workerResult.Stdout + workerResult.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", workerResult.Stdout + workerResult.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_HealthPort_Fails_Startup_Cleanly()
    {
        var result = await RunUntilExitAsync(
            HostLocator.AppHost("Evidgate.Worker"),
            new Dictionary<string, string>
            {
                ["EVIDGATE__DATABASE__CONNECTIONSTRING"] = DeadConnectionString,
                ["EVIDGATE__WORKER__HEALTHPORT"] = "abc",
                [IsolationSelfCheckEnvironment.Variable] = IsolationSelfCheckEnvironment.Disabled,
            });

        var output = result.Stdout + result.Stderr;
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Configuration invalid:", output, StringComparison.Ordinal);
        Assert.Contains("HealthPort", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unhandled exception", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_HealthAddress_Fails_Startup_Cleanly()
    {
        var result = await RunUntilExitAsync(
            HostLocator.AppHost("Evidgate.Worker"),
            new Dictionary<string, string>
            {
                ["EVIDGATE__DATABASE__CONNECTIONSTRING"] = DeadConnectionString,
                ["EVIDGATE__WORKER__HEALTHPORT"] = HostLocator.FreePort().ToString(CultureInfo.InvariantCulture),
                ["EVIDGATE__WORKER__HEALTHADDRESS"] = "localhost",
                [IsolationSelfCheckEnvironment.Variable] = IsolationSelfCheckEnvironment.Disabled,
            });

        var output = result.Stdout + result.Stderr;
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Configuration invalid:", output, StringComparison.Ordinal);
        Assert.Contains("HEALTHADDRESS", output, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_OtlpEndpoint_Fails_Startup_Cleanly()
    {
        var result = await RunUntilExitAsync(
            HostLocator.AppHost("Evidgate.Api"),
            new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{HostLocator.FreePort()}",
                ["EVIDGATE__DATABASE__CONNECTIONSTRING"] = DeadConnectionString,
                ["EVIDGATE__TELEMETRY__OTLPENDPOINT"] = "not a url",
                [IsolationSelfCheckEnvironment.Variable] = IsolationSelfCheckEnvironment.Disabled,
            });

        var output = result.Stdout + result.Stderr;
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Configuration invalid:", output, StringComparison.Ordinal);
        Assert.Contains("OTLPENDPOINT", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_Sample_Ratio_Fails_Startup_Cleanly()
    {
        var result = await RunUntilExitAsync(
            HostLocator.AppHost("Evidgate.Api"),
            new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{HostLocator.FreePort()}",
                ["EVIDGATE__DATABASE__CONNECTIONSTRING"] = DeadConnectionString,
                ["EVIDGATE__TELEMETRY__SAMPLERATIO"] = "1.5",
                [IsolationSelfCheckEnvironment.Variable] = IsolationSelfCheckEnvironment.Disabled,
            });

        var output = result.Stdout + result.Stderr;
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Configuration invalid:", output, StringComparison.Ordinal);
        Assert.Contains("SAMPLERATIO", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", output, StringComparison.Ordinal);
    }

    private static async Task<ProcessRunner.Result> RunUntilExitAsync(
        string fileName,
        IReadOnlyDictionary<string, string> env)
    {
        var allEvidgateKeys = Environment.GetEnvironmentVariables().Keys
            .Cast<string>()
            .Where(key => key.StartsWith("EVIDGATE__", StringComparison.Ordinal))
            .ToArray();

        using var process = ProcessRunner.Start(fileName, env, allEvidgateKeys);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            return await ProcessRunner.WaitForExitAsync(process, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw new TimeoutException($"{fileName} did not exit within 60 s — validation did not fail fast");
        }
    }
}
