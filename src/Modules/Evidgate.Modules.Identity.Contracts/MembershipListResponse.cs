namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// The 00 §9.1 cursor page of <c>GET /v1/orgs/{o}/memberships</c>: keyset-ordered on
/// <c>(created_at, id)</c> and covered by <c>membership_org_status_created_id_idx</c>
/// (02.1.2 §8 P3).
/// </summary>
/// <param name="Items">The page's rows, in <c>(created_at, id)</c> order.</param>
/// <param name="NextCursor">The opaque cursor of the next page, or null on the last page.</param>
public sealed record MembershipListResponse(IReadOnlyList<MembershipView> Items, string? NextCursor);
