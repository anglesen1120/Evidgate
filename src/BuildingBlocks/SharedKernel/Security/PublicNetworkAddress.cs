using System.Net;
using System.Net.Sockets;

namespace Evidgate.BuildingBlocks.SharedKernel.Security;

/// <summary>
/// The one judgement of "may a public service be reached here": host names and IP addresses of the platform's own
/// network, of loopback, link-local, private, shared, multicast, reserved and documentation space are refused. Both
/// outbound egress guards (healing provider, webhook endpoints) call it so the ranges cannot drift apart.
/// </summary>
public static class PublicNetworkAddress
{
    /// <summary>Whether the host of <paramref name="uri"/> is a blocked name or a blocked IP literal; resolution is not judged.</summary>
    /// <param name="uri">An absolute URI.</param>
    public static bool IsBlockedHost(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        // The DNS root dot names the same host, so it goes before any literal or name test.
        var host = uri.IdnHost.TrimEnd('.');

        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) ? IsBlocked(ip) : IsBlockedName(host);
    }

    /// <summary>Whether a DNS name cannot be a public one: unqualified, or under a local-only suffix.</summary>
    /// <param name="host">The host without its trailing dot.</param>
    public static bool IsBlockedName(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        host = host.TrimEnd('.');

        return host.Length == 0
            || !host.Contains('.', StringComparison.Ordinal)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="ip"/> is outside public unicast space. An IPv4-mapped IPv6 address is judged as its IPv4
    /// address; IPv6 outside 2000::/3, the documentation, Teredo and benchmark prefixes, and 6to4 are all blocked.
    /// </summary>
    /// <param name="ip">The address.</param>
    public static bool IsBlocked(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        var bytes = ip.GetAddressBytes();

        return ip.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsBlockedV4(bytes),
            AddressFamily.InterNetworkV6 => IsBlockedV6(bytes),
            _ => true,
        };
    }

    private static bool IsBlockedV4(byte[] b) =>
        b[0] is 0 or 10 or 127 or >= 224
        || (b[0] == 100 && b[1] is >= 64 and <= 127)
        || (b[0] == 169 && b[1] == 254)
        || (b[0] == 172 && b[1] is >= 16 and <= 31)
        || (b[0] == 192 && b[1] == 168)
        || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)
        || (b[0] == 192 && b[1] == 88 && b[2] == 99)
        || (b[0] == 198 && (b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100)))
        || (b[0] == 203 && b[1] == 0 && b[2] == 113);

    private static bool IsBlockedV6(byte[] b) =>
        // Only global unicast 2000::/3 can be public. 2001::/23 (Teredo, benchmarking, protocol assignments),
        // 2001:db8::/32 and 2002::/16 (6to4, which hides an IPv4 address) are not. NAT64, ULA, link-local and
        // multicast fall outside 2000::/3 already.
        (b[0] & 0xE0) != 0x20
        || (b[0] == 0x20 && b[1] == 0x01 && b[2] < 0x02)
        || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)
        || (b[0] == 0x20 && b[1] == 0x02);
}
