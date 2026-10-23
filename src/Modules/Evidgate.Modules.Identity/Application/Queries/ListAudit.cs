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

/// <summary>Filters shared by the audit list and the export.</summary>
/// <param name="ActorType">Exact <c>actor_type</c>.</param>
/// <param name="Action">Exact <c>action</c>.</param>
/// <param name="ObjectType">Exact <c>object_type</c>.</param>
/// <param name="From">Inclusive lower bound on <c>created_at</c>.</param>
/// <param name="To">Exclusive upper bound on <c>created_at</c>.</param>
internal sealed record AuditFilter(
    string? ActorType,
    string? Action,
    string? ObjectType,
    DateTimeOffset? From,
    DateTimeOffset? To);

/// <summary><c>GET /v1/orgs/{o}/audit</c> (02.7.1 AC4): one cursor page, newest first.</summary>
internal sealed record ListAuditQuery(Guid OrgId, string? Cursor, int Limit, AuditFilter Filter)
    : IQuery<Result<AuditListResponse>>;

/// <summary>
/// Handler of <see cref="ListAuditQuery"/>: a single statement per page (P3), served by
/// <c>audit_log_org_created_id_idx</c>. Another org's id is 404 and its rows are invisible to RLS.
/// </summary>
[Permission(Permissions.Audit.Read)]
internal sealed class ListAuditQueryHandler(ITenantDb db, ITenantScopeAccessor scopes)
    : IQueryHandler<ListAuditQuery, Result<AuditListResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AuditListResponse>> Handle(ListAuditQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(ListAuditQuery));

        var orgId = scopes.Require().OrgId;

        if (query.OrgId != orgId)
        {
            return Result.Failure<AuditListResponse>(Error.NotFound(OrgMismatch.Message));
        }

        DateTimeOffset? cursorCreatedAt = null;
        Guid? cursorId = null;

        if (query.Cursor is not null)
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var createdAt, out var id))
            {
                return Result.Failure<AuditListResponse>(Error.Invalid(
                    "the cursor is not a value this endpoint issued",
                    [new ErrorDetail("cursor", "malformed")]));
            }

            cursorCreatedAt = createdAt;
            cursorId = id;
        }

        var rows = await db.QueryAsync<AuditPageRow>(
            AuditReadSql.Page,
            new
            {
                org_id = orgId,
                actor_type = query.Filter.ActorType,
                action = query.Filter.Action,
                object_type = query.Filter.ObjectType,
                from = query.Filter.From,
                to = query.Filter.To,
                cursor_created_at = cursorCreatedAt,
                cursor_id = cursorId,
                limit = query.Limit + 1,
            },
            cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        var page = rows.Take(query.Limit).Select(row => row.ToDto()).ToList();

        return Result.Success(new AuditListResponse(
            page,
            hasMore ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null));
    }
}
