using System.Net;
using System.Net.Sockets;
using Evidgate.BuildingBlocks.SharedKernel.Security;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Policy.Webhooks.Application;

/// <summary>Settings of <see cref="WebhookUrlGuard"/>.</summary>
internal sealed class WebhookUrlOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Webhooks:Url";

    /// <summary>Ports other than 443 an operator allows; empty by default.</summary>
    public int[] AllowedPorts { get; set; } = [];
}

/// <summary>A URL the guard accepted and the addresses it validated; a connection may only go to one of them.</summary>
/// <param name="Uri">The absolute https URL.</param>
/// <param name="Addresses">Every resolved address, all public.</param>
internal sealed record WebhookTarget(Uri Uri, IReadOnlyList<IPAddress> Addresses);

/// <summary>The guard's answer: a target, or the class of rule that refused it.</summary>
/// <param name="Target">The validated target; null when refused.</param>
/// <param name="Reason">One of <see cref="WebhookUrlReasons"/>; null when accepted.</param>
internal sealed record WebhookUrlCheck(WebhookTarget? Target, string? Reason)
{
    /// <summary>Whether the URL may be used.</summary>
    public bool Allowed => Target is not null;
}

/// <summary>Rule classes a refusal names; none carries an address.</summary>
internal static class WebhookUrlReasons
{
    /// <summary>Not an absolute https URL of bounded length.</summary>
    public const string Scheme = "scheme";

    /// <summary>The authority carries userinfo.</summary>
    public const string Userinfo = "userinfo";

    /// <summary>Neither 443 nor an allow-listed port.</summary>
    public const string Port = "port";

    /// <summary>A local-only or unqualified name, or a blocked IP literal.</summary>
    public const string Host = "host";

    /// <summary>A resolved address is not public.</summary>
    public const string Address = "address";

    /// <summary>The name did not resolve.</summary>
    public const string Resolution = "resolution";
}

/// <summary>
/// The single SSRF guard for webhook targets: registration and every delivery call it. It checks the URL shape,
/// resolves the host and refuses when any answer is non-public, so a mixed answer cannot be used to pick the private one.
/// </summary>
internal sealed class WebhookUrlGuard(IWebhookHostResolver resolver, IOptions<WebhookUrlOptions> options)
{
    private const int MaxUrlLength = 2048;

    /// <summary>Validates <paramref name="url"/> and resolves its host.</summary>
    /// <param name="url">The URL as registered.</param>
    /// <param name="cancellationToken">Cancels name resolution.</param>
    public async Task<WebhookUrlCheck> CheckAsync(string? url, CancellationToken cancellationToken)
    {
        var shape = CheckShape(url, out var uri);

        if (shape is not null)
        {
            return Refuse(shape);
        }

        // IdnHost is the normalised form: decimal, hex, octal and short IPv4 spellings already read as dotted quads.
        var host = uri!.IdnHost.TrimEnd('.');
        var literal = IPAddress.TryParse(host.Trim('[', ']'), out var ip);

        if (!literal && PublicNetworkAddress.IsBlockedName(host))
        {
            return Refuse(WebhookUrlReasons.Host);
        }

        IPAddress[] addresses;

        if (literal)
        {
            addresses = [ip!];
        }
        else
        {
            try
            {
                addresses = [.. (await resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false)).Distinct()];
            }
            catch (Exception failure) when (failure is SocketException or ArgumentException)
            {
                return Refuse(WebhookUrlReasons.Resolution);
            }
        }

        if (addresses.Length == 0)
        {
            return Refuse(WebhookUrlReasons.Resolution);
        }

        return addresses.Any(PublicNetworkAddress.IsBlocked)
            ? Refuse(literal ? WebhookUrlReasons.Host : WebhookUrlReasons.Address)
            : new WebhookUrlCheck(new WebhookTarget(uri, addresses), null);
    }

    private static WebhookUrlCheck Refuse(string reason) => new(null, reason);

    private string? CheckShape(string? url, out Uri? uri)
    {
        uri = null;

        if (string.IsNullOrWhiteSpace(url)
            || url.Length > MaxUrlLength
            || !Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps)
        {
            return WebhookUrlReasons.Scheme;
        }

        // An empty userinfo ("https://@host") leaves UserInfo blank, so the raw authority is inspected too.
        if (parsed.UserInfo.Length > 0 || HasUserinfoMarker(url))
        {
            return WebhookUrlReasons.Userinfo;
        }

        if (parsed.Port != 443 && !options.Value.AllowedPorts.Contains(parsed.Port))
        {
            return WebhookUrlReasons.Port;
        }

        uri = parsed;

        return null;
    }

    private static bool HasUserinfoMarker(string url)
    {
        var start = url.IndexOf("://", StringComparison.Ordinal) + 3;
        var end = url.IndexOfAny(['/', '?', '#'], start);

        return url.AsSpan(start, (end < 0 ? url.Length : end) - start).Contains('@');
    }
}
