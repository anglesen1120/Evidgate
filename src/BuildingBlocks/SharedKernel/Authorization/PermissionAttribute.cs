namespace Evidgate.BuildingBlocks.SharedKernel.Authorization;

/// <summary>
/// Declares the permission a Mediator handler requires (00 §5.5 C2, §5.7 S2:
/// authorization runs in the pipeline, not in endpoints). The pipeline behaviour
/// resolves this attribute on the handler type; <c>tests/Architecture.Tests</c>
/// fails the build for any handler class that does not carry it.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class PermissionAttribute : Attribute
{
    /// <summary>Permission shape enforced by 00 §2: <c>resource:action</c>, lower-snake both sides.</summary>
    private static readonly System.Text.RegularExpressions.Regex Shape =
        new("^[a-z_]+:[a-z_]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>True when <paramref name="permission"/> is a well-formed permission string.</summary>
    public static bool IsValid(string? permission) =>
        permission is not null && Shape.IsMatch(permission);

    public PermissionAttribute(string permission)
    {
        if (!IsValid(permission))
        {
            throw new ArgumentException(
                $"Invalid permission '{permission}'; expected lower-snake resource:action such as \"run:create\".",
                nameof(permission));
        }

        Permission = permission;
    }

    /// <summary>Permission string in <c>resource:action</c> form, e.g. <c>"run:create"</c>.</summary>
    public string Permission { get; }
}
