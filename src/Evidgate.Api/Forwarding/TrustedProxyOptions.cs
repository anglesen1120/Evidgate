using System.Net;

namespace Evidgate.Api.Forwarding;

/// <summary>
/// Which senders the Api trusts to speak for a client through <c>X-Forwarded-For</c>, bound from
/// <c>EVIDGATE__FORWARDEDHEADERS__*</c>. Both lists are comma-separated; the whole setting is read once at start.
/// </summary>
public sealed class TrustedProxyOptions
{
    /// <summary>Configuration section bound to <c>EVIDGATE__FORWARDEDHEADERS__*</c>.</summary>
    public const string SectionName = "EVIDGATE:ForwardedHeaders";

    /// <summary>The explicit "this Api is not behind a proxy" value of <see cref="KnownProxies"/>.</summary>
    public const string None = "none";

    /// <summary>Comma-separated proxy IP addresses, or <see cref="None"/> when no proxy sits in front of the Api.</summary>
    public string? KnownProxies { get; init; }

    /// <summary>Comma-separated proxy networks in CIDR form, e.g. <c>10.0.0.0/8</c>.</summary>
    public string? KnownNetworks { get; init; }

    /// <summary>True when the setting says no proxy is in front of the Api.</summary>
    public bool DeclaredDirect =>
        string.Equals(KnownProxies?.Trim(), None, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(KnownNetworks);

    /// <summary>True when at least one proxy or network is configured (and the setting is not <see cref="None"/>).</summary>
    public bool HasTrust => !DeclaredDirect && (Split(KnownProxies).Count > 0 || Split(KnownNetworks).Count > 0);

    /// <summary>The parsed proxy addresses; a malformed entry is reported by <see cref="Problems"/>.</summary>
    public IReadOnlyList<IPAddress> ParseProxies() =>
        DeclaredDirect ? [] : [.. Split(KnownProxies).Select(s => TryAddress(s, out var ip) ? ip : null).OfType<IPAddress>()];

    /// <summary>The parsed proxy networks; a malformed entry is reported by <see cref="Problems"/>.</summary>
    public IReadOnlyList<IPNetwork> ParseNetworks() =>
        [.. Split(KnownNetworks).Select(s => TryNetwork(s, out var net) ? net : (IPNetwork?)null).OfType<IPNetwork>()];

    /// <summary>Entries that do not parse, as <c>key: value</c> lines for the start-up message.</summary>
    public IEnumerable<string> Problems()
    {
        if (!DeclaredDirect)
        {
            foreach (var entry in Split(KnownProxies).Where(s => !TryAddress(s, out _)))
            {
                yield return $"EVIDGATE__FORWARDEDHEADERS__KNOWNPROXIES: '{entry}' is not an IP address";
            }
        }

        foreach (var entry in Split(KnownNetworks).Where(s => !TryNetwork(s, out _)))
        {
            yield return $"EVIDGATE__FORWARDEDHEADERS__KNOWNNETWORKS: '{entry}' is not a CIDR network such as 10.0.0.0/8 (a /0 network would trust every sender)";
        }
    }

    // The framework parser also takes short forms ("10.1" is 10.0.0.1), which would trust a host nobody named.
    private static bool TryAddress(string value, out IPAddress address) =>
        IPAddress.TryParse(value, out address!) && (value.Contains(':') || value.Count(c => c == '.') == 3);

    // A zero-length prefix matches every sender, so it can only be a typo.
    private static bool TryNetwork(string value, out IPNetwork network) =>
        IPNetwork.TryParse(value, out network) && network.PrefixLength > 0;

    private static List<string> Split(string? value) =>
        [.. (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
