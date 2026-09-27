namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// One membership as the <c>/v1/orgs/{o}/memberships…</c> routes expose it
/// (02.1.2 §4/§5): the org-scoped row that makes a user a member, with the roles
/// that membership currently holds. <see cref="InvitedEmail"/> is personal data
/// (02.1.2 §8 "Privacy/PDPL"); it is returned only to a caller holding
/// <c>membership:read</c> (in V1 that is <c>tenant_admin</c>), inside the tenant
/// scope, and never outside the org.
/// </summary>
/// <param name="Id">The membership's UUID (application-generated, 00 §7).</param>
/// <param name="OrgId">The owning org; always the tenant scope the read ran in.</param>
/// <param name="UserId">Null while the invite is <c>pending</c>; set when a login binds it (02.3.1-T2).</param>
/// <param name="InvitedEmail">The normalised invite address (lowercased, trimmed).</param>
/// <param name="Status">One of <see cref="MembershipStatus"/>.</param>
/// <param name="Roles">The membership's roles, in <c>(created_at, id)</c> order; built-in catalogue rows plus the org's own.</param>
/// <param name="CreatedAt">Creation instant — the keyset's leading column (00 §9.1).</param>
/// <param name="UpdatedAt">Last write instant (00 §7).</param>
/// <param name="Version">Optimistic-concurrency token, the <c>ETag</c> of this row (00 §5.5 C4, §9.1).</param>
public sealed record MembershipView(
    Guid Id,
    Guid OrgId,
    Guid? UserId,
    string InvitedEmail,
    string Status,
    IReadOnlyList<RoleView> Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version);
