using System.ComponentModel.DataAnnotations;

namespace Evidgate.Modules.Evidence.Application.Retention;

/// <summary>
/// The bounded range a project's retention window may be set to, plus the platform defaults served to a project
/// that has no row. Bound from <c>EVIDGATE:Retention</c> and validated at boot like every other
/// <c>EVIDGATE__*</c> group, so the owner can tighten or loosen the floor and ceiling with a configuration change
/// rather than a migration.
/// <para>
/// The bounds are configuration, not contract: the database's <c>retention_policy_sane_check</c> only refuses a
/// non-positive artifact window or a metadata window shorter than it, so a deployment whose limits are too loose
/// is still storage-safe. The defaults are what a project without a row is served; they are never written by a
/// read.
/// </para>
/// </summary>
internal sealed class RetentionLimits
{
    /// <summary>Configuration section bound to <c>EVIDGATE__RETENTION__*</c>.</summary>
    public const string SectionName = "EVIDGATE:Retention";

    /// <summary>The shortest artifact window a project may set, in days.</summary>
    [Range(1, 3650, ErrorMessage = "EVIDGATE__RETENTION__MINARTIFACTDAYS must be between 1 and 3650")]
    public int MinArtifactDays { get; init; } = 1;

    /// <summary>The longest artifact window a project may set, in days.</summary>
    [Range(1, 3650, ErrorMessage = "EVIDGATE__RETENTION__MAXARTIFACTDAYS must be between 1 and 3650")]
    public int MaxArtifactDays { get; init; } = 90;

    /// <summary>The shortest metadata window a project may set, in days.</summary>
    [Range(1, 3650, ErrorMessage = "EVIDGATE__RETENTION__MINMETADATADAYS must be between 1 and 3650")]
    public int MinMetadataDays { get; init; } = 30;

    /// <summary>The longest metadata window a project may set, in days.</summary>
    [Range(1, 3650, ErrorMessage = "EVIDGATE__RETENTION__MAXMETADATADAYS must be between 1 and 3650")]
    public int MaxMetadataDays { get; init; } = 90;

    /// <summary>The artifact window served to a project with no policy row.</summary>
    [Range(1, 3650, ErrorMessage = "EVIDGATE__RETENTION__DEFAULTARTIFACTDAYS must be between 1 and 3650")]
    public int DefaultArtifactDays { get; init; } = 14;

    /// <summary>The metadata window served to a project with no policy row.</summary>
    [Range(1, 3650, ErrorMessage = "EVIDGATE__RETENTION__DEFAULTMETADATADAYS must be between 1 and 3650")]
    public int DefaultMetadataDays { get; init; } = 90;

    /// <summary>
    /// True when the configured range is coherent and both defaults sit inside it. The data annotations bound each
    /// value on its own; only a cross-field check can refuse a floor above its ceiling or a default the floor would
    /// then reject, which would make every default-served project's first write fail.
    /// </summary>
    internal bool IsCoherent =>
        MinArtifactDays <= MaxArtifactDays
        && MinMetadataDays <= MaxMetadataDays
        && DefaultArtifactDays >= MinArtifactDays
        && DefaultArtifactDays <= MaxArtifactDays
        && DefaultMetadataDays >= MinMetadataDays
        && DefaultMetadataDays <= MaxMetadataDays
        && DefaultMetadataDays >= DefaultArtifactDays;
}
