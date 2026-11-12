using System.Net;
using System.Net.Sockets;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// The egress allowlist of the healing adapter: the only endpoint a request may reach is the configured base address,
/// and outside Development that address may not be an internal one.
/// </summary>
internal static class HealingLlmEgress
{
    /// <summary>Whether <paramref name="uri"/> is the configured endpoint or a path under it (same scheme, host and port).</summary>
    /// <param name="baseAddress">The validated base address.</param>
    /// <param name="uri">The request target.</param>
    internal static bool IsAllowed(Uri baseAddress, Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && uri.UserInfo.Length == 0
        && uri.Scheme.Equals(baseAddress.Scheme, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals(baseAddress.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == baseAddress.Port
        && uri.AbsolutePath.StartsWith(baseAddress.AbsolutePath, StringComparison.Ordinal);

    /// <summary>
    /// Whether the host of <paramref name="uri"/> is one a public provider cannot be: an unqualified name, or an IP literal
    /// in a loopback, private, link-local, unique-local or unspecified range. Name resolution is not judged here.
    /// </summary>
    /// <param name="uri">The base address.</param>
    internal static bool IsInternalHost(Uri uri)
    {
        var host = uri.IdnHost;

        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            return IsInternal(ip);
        }

        return !host.Contains('.', StringComparison.Ordinal)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInternal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        var bytes = ip.GetAddressBytes();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                || bytes[0] == 0;
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (bytes[0] & 0xFE) == 0xFC;
    }
}

/// <summary>Refuses a request whose target is not the configured provider endpoint, before any key is resolved for it.</summary>
internal sealed class HealingLlmEgressHandler(HealingProviderOptions options) : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var baseAddress)
            || !HealingLlmEgress.IsAllowed(baseAddress, request.RequestUri))
        {
            // The message names the configuration key only: neither the target nor the payload may reach a log.
            throw new InvalidOperationException(
                $"the request target is not under {HealingProviderKeys.BaseAddress}, so it may not leave");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
