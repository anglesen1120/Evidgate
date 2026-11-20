using Evidgate.Testing;
using Xunit;

namespace Hosts.IntegrationTests;

public sealed partial class OptionsValidationTests
{
    /// <summary>
    /// A limiter budget the store cannot honour stops the Worker at boot too (it composes the same Identity module),
    /// with the configuration key named and no stack trace.
    /// </summary>
    /// <param name="environment">The host environment; Production is the deployed stance.</param>
    /// <param name="variable">The environment variable that carries the bad value.</param>
    /// <param name="value">The refused value.</param>
    /// <param name="named">The configuration key the refusal must name.</param>
    [Theory]
    [InlineData("Production", "EVIDGATE__IDENTITY__RATELIMITS__LOGINLIMIT", "0", "EVIDGATE:Identity:RateLimits:LoginLimit")]
    [InlineData("Production", "EVIDGATE__IDENTITY__RATELIMITS__LINKWINDOWSECONDS", "86401", "EVIDGATE:Identity:RateLimits:LinkWindowSeconds")]
    [InlineData("Production", "EVIDGATE__IDENTITY__RATELIMITS__INVITEMAXKEYS", "1000001", "EVIDGATE:Identity:RateLimits:InviteMaxKeys")]
    [InlineData("Production", "EVIDGATE__IDENTITY__RATELIMITS__CIEXCHANGELIMIT", "1000001", "EVIDGATE:Identity:RateLimits:CiExchangeLimit")]
    [InlineData("Development", "EVIDGATE__IDENTITY__RATELIMITS__LOGINLIMIT", "0", "EVIDGATE:Identity:RateLimits:LoginLimit")]
    [InlineData("Development", "EVIDGATE__IDENTITY__RATELIMITS__CIEXCHANGEMAXKEYS", "0", "EVIDGATE:Identity:RateLimits:CiExchangeMaxKeys")]
    public async Task Invalid_Rate_Limit_Option_Fails_Worker_Startup_Cleanly(
        string environment, string variable, string value, string named)
    {
        var result = await RunUntilExitAsync(
            HostLocator.AppHost("Evidgate.Worker"),
            WorkerEnvironment(environment, variable, value),
            statingNoHealingProvider: true);

        var output = result.Stdout + result.Stderr;
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Configuration invalid:", output, StringComparison.Ordinal);
        Assert.Contains(named, output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", output, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> WorkerEnvironment(string environment, string variable, string value)
    {
        // The queue credential is resolved before the limiter group, so the baseline carries a well-formed one.
        var env = WorkerChildEnvironment.Build(
            DeadConnectionString,
            WorkerChildEnvironment.Unreachable,
            HostLocator.FreePort(),
            isolationSelfCheckDisabled: true);
        env["DOTNET_ENVIRONMENT"] = environment;
        env[variable] = value;

        return env;
    }
}
