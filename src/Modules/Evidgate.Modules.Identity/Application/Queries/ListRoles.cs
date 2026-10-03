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
/// One page of roles (<c>GET /v1/roles</c>, 02 §4.2): the built-in catalogue plus
/// the caller's own org rows, keyset-ordered on <c>(created_at, id)</c> (00 §9.1).
/// </summary>
/// <param name="Cursor">The opaque cursor of the previous page, or null for the first page.</param>
/// <param name="Limit">Page size, already validated against 00 §9.1 (1..200) at the edge.</param>
internal sealed record ListRolesQuery(string? Cursor, int Limit) : IQuery<Result<RoleListResponse>>;

/// <summary>
/// Handler of <see cref="ListRolesQuery"/>. A cursor this service did not issue is
/// a client error (422 <c>request_invalid</c>), never a silently different page:
/// the cursor is decoded into the keyset pair before it reaches the statement.
/// </summary>
[Permission(Permissions.Role.Read)]
internal sealed class ListRolesQueryHandler(ITenantDb db)
    : IQueryHandler<ListRolesQuery, Result<RoleListResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<RoleListResponse>> Handle(
        ListRolesQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(ListRolesQuery));

        DateTimeOffset? cursorCreatedAt = null;
        Guid? cursorId = null;

        if (query.Cursor is { } cursor)
        {
            if (!KeysetCursor.TryDecode(cursor, out var decodedCreatedAt, out var decodedId))
            {
                return Result.Failure<RoleListResponse>(Error.Invalid(
                    "the cursor is not a value this endpoint issued",
                    [new ErrorDetail("cursor", "malformed")]));
            }

            cursorCreatedAt = decodedCreatedAt;
            cursorId = decodedId;
        }

        var rows = await db.QueryAsync<RoleView>(
            IdentityReadSql.RolesPage,
            new { cursor_created_at = cursorCreatedAt, cursor_id = cursorId, limit = query.Limit + 1 },
            cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        var items = hasMore ? [.. rows.Take(query.Limit)] : rows;

        return Result.Success(new RoleListResponse(
            items,
            hasMore && items.Count > 0 ? KeysetCursor.Encode(items[^1].CreatedAt, items[^1].Id) : null));
    }
}
