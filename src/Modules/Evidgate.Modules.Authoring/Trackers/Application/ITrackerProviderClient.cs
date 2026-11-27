namespace Evidgate.Modules.Authoring.Trackers.Application;

/// <summary>The tokens a provider issued for one authorization code. The access token never leaves memory except through the store.</summary>
/// <param name="AccessToken">The access token.</param>
/// <param name="RefreshToken">The refresh token, when the provider issued one.</param>
/// <param name="ExpiresAt">When the access token expires, when the provider said.</param>
/// <param name="GrantedScopes">The scopes the provider granted, exactly the ones requested.</param>
internal sealed record TrackerToken(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> GrantedScopes)
{
    /// <summary>The type name only: a log line or an exception that formats the record must never print a token.</summary>
    public override string ToString() => nameof(TrackerToken);
}

/// <summary>
/// The provider side of the authorization-code flow: the consent URL and the one code exchange. Every call goes to a
/// fixed provider host; nothing here ever writes to a tracker.
/// </summary>
internal interface ITrackerProviderClient
{
    /// <summary>True when the provider has a client id, a client secret and a redirect URI.</summary>
    /// <param name="provider">The provider.</param>
    bool IsConfigured(TrackerProvider provider);

    /// <summary>The configured redirect URI the provider sends the browser back to.</summary>
    /// <param name="provider">The provider.</param>
    string RedirectUri(TrackerProvider provider);

    /// <summary>The consent URL for <paramref name="state"/>; <paramref name="pkceVerifier"/> is used by providers that take a code challenge.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="state">The single-use state.</param>
    /// <param name="pkceVerifier">The PKCE verifier whose S256 challenge goes on the URL; ignored by Jira.</param>
    string Authorize(TrackerProvider provider, string state, string? pkceVerifier);

    /// <summary>Exchanges <paramref name="code"/> for tokens; null when the provider refuses it or answers anything unusable.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="code">The authorization code, used once.</param>
    /// <param name="pkceVerifier">The verifier of the consent that issued the code.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    Task<TrackerToken?> ExchangeAsync(TrackerProvider provider, string code, string? pkceVerifier, CancellationToken cancellationToken);
}
