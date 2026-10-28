using System.Text.Json;
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

/// <summary>Filters of the security-events list (11.5.1 §5).</summary>
/// <param name="ProjectId">Exact <c>project_id</c>; null means any project.</param>
/// <param name="Type">Exact <c>type</c>, one of the closed V1 set.</param>
/// <param name="Severity">Exact <c>severity</c>: info | warning | critical.</param>
/// <param name="From">Inclusive lower bound on <c>created_at</c>.</param>
/// <param name="To">Exclusive upper bound on <c>created_at</c>.</param>
internal sealed record SecurityEventFilter(
    Guid? ProjectId,
    string? Type,
    string? Severity,
    DateTimeOffset? From,
    DateTimeOffset? To);

/// <summary><c>GET /v1/orgs/{o}/security-events</c> (11.5.1 AC3): one cursor page, newest first.</summary>
internal sealed record ListSecurityEventsQuery(Guid OrgId, string? Cursor, int Limit, SecurityEventFilter Filter)
    : IQuery<Result<SecurityEventList>>;

/// <summary>
/// Handler of <see cref="ListSecurityEventsQuery"/>: a single statement per page (P3), served by
/// <c>security_event_list_idx</c>. Another org's id is 404 and its rows are invisible to RLS.
/// </summary>
[Permission(Permissions.SecurityEvent.Read)]
internal sealed class ListSecurityEventsQueryHandler(ITenantDb db, ITenantScopeAccessor scopes)
    : IQueryHandler<ListSecurityEventsQuery, Result<SecurityEventList>>
{
    /// <inheritdoc />
    public async ValueTask<Result<SecurityEventList>> Handle(
        ListSecurityEventsQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(ListSecurityEventsQuery));

        var orgId = scopes.Require().OrgId;

        if (query.OrgId != orgId)
        {
            return Result.Failure<SecurityEventList>(Error.NotFound(OrgMismatch.Message));
        }

        DateTimeOffset? cursorCreatedAt = null;
        Guid? cursorId = null;

        if (query.Cursor is not null)
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var createdAt, out var id))
            {
                return Result.Failure<SecurityEventList>(Error.Invalid(
                    "the cursor is not a value this endpoint issued",
                    [new ErrorDetail("cursor", "malformed")]));
            }

            cursorCreatedAt = createdAt;
            cursorId = id;
        }

        var rows = await db.QueryAsync<SecurityEventPageRow>(
            SecurityEventReadSql.Page,
            new
            {
                org_id = orgId,
                project_id = query.Filter.ProjectId,
                type = query.Filter.Type,
                severity = query.Filter.Severity,
                from = query.Filter.From,
                to = query.Filter.To,
                cursor_created_at = cursorCreatedAt,
                cursor_id = cursorId,
                limit = query.Limit + 1,
            },
            cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        var page = rows.Take(query.Limit).Select(ToDto).ToList();

        return Result.Success(new SecurityEventList(
            page,
            hasMore ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null));
    }

    /// <summary>
    /// One row as the wire DTO. The <c>jsonb</c> columns arrive as their text form (the statement's
    /// <c>::text</c> cast, as the audit read does) and become <see cref="JsonElement"/> here, so the
    /// persistence layer never names an HTTP type.
    /// </summary>
    private static SecurityEventItem ToDto(SecurityEventPageRow row) => new(
        row.Id,
        row.ProjectId,
        row.Type,
        row.Severity,
        JsonSerializer.Deserialize<JsonElement>(row.ActorJson),
        JsonSerializer.Deserialize<JsonElement>(row.DetailsJson),
        row.RequestId,
        row.CreatedAt);
}
