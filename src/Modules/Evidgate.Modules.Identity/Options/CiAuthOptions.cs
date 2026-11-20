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
    /// Comma-separated GitHub <c>event_name</c> values accepted at the exchange; every other event is refused, so the check fails
    /// closed. <c>pull_request_target</c> and <c>workflow_run</c> run in the base repository's context while they may be driven by
    /// untrusted fork code, so they are never listed and the boot validator refuses them. The claim is required and must be a JSON
    /// string; comparison is ordinal and case-exact, as GitHub sends lowercase names.
    /// </summary>
    public string AllowedEventNames { get; init; } = "push,workflow_dispatch,schedule,release,merge_group";

    /// <summary>
    /// Bound only so the boot validator can refuse a leftover <c>EVIDGATE__CI__DENIEDEVENTNAMES</c>: the deny list was replaced by
    /// <see cref="AllowedEventNames"/>, and silently ignoring the old variable would leave an operator believing it still applies.
    /// </summary>
    public string? DeniedEventNames { get; init; }
}
