using System.ComponentModel.DataAnnotations;

namespace Evidgate.BuildingBlocks.SharedKernel.GitHub;

/// <summary>
/// Configuration of the platform's single GitHub App client, bound from <c>EVIDGATE:GITHUB</c>. The App id and the
/// client id are plain configuration; <see cref="PrivateKey"/> and <see cref="ClientSecret"/> have internal setters, so
/// the binder never reads them from configuration — they are resolved through <c>ISecretProvider</c> and are never
/// logged or put in an exception message.
/// </summary>
/// <remarks>
/// An absent App id leaves the client unconfigured: every call returns <c>github_not_configured</c> and no outbound
/// request is sent, so a host that never installs the App starts without one. A present but unusable value is refused
/// at boot by <see cref="GitHubOptionsValidator"/>, whose messages name the configuration key only.
/// </remarks>
public sealed class GitHubOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "EVIDGATE:GITHUB";

    /// <summary>Numeric id of the App, as GitHub shows it; it is the JWT issuer. Plain configuration.</summary>
    public string? AppId { get; init; }

    /// <summary>OAuth client id of the App; plain configuration, used by the install callback's code exchange.</summary>
    public string? ClientId { get; init; }

    /// <summary>App private key in PEM form, resolved through the secret provider into startup-bound options.</summary>
    public string? PrivateKey { get; internal set; }

    /// <summary>App client secret, resolved through the secret provider; used only for the OAuth code exchange.</summary>
    public string? ClientSecret { get; internal set; }

    /// <summary>Public slug of the App, used to build the install URL. Plain configuration.</summary>
    public string? AppSlug { get; init; }

    /// <summary>Base address of the REST API; the validator pins it to <c>https://api.github.com</c>.</summary>
    public string ApiBaseUrl { get; init; } = "https://api.github.com";

    /// <summary>Base address of the web/OAuth host; the validator pins it to <c>https://github.com</c>.</summary>
    public string OAuthBaseUrl { get; init; } = "https://github.com";

    /// <summary>Timeout of one outbound request, in seconds; the resilience pipeline retries inside a larger budget.</summary>
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

    /// <summary>Largest accepted tree entry count; <see cref="MaxResponseBytes"/> usually refuses a huge tree first.</summary>
    [Range(1, 1_000_000)]
    public int MaxTreeEntries { get; init; } = 100_000;

    /// <summary>Largest number of installation tokens kept in memory; the cache never grows past it.</summary>
    [Range(1, 1024)]
    public int MaxTokenCacheEntries { get; init; } = 128;

    /// <summary>Largest number of pages the ownership check reads from a paginated <c>GET /user/installations</c>.</summary>
    [Range(1, 50)]
    public int MaxInstallationPages { get; init; } = 10;
}
