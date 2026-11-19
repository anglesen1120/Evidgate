using Evidgate.Testing;
using Xunit;

namespace Hosts.IntegrationTests;

public sealed partial class OptionsValidationTests
{
    /// <summary>
    /// A limiter budget the store cannot honour stops the Api at boot with the configuration key named, instead of
    /// failing the first login, link, invite or CI request.
    /// </summary>
    /// <param name="variable">The environment variable that carries the bad value.</param>
    /// <param name="value">The refused value.</param>
    /// <param name="named">The configuration key the refusal must name.</param>
    [Theory]
    [InlineData("EVIDGATE__IDENTITY__RATELIMITS__LOGINLIMIT", "0", "EVIDGATE:Identity:RateLimits:LoginLimit")]
    [InlineData("EVIDGATE__IDENTITY__RATELIMITS__LINKWINDOWSECONDS", "86401", "EVIDGATE:Identity:RateLimits:LinkWindowSeconds")]
    [InlineData("EVIDGATE__IDENTITY__RATELIMITS__INVITEMAXKEYS", "1000001", "EVIDGATE:Identity:RateLimits:InviteMaxKeys")]
    [InlineData("EVIDGATE__IDENTITY__RATELIMITS__CIEXCHANGELIMIT", "1000001", "EVIDGATE:Identity:RateLimits:CiExchangeLimit")]
    public async Task Invalid_Rate_Limit_Option_Fails_Startup_Cleanly(string variable, string value, string named)
    {
        var result = await RunUntilExitAsync(
            HostLocator.AppHost("Evidgate.Api"),
            new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{HostLocator.FreePort()}",
                ["EVIDGATE__DATABASE__CONNECTIONSTRING"] = DeadConnectionString,
                ["EVIDGATE__RUNS__CANCEL__SOFTSTOPDELAY"] = "00:00:30",
                ["EVIDGATE__RUNS__CANCEL__KILLDELAY"] = "00:02:00",
                ["EVIDGATE__HEALING__LLM__PROVIDER"] = "none",
                [variable] = value,
                [IsolationSelfCheckEnvironment.Variable] = IsolationSelfCheckEnvironment.Disabled,
            });

        var output = result.Stdout + result.Stderr;
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Configuration invalid:", output, StringComparison.Ordinal);
        Assert.Contains(named, output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", output, StringComparison.Ordinal);
    }
}
