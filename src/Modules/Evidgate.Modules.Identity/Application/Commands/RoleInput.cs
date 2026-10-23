using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// The shape rules both custom-role commands' validators share (02.2.1 §5): the
/// input limits of 00 §5.7 S3, the role-code shape migration 0010's
/// <c>role_code_format_check</c> carries, and the permission shape 00 §2 defines.
/// Shape only — whether a code exists in the catalogue and is
/// <c>tenant_assignable</c> is a catalogue question the handlers ask (T79).
/// </summary>
internal static class RoleInput
{
    /// <summary>Input cap for a role code, matching the <c>role_code_format_check</c> shape's practical length.</summary>
    internal const int MaxCodeLength = 64;

    /// <summary>Input cap for a role name; the same bound the <c>role_name_length_check</c> carries.</summary>
    internal const int MaxNameLength = 64;

    /// <summary>Input cap for one role's permission set, so a body cannot carry an unbounded list.</summary>
    internal const int MaxPermissions = 200;

    /// <summary>The 02 §4.1 role-code shape, the SQL <c>role_code_format_check</c> spelled in C#.</summary>
    internal static readonly Regex CodeShape = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>True when <paramref name="code"/> is a well-formed 00 §2 <c>resource:action</c> string.</summary>
    internal static bool IsPermissionShaped(string? code) => PermissionAttribute.IsValid(code);
}
