namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// One role as <c>GET /v1/roles</c> exposes it (00 §2, 02 §5): the built-in roles
/// of the catalogue plus, once the flagged custom-role commands ship, the caller's
/// own org rows. Another org's custom role is never visible — the read runs under
/// the catalogue RLS policy (02 §4.5).
/// </summary>
/// <param name="Id">The role's UUID (application-generated, 00 §7).</param>
/// <param name="OrgId">Null for a built-in role, the owning org for a custom role.</param>
/// <param name="Code">The role code of 00 §2 (e.g. <c>tenant_admin</c>).</param>
/// <param name="Name">Display name.</param>
/// <param name="IsBuiltin">True for a catalogue role: immutable (AC6).</param>
/// <param name="CreatedAt">Creation instant — the keyset's leading column (00 §9.1).</param>
/// <param name="Version">Optimistic-concurrency token (00 §5.5 C4, §7).</param>
public sealed record RoleView(
    Guid Id,
    Guid? OrgId,
    string Code,
    string Name,
    bool IsBuiltin,
    DateTimeOffset CreatedAt,
    int Version);
