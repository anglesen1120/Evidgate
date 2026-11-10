using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Evidgate.Api.Forwarding;

/// <summary>
/// Fails the start of an Api that has no stated stance on proxies: outside Development, either a trusted
/// proxy/network is configured or <c>KNOWNPROXIES=none</c> says there is none. Silence is refused because the
/// per-client rate limits, session rows and audit rows would then key every client on the proxy's address.
/// </summary>
public sealed class TrustedProxyOptionsValidator(IHostEnvironment environment) : IValidateOptions<TrustedProxyOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TrustedProxyOptions options)
    {
        var problems = options.Problems().ToList();

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
