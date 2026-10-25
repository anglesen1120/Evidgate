using System.ComponentModel.DataAnnotations;

namespace Evidgate.Modules.Identity.Login.Google;

/// <summary>
/// Configuration of Google federated login (02.3.1-T1.2), bound from <c>EVIDGATE:OIDC:GOOGLE</c>.
/// Both halves of the client credential are optional at boot: a host without them serves every other
/// route and answers the Google routes with 503 <c>unavailable</c>
/// (<see cref="GoogleLoginProvider.IsConfigured"/>).
/// </summary>
/// <remarks>
/// <see cref="ClientSecret"/> has an internal setter, so the configuration binder skips it; it is
/// resolved through <c>ISecretProvider</c> by <see cref="GoogleLoginOptionsSecretPostConfigure"/>
/// (10.1.1-T2). <see cref="GoogleLoginOptionsValidator"/> refuses at boot an endpoint that leaves the
/// fixed S4 allowlist of <see cref="GoogleEndpoints"/>, and validation names the key only, never a value.
/// </remarks>
internal sealed class GoogleLoginOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "EVIDGATE:OIDC:GOOGLE";

    /// <summary>OAuth client id of the platform's Google client; plain configuration, safe to log by name only.</summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// OAuth client secret, resolved through <c>ISecretProvider</c> (<c>SecretNames.OidcGoogleClientSecret</c>)
    /// and never bound from configuration, logged or put in an exception message.
    /// </summary>
    public string? ClientSecret { get; internal set; }

    /// <summary>Expected <c>iss</c> of the ID token; the bare <see cref="GoogleEndpoints.IssuerAlias"/> form is accepted beside it.</summary>
    [Required]
    public string Issuer { get; init; } = GoogleEndpoints.Issuer;

    /// <summary>Authorization endpoint the browser is sent to (never called by the server).</summary>
    [Required]
    public string AuthorizationEndpoint { get; init; } = "https://accounts.google.com/o/oauth2/v2/auth";

    /// <summary>Token endpoint of the authorization-code exchange; must stay on the outbound allowlist.</summary>
    [Required]
    public string TokenEndpoint { get; init; } = "https://oauth2.googleapis.com/token";

    /// <summary>JWKS document that publishes the ID-token signing keys; must stay on the outbound allowlist.</summary>
    [Required]
    public string JwksUri { get; init; } = "https://www.googleapis.com/oauth2/v3/certs";

    /// <summary>Tolerance applied to <c>exp</c> and <c>nbf</c>, in seconds (02.3.1 AC2 fixes 60; 0..300).</summary>
    [Range(0, 300)]
    public int ClockSkewSeconds { get; init; } = 60;

    /// <summary>How long a fetched JWKS document is trusted without a refresh attempt, in seconds.</summary>
    [Range(30, 86400)]
    public int JwksTtlSeconds { get; init; } = 3600;

    /// <summary>The soonest an unknown <c>kid</c> may force another JWKS fetch, in seconds.</summary>
    [Range(1, 3600)]
    public int JwksMinRefreshSeconds { get; init; } = 30;

    /// <summary>Hard timeout of one outbound provider call, in seconds.</summary>
    [Range(1, 30)]
    public int HttpTimeoutSeconds { get; init; } = 5;

    /// <summary>Largest provider response body the server reads, in bytes; a longer body is refused unread.</summary>
    [Range(1024, 1048576)]
    public int MaxResponseBytes { get; init; } = 65536;
}

/// <summary>
/// The fixed outbound endpoints of Google login (S4, 02.3.1 §8). The allowlist is a code constant, never
/// configuration: an options override that points outside it is refused at boot by
/// <see cref="GoogleLoginOptionsValidator"/> and again before every send by
/// <see cref="GoogleOutboundAllowlistHandler"/>.
/// </summary>
internal static class GoogleEndpoints
{
    /// <summary>The issuer Google publishes in ID tokens.</summary>
    internal const string Issuer = "https://accounts.google.com";

    /// <summary>The legacy bare-host form of the issuer, accepted beside <see cref="Issuer"/>.</summary>
    internal const string IssuerAlias = "accounts.google.com";

    /// <summary>Host of the browser-facing authorization endpoint (target of a redirect, never of a server call).</summary>
    internal const string AuthorizationHost = "accounts.google.com";

    /// <summary>Every <c>iss</c> the ID token may carry: the configured issuer plus the bare alias.</summary>
    internal static readonly string[] IssuerAliases = [Issuer, IssuerAlias];

    /// <summary>The only hosts the server itself may call over https: the token and JWKS endpoints.</summary>
    internal static readonly string[] OutboundHosts = ["oauth2.googleapis.com", "www.googleapis.com"];

    private static readonly string[] AuthorizationHosts = [AuthorizationHost];

    /// <summary>True when <paramref name="uri"/> is https, carries no userinfo, keeps the default port and targets an allowlisted host.</summary>
    /// <param name="uri">The endpoint to check.</param>
    internal static bool IsAllowlistedOutbound(Uri uri) => IsAllowed(uri, OutboundHosts);

    /// <summary>True when <paramref name="uri"/> is a well-formed https address of the authorization host.</summary>
    /// <param name="uri">The endpoint to check.</param>
    internal static bool IsAllowlistedAuthorization(Uri uri) => IsAllowed(uri, AuthorizationHosts);

    private static bool IsAllowed(Uri uri, string[] hosts)
        => uri is { IsAbsoluteUri: true }
           && uri.Scheme == Uri.UriSchemeHttps
           && string.IsNullOrEmpty(uri.UserInfo)
           && uri.IsDefaultPort
           && Array.Exists(hosts, host => string.Equals(host, uri.Host, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The S4 allowlist as a last line of defence in front of the primary handler: a request whose URI left
/// the allowlist is never sent, whatever built it. Redirects are refused by the primary handler itself
/// (<c>AllowAutoRedirect = false</c>), so a 3xx can never move a call to another host.
/// </summary>
internal sealed class GoogleOutboundAllowlistHandler : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri is not { } uri || !GoogleEndpoints.IsAllowlistedOutbound(uri))
        {
            // The message names no host and no value: a URI can carry a code, a token or a client id.
            throw new InvalidOperationException("the Google OIDC client may only call the allowlisted endpoints over https (S4)");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
