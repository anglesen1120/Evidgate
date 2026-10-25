namespace Evidgate.Modules.Identity.Login;

/// <summary>
/// One federated login provider (02.3.1-T1): Google OIDC here, GitHub OAuth2 in 02.3.1-T1.3. The
/// start/callback routes (<see cref="Evidgate.Modules.Identity.Endpoints.LoginEndpoints"/>) own the cookie, state, PKCE and redirect
/// mechanics; a provider owns only what is provider-specific: the authorization URL, the code
/// exchange and the verification of what the provider returns. Every outbound call a provider makes
/// happens in <see cref="CompleteAsync"/>, which the callback invokes BEFORE any database
/// transaction is opened (P5).
/// </summary>
internal interface ILoginProvider
{
    /// <summary>Route value of the provider (<c>google</c>, later <c>github</c>); lower case.</summary>
    string Name { get; }

    /// <summary>Whether the provider is configured (client id and secret present). An unconfigured provider is answered with 503 <c>unavailable</c>.</summary>
    bool IsConfigured { get; }

    /// <summary>Builds the provider's authorization URL for the start redirect. No network call.</summary>
    /// <param name="request">State, nonce, PKCE challenge and the redirect URI registered at the provider.</param>
    Uri BuildAuthorizationUrl(LoginAuthorizationRequest request);

    /// <summary>
    /// Exchanges the authorization code (with the PKCE verifier), verifies what the provider returns and yields
    /// the identity or a typed failure. Never throws for a provider failure; honours <paramref name="cancellationToken"/>.
    /// </summary>
    /// <param name="request">Code, PKCE verifier, the nonce the start route stored and the redirect URI.</param>
    /// <param name="cancellationToken">Request abort.</param>
    Task<LoginProviderResult> CompleteAsync(LoginCallbackRequest request, CancellationToken cancellationToken);
}

/// <summary>Inputs of <see cref="ILoginProvider.BuildAuthorizationUrl"/>.</summary>
/// <param name="State">Opaque anti-CSRF value, one use.</param>
/// <param name="Nonce">Value the ID token must echo (OIDC providers).</param>
/// <param name="CodeChallenge">PKCE S256 challenge of the verifier kept in the state cookie.</param>
/// <param name="RedirectUri">Absolute callback URI of the platform.</param>
internal sealed record LoginAuthorizationRequest(string State, string Nonce, string CodeChallenge, Uri RedirectUri);

/// <summary>Inputs of <see cref="ILoginProvider.CompleteAsync"/>.</summary>
/// <param name="Code">Authorization code from the provider redirect.</param>
/// <param name="CodeVerifier">PKCE verifier from the state cookie.</param>
/// <param name="Nonce">Nonce from the state cookie, which the ID token must carry.</param>
/// <param name="RedirectUri">The same redirect URI used at start.</param>
internal sealed record LoginCallbackRequest(string Code, string CodeVerifier, string Nonce, Uri RedirectUri);

/// <summary>A provider-verified identity. <c>(Provider, Subject)</c> is the only identity key (D3).</summary>
/// <param name="Provider">Provider name, lower case.</param>
/// <param name="Subject">Provider subject (Google <c>sub</c>).</param>
/// <param name="Email">Email at the provider; only ever bound when <paramref name="EmailVerified"/> is true.</param>
/// <param name="EmailVerified">Provider-asserted verification; a handler returns an identity only when it is true.</param>
/// <param name="DisplayName">Optional display name.</param>
/// <param name="HostedDomain">
/// The hosted domain the provider asserted for this identity, ASCII lower-cased and trimmed: Google's
/// <c>hd</c> claim of the ID token, taken AFTER the token's signature, issuer, audience, lifetime and nonce
/// were verified, and never from userinfo or a query value. Null when the provider asserted none, when the
/// claim is not a string, or when it is longer than 253 characters (a DNS name's maximum). GitHub never
/// asserts one, so it is always null for that provider (02.3.1-T2, Q3).
/// </param>
internal sealed record VerifiedIdentity(
    string Provider,
    string Subject,
    string Email,
    bool EmailVerified,
    string? DisplayName,
    string? HostedDomain = null);

/// <summary>Why a provider completion failed. The callback maps these; none carries provider text.</summary>
internal enum LoginFailure
{
    /// <summary>Bad signature, issuer, audience, expiry, nbf, nonce, unknown kid, unverified email, rejected code: 401 <c>token_invalid</c>.</summary>
    TokenInvalid,

    /// <summary>Timeout, transport error, 5xx or unusable JWKS/discovery: 503 <c>unavailable</c> with <c>Retry-After</c>.</summary>
    ProviderUnavailable,
}

/// <summary>Outcome of <see cref="ILoginProvider.CompleteAsync"/>: exactly one of the two members is set.</summary>
/// <param name="Identity">The verified identity on success.</param>
/// <param name="Failure">The failure otherwise.</param>
internal readonly record struct LoginProviderResult(VerifiedIdentity? Identity, LoginFailure? Failure)
{
    /// <summary>A success.</summary>
    public static LoginProviderResult Ok(VerifiedIdentity identity) => new(identity, null);

    /// <summary>A failure.</summary>
    public static LoginProviderResult Fail(LoginFailure failure) => new(null, failure);
}
