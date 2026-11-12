using System.ComponentModel.DataAnnotations;

namespace Evidgate.Modules.Catalog.Infrastructure.GitHub;

/// <summary>
/// Configuration of the platform's GitHub App client, bound from <c>EVIDGATE:GITHUB</c>. The App
/// id is plain configuration; <see cref="PrivateKey"/> has an internal setter, so the binder never
/// reads it from configuration — it is resolved through <c>ISecretProvider</c> by
/// <see cref="GitHubOptionsSecretPostConfigure"/> and never logged or put in an exception message.
/// </summary>
/// <remarks>
/// An absent App id leaves the client unconfigured: every call returns
/// <c>github_not_configured</c> and no outbound request is sent, so a host that never binds a
/// repository starts without a GitHub App. A present but unusable value is refused at boot by
/// <see cref="GitHubOptionsValidator"/>, whose messages name the key only.
/// </remarks>
internal sealed class GitHubOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "EVIDGATE:GITHUB";

    /// <summary>Numeric id of the App, as GitHub shows it; it is the JWT issuer. Plain configuration.</summary>
    public string? AppId { get; init; }

    /// <summary>
    /// App private key in PEM form, resolved through <c>ISecretProvider</c>
    /// (<c>SecretNames.GitHubAppPrivateKey</c>) and never bound from configuration.
    /// </summary>
    public string? PrivateKey { get; internal set; }

    /// <summary>Base address of the REST API; the validator pins it to <c>https://api.github.com</c> (S4).</summary>
    public string ApiBaseUrl { get; init; } = "https://api.github.com";

    /// <summary>Timeout of one outbound request, in seconds; the resilience pipeline retries inside a larger total budget.</summary>
    [Range(1, 60)]
    public int HttpTimeoutSeconds { get; init; } = 10;

    /// <summary>How long before an installation token expires the cache stops serving it, in seconds.</summary>
    [Range(0, 1800)]
    public int InstallationTokenSafetyMarginSeconds { get; init; } = 300;

    /// <summary>App JWTs are backdated by this much so a small clock difference at GitHub does not refuse them.</summary>
    [Range(0, 120)]
    public int AppJwtBackdateSeconds { get; init; } = 60;

    /// <summary>Largest response body the client reads, in bytes; a longer body is refused unread.</summary>
    [Range(1024, 8_388_608)]
    public int MaxResponseBytes { get; init; } = 2_097_152;

    /// <summary>Largest decoded blob the client accepts, in bytes; git caps a blob the same way.</summary>
    [Range(1024, 4_194_304)]
    public int MaxBlobBytes { get; init; } = 1_048_576;

    /// <summary>Largest tree the client accepts; GitHub itself refuses a recursive tree past 100 000 entries.</summary>
    [Range(1, 1_000_000)]
    public int MaxTreeEntries { get; init; } = 100_000;

    /// <summary>Largest number of installation tokens kept in memory; the cache never grows past it.</summary>
    [Range(1, 1024)]
    public int MaxTokenCacheEntries { get; init; } = 128;
}

/// <summary>
/// The fixed outbound endpoint of the App client (S4): <c>api.github.com</c> and nothing else. The
/// allowlist is a code constant, never configuration, so an overridden base address is refused at
/// boot instead of being called.
/// </summary>
internal static class GitHubEndpoints
{
    /// <summary>The only host the App client may call.</summary>
    internal const string ApiHost = "api.github.com";

    /// <summary>GitHub's documented REST API version, sent on every call.</summary>
    internal const string ApiVersion = "2022-11-28";

    /// <summary>Media type that selects the versioned REST representation.</summary>
    internal const string ApiMediaType = "application/vnd.github+json";

    /// <summary>User agent GitHub asks every client to send.</summary>
    internal const string UserAgent = "Evidgate";

    /// <summary>
    /// True when <paramref name="uri"/> is an https address of the GitHub API host, with no userinfo,
    /// no path, no query and the default port — the only shape a request base may have.
    /// </summary>
    /// <param name="uri">The base address to check.</param>
    internal static bool IsAllowedApiBase(Uri uri) =>
        uri is { IsAbsoluteUri: true }
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.IsDefaultPort
        && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && string.Equals(ApiHost, uri.Host, StringComparison.OrdinalIgnoreCase);
}
