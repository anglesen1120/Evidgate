using System.ComponentModel.DataAnnotations;

namespace Evidgate.Modules.Identity.Login.GitHub;

/// <summary>
/// Configuration of GitHub federated login (02.3.1-T1.3), bound from <c>EVIDGATE:OAUTH:GITHUB</c>.
/// Both halves of the client credential are optional at boot: a host without them serves every other
/// route and answers the GitHub routes with 503 <c>unavailable</c>
/// (<see cref="GitHubLoginProvider.IsConfigured"/>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ClientSecret"/> has an internal setter, so the configuration binder skips it; it is
/// resolved through <c>ISecretProvider</c> by <see cref="GitHubLoginOptionsSecretPostConfigure"/>
/// (10.1.1-T2). <see cref="GitHubLoginOptionsValidator"/> refuses at boot an endpoint that leaves the
/// fixed S4 allowlist of <see cref="GitHubEndpoints"/>, and validation names the key only, never a value.
/// </para>
/// <para>
/// A null, empty or whitespace value counts as ABSENT for every member: the deployment left the key out.
/// An absent credential or endpoint leaves the provider unconfigured — the routes then answer 503
/// <c>unavailable</c> with <c>Retry-After</c> — instead of failing the host at boot, which is why no
/// member is <c>[Required]</c>. A present value is still validated.
/// </para>
/// <para>
/// GitHub is OAuth2 only (ruling T6): there is no ID token, no issuer and no JWKS document, so the
/// section carries no such key. The subject comes from <c>GET /user</c> and the email from
/// <c>GET /user/emails</c>, both under <see cref="ApiBaseUrl"/>.
/// </para>
/// </remarks>
internal sealed class GitHubLoginOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "EVIDGATE:OAUTH:GITHUB";

    /// <summary>OAuth client id of the platform's GitHub OAuth app; plain configuration, safe to log by name only.</summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// OAuth client secret, resolved through <c>ISecretProvider</c>
    /// (<c>SecretNames.OauthGithubClientSecret</c>) and never bound from configuration, logged or put in
    /// an exception message.
    /// </summary>
    public string? ClientSecret { get; internal set; }

    /// <summary>Authorization endpoint the browser is sent to (never called by the server).</summary>
    public string AuthorizeUrl { get; init; } = "https://github.com/login/oauth/authorize";

    /// <summary>Token endpoint of the authorization-code exchange; must stay on the outbound allowlist.</summary>
    public string TokenUrl { get; init; } = "https://github.com/login/oauth/access_token";

    /// <summary>Base address of the REST API the provider reads the subject and the emails from; must stay on the outbound allowlist.</summary>
    public string ApiBaseUrl { get; init; } = "https://api.github.com";

    /// <summary>Hard timeout of one outbound provider call, in seconds.</summary>
    [Range(1, 30)]
    public int HttpTimeoutSeconds { get; init; } = 5;

    /// <summary>Largest provider response body the server reads, in bytes; a longer body is refused unread.</summary>
    [Range(1024, 1048576)]
    public int MaxResponseBytes { get; init; } = 65536;
}

/// <summary>
/// The fixed outbound endpoints of GitHub login (S4, 02.3.1 §8): <c>github.com</c> for the browser-facing
/// authorization redirect and the token exchange, <c>api.github.com</c> for the two profile reads. The
/// allowlist is a code constant, never configuration: an options override that points outside it is
/// refused at boot by <see cref="GitHubLoginOptionsValidator"/> and again before every send by
/// <see cref="GitHubOutboundAllowlistHandler"/>.
/// </summary>
internal static class GitHubEndpoints
{
    /// <summary>Host of the browser-facing authorization endpoint (target of a redirect, never of a server call).</summary>
    internal const string AuthorizationHost = "github.com";

    /// <summary>The only hosts the server itself may call over https: the token endpoint and the REST API.</summary>
    internal static readonly string[] OutboundHosts = [AuthorizationHost, "api.github.com"];

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
internal sealed class GitHubOutboundAllowlistHandler : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri is not { } uri || !GitHubEndpoints.IsAllowlistedOutbound(uri))
        {
            // The message names no host and no value: a URI can carry a code, a token or a client id.
            throw new InvalidOperationException("the GitHub OAuth client may only call the allowlisted endpoints over https (S4)");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
