using System.ComponentModel.DataAnnotations;

namespace Evidgate.Modules.Identity.Options;

/// <summary>
/// Configuration of CI authentication (02.4.1/02.4.2), bound from <c>EVIDGATE:Ci</c>. Every secret
/// is optional at boot: a host without them still serves everything else, and the CI endpoints fail
/// closed (503 <c>unavailable</c>) until the key material is configured.
/// </summary>
/// <remarks>
/// The three key-material properties (<see cref="SigningKey"/>, <see cref="Pepper"/>,
/// <see cref="PreviousPepper"/>) have internal setters, so the configuration binder skips
/// them; they are resolved through <c>ISecretProvider</c> by
/// <see cref="Ci.CiAuthOptionsSecretPostConfigure"/> (10.1.1-T2).
/// </remarks>
internal sealed class CiAuthOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "EVIDGATE:Ci";

    /// <summary>Base64 HS256 key (at least 32 bytes) that signs the minted <c>ci_producer</c> JWTs (D7).</summary>
    public string? SigningKey { get; internal set; }

    /// <summary><c>iss</c> of the minted token.</summary>
    [Required]
    public string Issuer { get; init; } = "evidgate";

    /// <summary><c>aud</c> of the minted token; every ingest endpoint expects it.</summary>
    [Required]
    public string Audience { get; init; } = "evidgate";

    /// <summary>Lifetime of a minted token in minutes (00 AC6: at most 60).</summary>
    [Range(1, 60)]
    public int TokenLifetimeMinutes { get; init; } = 60;

    /// <summary>Base64 current pepper (at least 32 bytes) for ingest-token and binding-challenge HMACs (T5, D-A4).</summary>
    public string? Pepper { get; internal set; }

    /// <summary>Version stored beside every HMAC made with <see cref="Pepper"/>.</summary>
    [Range(1, int.MaxValue)]
    public int PepperVersion { get; init; } = 1;

    /// <summary>Base64 previous pepper, kept while tokens made with it are still valid (rotation overlap).</summary>
    public string? PreviousPepper { get; internal set; }

    /// <summary>Version of <see cref="PreviousPepper"/>.</summary>
    [Range(1, int.MaxValue)]
    public int PreviousPepperVersion { get; init; } = 1;

    /// <summary>Expected <c>iss</c> of a GitHub Actions OIDC token.</summary>
    [Required]
    public string GithubOidcIssuer { get; init; } = "https://token.actions.githubusercontent.com";

    /// <summary>Expected <c>aud</c> of a GitHub Actions OIDC token (the workflow requests it).</summary>
    [Required]
    public string GithubOidcAudience { get; init; } = "evidgate";

    /// <summary>JWKS document of the GitHub issuer; must be https on the issuer's host (S4).</summary>
    [Required]
    public string GithubJwksUri { get; init; } = "https://token.actions.githubusercontent.com/.well-known/jwks";

    /// <summary>Clock skew tolerated on <c>exp</c>/<c>nbf</c>, in seconds (default 60).</summary>
    [Range(0, 300)]
    public int MaxSkewSeconds { get; init; } = 60;

    /// <summary>
    /// Comma-separated GitHub <c>event_name</c> values refused at the exchange. <c>pull_request_target</c> and <c>workflow_run</c>
    /// run in the base repository's context (a <c>workflow_run</c> of the allowed workflow on the default branch passes the ref and
    /// workflow allowlists) while they may be driven by untrusted fork code, so their tokens must not mint evidence credentials.
    /// An explicit value replaces this default: removing <c>workflow_run</c> from an override re-opens the risk. The claim itself
    /// is required; comparison is ordinal and case-exact.
    /// </summary>
    public string DeniedEventNames { get; init; } = "pull_request_target,workflow_run";

    /// <summary>Exchange attempts allowed per key within <see cref="ExchangeWindowSeconds"/> (AC8).</summary>
    [Range(1, 10000)]
    public int ExchangeBurstLimit { get; init; } = 30;

    /// <summary>Length of the rate-limit window in seconds.</summary>
    [Range(1, 3600)]
    public int ExchangeWindowSeconds { get; init; } = 60;
}
