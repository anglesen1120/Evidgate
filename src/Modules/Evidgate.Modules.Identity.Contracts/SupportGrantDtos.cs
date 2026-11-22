namespace Evidgate.Modules.Identity.Contracts;

/// <summary>One support grant as the tenant's list exposes it: the request, the derived state and the decision.</summary>
/// <param name="Id">Grant id.</param>
/// <param name="OrgId">The tenant the grant opens.</param>
/// <param name="OperatorUserId">The operator account the access belongs to.</param>
/// <param name="Scopes">The permission strings the grant covers.</param>
/// <param name="ProjectIds">The projects covered; empty means the whole org.</param>
/// <param name="Reason">Why the access was requested.</param>
/// <param name="State"><c>requested</c>, <c>approved</c>, <c>revoked</c> or <c>expired</c>, derived on read.</param>
/// <param name="ApprovedBy">The approving member, or null while requested.</param>
/// <param name="ApprovedAt">The approval instant, or null while requested.</param>
/// <param name="ExpiresAt">When the access stops.</param>
/// <param name="RevokedAt">The revocation instant, or null while live.</param>
/// <param name="CreatedAt">When the request was filed.</param>
public sealed record SupportGrant(
    Guid Id,
    Guid OrgId,
    Guid OperatorUserId,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<Guid> ProjectIds,
    string Reason,
    string State,
    Guid? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset CreatedAt);

/// <summary>Cursor page of an organization's support grants, newest first.</summary>
/// <param name="Items">The page.</param>
/// <param name="NextCursor">Opaque cursor or null.</param>
public sealed record SupportGrantList(IReadOnlyList<SupportGrant> Items, string? NextCursor);
