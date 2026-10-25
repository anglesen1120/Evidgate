using System.Net;
using System.Net.Sockets;

namespace Evidgate.Modules.Identity.Login.Completion;

/// <summary>
/// The client address as the <c>session.ip</c> column (<c>inet</c>) accepts it (02.3.1 §5, hand-over (vi)
/// of #160): <c>inet</c> has no zone index, so an IPv6 address that arrives scoped — <c>fe80::1%eth0</c>
/// from a socket, <c>fe80::1%6</c> from a header — is stored without it rather than failing the insert.
/// </summary>
/// <remarks>
/// Anything that is not an IP address is dropped (null, never echoed): the value is client data on its way
/// into a column, and a session without an address is a supported row (<c>ip inet</c> is nullable), while a
/// malformed literal would be an insert failure after the user and the identity were already written.
/// </remarks>
internal static class ClientAddress
{
    /// <summary>The zone-index separator of a scoped IPv6 literal.</summary>
    private const char ZoneSeparator = '%';

    /// <summary>
    /// <paramref name="address"/> without its zone index when it parses as an IP address, otherwise null.
    /// </summary>
    /// <param name="address">The address the request reported (<c>RemoteIpAddress.ToString()</c>), or null.</param>
    /// <returns>A canonical literal (<c>inet</c>-safe), or null when nothing usable was presented.</returns>
    public static string? Sanitise(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var candidate = address.Trim();

        if (IPAddress.TryParse(candidate, out var parsed))
        {
            return Canonical(parsed);
        }

        // A zone NAME (`fe80::1%eth0`) is the one form the parser refuses: the index is exactly what the
        // column cannot take, so dropping it is the whole job.
        var zone = candidate.IndexOf(ZoneSeparator, StringComparison.Ordinal);

        return zone > 0 && IPAddress.TryParse(candidate[..zone], out parsed) ? Canonical(parsed) : null;
    }

    /// <summary>The literal of <paramref name="address"/> with any IPv6 scope id cleared.</summary>
    private static string Canonical(IPAddress address)
    {
        // ScopeId throws for IPv4, so the family check is the guard, not a formality.
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
        {
            return new IPAddress(address.GetAddressBytes()).ToString();
        }

        return address.ToString();
    }
}
