namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// Body of <c>POST /v1/orgs/{o}/memberships/{m}/roles</c> (02.1.2 §5): grant one role
/// to one membership. By id, not by code: <c>role.id</c> is what <c>GET /v1/roles</c>
/// returns and what <c>DELETE …/roles/{r}</c> addresses, so one identifier form is
/// used on both sides.
/// </summary>
/// <param name="RoleId">
/// The role to grant: a built-in catalogue role or the caller's own org row. A role
/// the tenant cannot see is 409 <c>unknown_role</c> (02.1.2 §5).
/// </param>
public sealed record GrantRoleRequest(Guid RoleId);
