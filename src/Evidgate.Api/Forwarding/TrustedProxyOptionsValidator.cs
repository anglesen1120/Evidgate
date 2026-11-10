using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Evidgate.Api.Forwarding;

/// <summary>
/// Fails the start of an Api that has no stated stance on proxies: outside Development, either a trusted
/// proxy/network is configured or <c>KNOWNPROXIES=none</c> says there is none. Silence is refused because the
/// per-client rate limits, session rows would then key every client on the proxy's address.
/// </summary>
public sealed class TrustedProxyOptionsValidator(IHostEnvironment environment, IConfiguration configuration) : IValidateOptions<TrustedProxyOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TrustedProxyOptions options)
    {
        var problems = options.Problems().ToList();

        // The framework's own switch (ASPNETCORE_FORWARDEDHEADERS_ENABLED) adds a second middleware that
        // clears the known lists and so trusts any sender; the configured lists would no longer decide anything.
        if (!environment.IsDevelopment() && bool.TryParse(configuration["FORWARDEDHEADERS_ENABLED"], out var frameworkSwitch) && frameworkSwitch)
        {
            problems.Add(
                "ASPNETCORE_FORWARDEDHEADERS_ENABLED=true makes the framework trust every sender of X-Forwarded-For; unset it and "
                + "list the proxies in EVIDGATE__FORWARDEDHEADERS__KNOWNPROXIES / EVIDGATE__FORWARDEDHEADERS__KNOWNNETWORKS");
        }

        if (options.KnownProxies?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length > 1
            && options.KnownProxies.Contains(TrustedProxyOptions.None, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("EVIDGATE__FORWARDEDHEADERS__KNOWNPROXIES: 'none' must stand alone, not be mixed with other entries");
        }

        if (!environment.IsDevelopment() && !options.HasTrust && !options.DeclaredDirect)
        {
            problems.Add(
                "EVIDGATE__FORWARDEDHEADERS__KNOWNPROXIES and/or EVIDGATE__FORWARDEDHEADERS__KNOWNNETWORKS must name the "
                + "proxies allowed to set X-Forwarded-For; set EVIDGATE__FORWARDEDHEADERS__KNOWNPROXIES=none only when the Api is not behind a proxy");
        }

        if (options.KnownProxies?.Trim().Equals(TrustedProxyOptions.None, StringComparison.OrdinalIgnoreCase) == true
            && !string.IsNullOrWhiteSpace(options.KnownNetworks))
        {
            problems.Add("EVIDGATE__FORWARDEDHEADERS__KNOWNPROXIES=none contradicts EVIDGATE__FORWARDEDHEADERS__KNOWNNETWORKS");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
