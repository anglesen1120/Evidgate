namespace Evidgate.BuildingBlocks.SharedKernel.GitHub;

/// <summary>
/// The fixed outbound endpoints of the App client: <c>api.github.com</c> for the REST API and <c>github.com</c> for
/// the OAuth code exchange. Each allowlist is a code constant, never configuration, so an overridden base address is
/// refused at boot instead of being called.
/// </summary>
public static class GitHubEndpoints
{
    /// <summary>The only REST API host the client may call.</summary>
    public const string ApiHost = "api.github.com";

    /// <summary>The only web host the client may call (OAuth code exchange, install URL).</summary>
    public const string OAuthHost = "github.com";

    /// <summary>GitHub's documented REST API version, sent on every call.</summary>
    public const string ApiVersion = "2022-11-28";

    /// <summary>Media type that selects the versioned REST representation.</summary>
    public const string ApiMediaType = "application/vnd.github+json";

    /// <summary>Media type GitHub requires for the form-encoded OAuth exchange.</summary>
    public const string OAuthMediaType = "application/json";

    /// <summary>User agent GitHub asks every client to send.</summary>
    public const string UserAgent = "Evidgate";

    /// <summary>True when <paramref name="uri"/> is the bare https address of the REST API host.</summary>
    /// <param name="uri">The base address to check.</param>
    public static bool IsAllowedApiBase(Uri uri) =>
        IsBareHost(uri, ApiHost);

    /// <summary>True when <paramref name="uri"/> is the bare https address of the web host.</summary>
    /// <param name="uri">The base address to check.</param>
    public static bool IsAllowedOAuthBase(Uri uri) =>
        IsBareHost(uri, OAuthHost);

    private static bool IsBareHost(Uri uri, string host) =>
        uri is { IsAbsoluteUri: true }
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.IsDefaultPort
        && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && string.Equals(host, uri.Host, StringComparison.OrdinalIgnoreCase);
}
