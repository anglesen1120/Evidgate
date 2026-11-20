using Evidgate.BuildingBlocks.Contracts.Validation;

namespace Evidgate.Modules.Orchestrator.Domain;

/// <summary>
/// Body rules shared by the manifest and environment-revision commands (05.1.1/05.1.2 review): the strings a body may carry,
/// and the error paths it may be described by.
/// </summary>
internal static class JsonBody
{
    /// <summary>Map members whose names are caller data: a path never names them (they may be secrets).</summary>
    private static readonly HashSet<string> MapMembers = new(StringComparer.Ordinal) { "params", "base_urls", "dependency_versions" };

    /// <summary>
    /// The path of a schema error that names no caller data: a member of <c>params</c>, <c>base_urls</c> or
    /// <c>dependency_versions</c> becomes <c>*</c>, the unknown member of an <c>additional_properties</c> error is cut, and
    /// the path is capped at 200 characters.
    /// </summary>
    internal static string SafePath(ContractValidationError error)
    {
        var segments = error.Path.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            if (MapMembers.Contains(segments[i - 1]))
            {
                segments[i] = "*";
            }
        }

        if (string.Equals(error.Reason, "additional_properties", StringComparison.Ordinal) && segments.Length > 1)
        {
            segments = segments[..^1];
        }

        var path = string.Join('/', segments);

        return path.Length > 200 ? path[..200] : path;
    }
}
