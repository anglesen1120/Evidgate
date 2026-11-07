using System.ComponentModel.DataAnnotations;

namespace Evidgate.Modules.Identity.Options;

/// <summary>
/// Configuration of project API tokens, bound from <c>EVIDGATE:ApiToken</c>. The pepper itself is not
/// here: tokens hash through the CI key material, so a rotation there keeps every credential on one
/// pepper ring.
/// </summary>
internal sealed class ApiTokenOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "EVIDGATE:ApiToken";

    /// <summary>The longest lifetime a created token may ask for, in days.</summary>
    [Range(1, 3650)]
    public int MaxLifetimeDays { get; init; } = 365;
}
