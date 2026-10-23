using System.Diagnostics.CodeAnalysis;

namespace Evidgate.Modules.Identity.Options;

/// <summary>
/// The Identity module's runtime flags, bound from <c>EVIDGATE:Identity</c>
/// (10.1.1's <c>EVIDGATE__IDENTITY__*</c> matrix). Internal like the rest of the
/// module implementation (00 §5.5 — only <c>.Contracts</c>/<c>.Endpoints</c> may be
/// public); the composition roots bind it through <c>AddIdentityModule</c>.
/// </summary>
internal sealed class IdentityOptions
{
    /// <summary>Configuration section bound to <c>EVIDGATE__IDENTITY__*</c>.</summary>
    public const string SectionName = "EVIDGATE:Identity";

    /// <summary>
    /// Whether the custom-role write commands (<c>POST</c>/<c>PATCH /v1/roles</c>,
    /// 02.2.1 §5) may create or change a role that is not a built-in one. Off in V1
    /// (RECOMMENDATIONS 02-Q3): the routes ship and answer 403 <c>forbidden</c>, so
    /// the V2 editor is a configuration change rather than a release. A built-in
    /// role is immutable whatever this flag says (AC6).
    /// </summary>
    public bool CustomRolesEnabled { get; init; }
}
