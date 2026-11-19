using Evidgate.Testing;
using Xunit;

namespace Hosts.IntegrationTests;

public sealed partial class OptionsValidationTests
{
    /// <summary>
    /// A limiter budget the store cannot honour stops the Api at boot with the configuration key named and no stack
    /// trace, instead of failing the first login, link, invite or CI request.
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
    public async Task Invalid_Rate_Limit_Option_Fails_Startup_Cleanly(
        string environment, string variable, string value, string named)
    {
        var result = await RunUntilExitAsync(
            HostLocator.AppHost("Evidgate.Api"),
            new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{HostLocator.FreePort()}",
                ["ASPNETCORE_ENVIRONMENT"] = environment,
                ["EVIDGATE__DATABASE__CONNECTIONSTRING"] = DeadConnectionString,
                ["EVIDGATE__RUNS__CANCEL__SOFTSTOPDELAY"] = "00:00:30",
                ["EVIDGATE__RUNS__CANCEL__KILLDELAY"] = "00:02:00",
                [variable] = value,
                [IsolationSelfCheckEnvironment.Variable] = IsolationSelfCheckEnvironment.Disabled,
            },
            statingNoHealingProvider: true);

        var output = result.Stdout + result.Stderr;
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Configuration invalid:", output, StringComparison.Ordinal);
        Assert.Contains(named, output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", output, StringComparison.Ordinal);
    }
}
