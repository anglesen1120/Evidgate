namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// One role a member holds, as the identity directory reports it (02.1.2-T1.3). A
/// slim projection of <see cref="RoleView"/>: the consumers (02.3.1's login, the
/// permission evaluator) need the code and the built-in flag, not the row's audit
/// columns.
/// </summary>
/// <param name="RoleId">The role's UUID (<c>role.id</c>).</param>
/// <param name="Code">The role code of 00 §2, e.g. <c>tenant_admin</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="IsBuiltin">True when the row is platform catalogue data (<c>role.org_id IS NULL</c>).</param>
public sealed record MemberRole(Guid RoleId, string Code, string Name, bool IsBuiltin);
