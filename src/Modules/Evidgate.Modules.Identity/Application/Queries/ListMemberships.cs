using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Identity.Application.Queries;

/// <summary>
/// One page of an org's memberships (<c>GET /v1/orgs/{o}/memberships</c>, 02.1.2
/// §5), keyset-ordered on <c>(created_at, id)</c> per 00 §9.1 and covered by
/// <c>membership_org_created_id_idx</c>. The read runs in the C2 read-only tenant
/// transaction (C1/T95) under RLS, so another org's rows are not merely filtered out
/// of the page — they are not reachable from the statement.
/// </summary>
/// <param name="OrgId">The org from the route; it must equal the request tenant (otherwise 404).</param>
/// <param name="Cursor">The opaque cursor of the previous page, or null for the first page.</param>
/// <param name="Limit">Page size, already validated against 00 §9.1 (1..200) at the edge.</param>
internal sealed record ListMembershipsQuery(Guid OrgId, string? Cursor, int Limit)
    : IQuery<Result<MembershipListResponse>>;

/// <summary>
/// Handler of <see cref="ListMembershipsQuery"/>. A cursor this service did not
/// issue is a client error (422 <c>request_invalid</c>), never a silently different
/// page: the cursor is decoded into the keyset pair before it reaches the statement.
/// </summary>
[Permission(Permissions.Membership.Read)]
internal sealed class ListMembershipsQueryHandler(ITenantDb db, ITenantScopeAccessor scopes)
    : IQueryHandler<ListMembershipsQuery, Result<MembershipListResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<MembershipListResponse>> Handle(
        ListMembershipsQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(ListMembershipsQuery));

        var orgId = scopes.Require().OrgId;

        if (query.OrgId != orgId)
        {
            return Result.Failure<MembershipListResponse>(Error.NotFound(OrgMismatch.Message));
        }

        DateTimeOffset? cursorCreatedAt = null;
        Guid? cursorId = null;

        if (query.Cursor is { } cursor)
        {
            if (!ReadCursor.TryDecode(cursor, out var decodedCreatedAt, out var decodedId))
            {
                return Result.Failure<MembershipListResponse>(Error.Invalid(
                    "the cursor is not a value this endpoint issued",
                    [new ErrorDetail("cursor", "malformed")]));
            }

            cursorCreatedAt = decodedCreatedAt;
            cursorId = decodedId;
        }

        var rows = await db.QueryAsync<MembershipRow>(
            MembershipReadSql.MembershipsPage,
            new
            {
                org_id = orgId,
                cursor_created_at = cursorCreatedAt,
                cursor_id = cursorId,
                limit = query.Limit + 1,
            },
            cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        IReadOnlyList<MembershipRow> page = hasMore ? [.. rows.Take(query.Limit)] : rows;

        var items = await MembershipReader.ToViewsAsync(db, orgId, page, cancellationToken).ConfigureAwait(false);

        return Result.Success(new MembershipListResponse(
            items,
            hasMore && items.Count > 0 ? ReadCursor.Encode(items[^1].CreatedAt, items[^1].Id) : null));
    }
}
