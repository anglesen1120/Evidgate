using Evidgate.Modules.Authoring.Trackers.Application;

namespace Evidgate.Modules.Authoring.Trackers.Infrastructure;

/// <summary>
/// The fixed provider endpoints. They are code constants, not configuration: an override pointing anywhere else would be
/// a configuration attack, so the only configurable parts of a provider are its client and its own callback.
/// </summary>
internal static class TrackerEndpoints
{
    internal const string JiraAuthorizeUrl = "https://auth.atlassian.com/authorize";
    internal const string JiraTokenUrl = "https://auth.atlassian.com/oauth/token";
    internal const string JiraAudience = "api.atlassian.com";
    internal const string LinearAuthorizeUrl = "https://linear.app/oauth/authorize";
    internal const string LinearTokenUrl = "https://api.linear.app/oauth/token";

    /// <summary>The only hosts the server may call, and the only hosts a consent URL may name.</summary>
    internal static readonly string[] Hosts = ["auth.atlassian.com", "api.atlassian.com", "api.linear.app", "linear.app"];

    /// <summary>The path of the provider's callback on this API.</summary>
    internal static string CallbackPath(TrackerProvider provider) =>
        $"/v1/tracker-connections/{TrackerProviders.ToWire(provider)}/callback";

    /// <summary>True when <paramref name="uri"/> is https, has no userinfo, keeps the default port and names an allowlisted host.</summary>
    internal static bool IsAllowed(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.UserInfo.Length == 0
        && uri.IsDefaultPort
        && Array.Exists(Hosts, host => string.Equals(host, uri.Host, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Refuses, before anything is sent, a request whose destination is not on the fixed provider list. A refusal names
/// no host and no value: the request may carry a credential.
/// </summary>
internal sealed class TrackerOutboundAllowlistHandler : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TrackerEndpoints.IsAllowed(request.RequestUri))
        {
            throw new InvalidOperationException("The tracker client refused a destination outside its fixed provider hosts.");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
