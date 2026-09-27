namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// Body of <c>POST /v1/roles</c> (02.2.1 §5): create a custom role in the caller's
/// org with the permissions it starts from. The route exists in V1 but answers 403
/// until <c>EVIDGATE__IDENTITY__CUSTOMROLESENABLED</c> is on (RECOMMENDATIONS
/// 02-Q3); a built-in role can never be created or changed through it (AC6).
/// </summary>
/// <param name="Code">Role code, <c>^[a-z][a-z0-9_]*$</c>; unique per org → 409 <c>role_code_taken</c>.</param>
/// <param name="Name">Display name, 1..64 chars.</param>
/// <param name="Permissions">
/// The catalogue codes to grant. Every one must be a catalogue code (else 422
/// <c>unknown_permission</c>) and <c>tenant_assignable</c> (else 422
/// <c>permission_not_assignable</c>, T79).
/// </param>
public sealed record CreateRoleRequest(string Code, string Name, IReadOnlyList<string> Permissions);

/// <summary>
/// Body of <c>PATCH /v1/roles/{r}</c> (02.2.1 §5): rename a custom role and/or add
/// grants to it. Both fields are optional; neither may be used on a built-in role
/// (409 <c>builtin_role_immutable</c>).
/// </summary>
/// <param name="Name">The new display name, or null to leave it unchanged.</param>
/// <param name="AddPermissions">
/// Catalogue codes to grant in addition to the role's current set, or null to leave
/// the set unchanged. Additive on purpose: migration 0010 deliberately revokes
/// <c>DELETE</c> on <c>role_permission</c> for the app role (a role is archived, not
/// rewritten — 02 §6 keeps custom-role archival past V1), so V1 cannot express the
/// removal of a grant and must not pretend to (recorded in the feature plan §3).
/// Validation is the same as at creation: 422 <c>unknown_permission</c> /
/// <c>permission_not_assignable</c>.
/// </param>
public sealed record UpdateRoleRequest(string? Name, IReadOnlyList<string>? AddPermissions);
