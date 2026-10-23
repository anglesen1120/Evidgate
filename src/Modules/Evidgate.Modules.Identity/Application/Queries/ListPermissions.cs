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
/// One page of the permission catalogue (<c>GET /v1/permissions</c>, 00 §9.3 via
/// 02 §4.2). Read-only by construction (00 §5.5 C1): the catalogue is immutable
/// data and the query writes nothing.
/// </summary>
/// <param name="Cursor">The previous page's last code, or null for the first page.</param>
/// <param name="Limit">Page size, already validated against 00 §9.1 (1..200) at the edge.</param>
internal sealed record ListPermissionsQuery(string? Cursor, int Limit) : IQuery<Result<PermissionListResponse>>;

/// <summary>
/// Handler of <see cref="ListPermissionsQuery"/>: one bounded statement over the
/// catalogue's primary key, inside the read-only tenant scope the C2 pipeline
/// opened. RLS decides the rows — the platform catalogue plus the caller's own org
/// rows — so the query cannot forget a tenant predicate (02 §4.5). The cursor is a
/// catalogue code, so a value outside the catalogue is one this service never
/// issued: 422 <c>request_invalid</c>, never a silently different page.
/// </summary>
[Permission(Permissions.Role.Read)]
internal sealed class ListPermissionsQueryHandler(ITenantDb db)
    : IQueryHandler<ListPermissionsQuery, Result<PermissionListResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<PermissionListResponse>> Handle(
        ListPermissionsQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(ListPermissionsQuery));

        if (query.Cursor is { } cursor && !Permissions.IsKnown(cursor))
        {
            return Result.Failure<PermissionListResponse>(Error.Invalid(
                "the cursor is not a value this endpoint issued",
                [new ErrorDetail("cursor", "malformed")]));
        }

        // limit + 1 rows: the extra row only answers "is there a next page?", it is
        // never returned (00 §9.1's next_cursor contract).
        var rows = await db.QueryAsync<PermissionView>(
            IdentityReadSql.PermissionsPage,
            new { cursor_code = query.Cursor, limit = query.Limit + 1 },
            cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        var items = hasMore ? [.. rows.Take(query.Limit)] : rows;

        return Result.Success(new PermissionListResponse(
            items,
            hasMore ? items[^1].Code : null));
    }
}
