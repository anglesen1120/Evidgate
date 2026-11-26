using System.Text.RegularExpressions;

namespace Evidgate.Modules.Policy.Domain;

/// <summary>Stored states of a deploy target: it moves between them, and is never removed.</summary>
internal static class DeployTargetState
{
    public const string Active = "active";
    public const string Disabled = "disabled";

    /// <summary>True for a state a caller may ask for.</summary>
    public static bool IsKnown(string? state) => state is Active or Disabled;
}

/// <summary>
/// Shape rules of a deploy target. The API checks them before the write; the table repeats each one as a CHECK
/// (<c>deploy_target_name_check</c>, <c>deploy_target_position_check</c>), so a bug here cannot store a bad row.
/// </summary>
internal static partial class DeployTargetRules
{
    /// <summary>Most targets one project may hold; the list is one unpaged page, so the cap keeps it bounded.</summary>
    public const int MaxPerProject = 50;

    /// <summary>The largest <c>order</c> value (the column CHECK).</summary>
    public const int MaxOrder = 10000;

    /// <summary>A slug: letters, digits, dot, underscore, hyphen; starts with a letter or digit; 1-63 characters.</summary>
    public static bool IsValidName(string? name) => name is not null && NameShape().IsMatch(name);

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,62}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex NameShape();
}
