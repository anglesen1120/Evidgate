namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// A membership with its roles, as <see cref="IMembership"/> reports it
/// (02.1.2-T1.3): the row's identity and status plus the role ids it holds, which is
/// what a caller needs to decide access without a second round trip.
/// </summary>
/// <param name="MembershipId">The membership's UUID.</param>
/// <param name="OrgId">The owning org.</param>
/// <param name="UserId">The bound user, or null while the invite is <c>pending</c>.</param>
/// <param name="Status">One of <see cref="MembershipStatus"/>.</param>
/// <param name="RoleIds">The role ids the membership holds.</param>
public sealed record MembershipSummary(
    Guid MembershipId,
    Guid OrgId,
    Guid? UserId,
    string Status,
    IReadOnlyList<Guid> RoleIds);
