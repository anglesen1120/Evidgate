using System.Net;

namespace Evidgate.Modules.Identity.Ci;

/// <summary>
/// The one place that reads the caller's address. Behind a trusted proxy the host's forwarded-headers
/// middleware has already replaced <c>Connection.RemoteIpAddress</c> with the client's, so limiter keys,
/// session rows and audit rows all agree on the same value and never on the proxy's.
/// </summary>
internal static class ClientIp
{
    /// <summary>The caller's address, or null when the transport has none.</summary>
    public static IPAddress? Of(HttpContext context) => context.Connection.RemoteIpAddress;

    /// <summary>The rate-limit key of the caller (IPv6 reduced to its /64).</summary>
    public static string LimiterKey(HttpContext context) => ExchangeRateLimiter.ClientKey(Of(context));

    /// <summary>The caller's address as the text a session or audit row stores, or null.</summary>
    public static string? Text(HttpContext context) => Of(context)?.ToString();
}
