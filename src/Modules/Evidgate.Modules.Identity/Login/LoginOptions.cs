using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Login;

/// <summary>
/// Configuration of the federated login protocol (02.3.1-T1.1), bound from <c>EVIDGATE:Login</c>: the
/// browser-facing base URL the provider is told to call back on, and the lifetime of the pending-login
/// cookie that carries <c>state</c>, the nonce and the PKCE verifier.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PublicBaseUrl"/> is optional at boot: a host without it serves every other route and
/// answers both login routes with 503 <c>unavailable</c> plus <c>Retry-After</c>, exactly like a provider
/// whose client credential is missing (<see cref="ILoginProvider.IsConfigured"/>). Absent covers null,
/// empty and whitespace alike, so an injected empty value cannot fail the host at boot. A present value
/// must be an absolute https URL; <see cref="LoginOptionsValidator"/> refuses it otherwise at boot and
/// names the key only, never the value (00 §11 NFR-01-2).
/// </para>
/// <para>
/// The provider half of the configuration (<c>EVIDGATE:OIDC:GOOGLE</c>, <c>EVIDGATE:OAUTH:GITHUB</c>) is
/// each provider's own options type; nothing here is a secret.
/// </para>
/// </remarks>
internal sealed class LoginOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "EVIDGATE:Login";

    /// <summary>
    /// Absolute https base URL of the platform as a browser reaches it. The redirect URI registered at
    /// every provider is <c>{PublicBaseUrl}/v1/auth/oidc/{provider}/callback</c>.
    /// </summary>
    public string? PublicBaseUrl { get; init; }

    /// <summary>
    /// Lifetime of the pending-login cookie and of the one-time <c>state</c> it carries, in seconds
    /// (02.3.1 §8: short-lived and one-time; 600 s default).
    /// </summary>
    [Range(60, 900)]
    public int StateLifetimeSeconds { get; init; } = 600;

    /// <summary>
    /// <c>Retry-After</c> of the 503 answered when the provider cannot be used, in seconds (AC8).
    /// </summary>
    [Range(1, 300)]
    public int RetryAfterSeconds { get; init; } = 30;

    /// <summary><see cref="StateLifetimeSeconds"/> as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan StateLifetime => TimeSpan.FromSeconds(StateLifetimeSeconds);

    /// <summary>
    /// The redirect URI registered at <paramref name="provider"/>, or <c>null</c> when
    /// <see cref="PublicBaseUrl"/> is absent or unusable — the route then answers 503 rather than send the
    /// browser to a provider with a redirect URI the provider does not know.
    /// </summary>
    /// <param name="provider">The provider's route value (<c>google</c>, later <c>github</c>).</param>
    public Uri? CallbackUri(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        return !string.IsNullOrWhiteSpace(PublicBaseUrl)
            && Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var baseUri)
            && baseUri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrEmpty(baseUri.Host)
                ? new Uri(baseUri, "/v1/auth/oidc/" + Uri.EscapeDataString(provider) + "/callback")
                : null;
    }
}

/// <summary>
/// Boot validation of the browser-facing base URL: absent is valid (the login routes fail closed), a
/// present value must be an absolute https URL with no path, query or fragment, because the redirect URI
/// is built from its origin. Null, empty and whitespace all mean absent, so an empty injected value
/// leaves the routes on 503 instead of failing the host at boot. Every message names the configuration
/// key only.
/// </summary>
internal sealed class LoginOptionsValidator : IValidateOptions<LoginOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, LoginOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.PublicBaseUrl))
        {
            return ValidateOptionsResult.Success;
        }

        const string key = LoginOptions.SectionName + ":" + nameof(LoginOptions.PublicBaseUrl);

        if (!Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host))
        {
            return ValidateOptionsResult.Fail($"{key} must be an absolute https URL, e.g. https://app.evidgate.example");
        }

        if (uri.AbsolutePath is not "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return ValidateOptionsResult.Fail($"{key} must be an origin only: no path, query or fragment");
        }

        return ValidateOptionsResult.Success;
    }
}
