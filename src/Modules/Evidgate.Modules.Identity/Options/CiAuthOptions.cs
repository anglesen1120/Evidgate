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

    /// <summary>Base64 current pepper (at least 32 bytes) for ingest-token HMACs.</summary>
    public string? Pepper { get; internal set; }

    /// <summary>Version stored beside every HMAC made with <see cref="Pepper"/>.</summary>
    [Range(1, int.MaxValue)]
    public int PepperVersion { get; init; } = 1;

    /// <summary>Base64 previous pepper, kept while tokens made with it are still valid (rotation overlap).</summary>
    public string? PreviousPepper { get; internal set; }

    /// <summary>Version of <see cref="PreviousPepper"/>.</summary>
    [Range(1, int.MaxValue)]
    public int PreviousPepperVersion { get; init; } = 1;
}
