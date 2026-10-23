using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Tenancy.Contracts;
using Evidgate.Modules.Tenancy.Domain;
using Evidgate.Modules.Tenancy.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Tenancy.Application.Queries;

/// <summary>Keyset page of the org's active projects (<c>GET /v1/orgs/{o}/projects</c>, 02.1.1 §5).</summary>
/// <param name="OrgId">The org from the route; another org's id is 404.</param>
/// <param name="Cursor">The opaque cursor of the previous page, or null for the first.</param>
/// <param name="Limit">Page size, 1–200 (validated at the edge).</param>
internal sealed record ListProjectsQuery(Guid OrgId, string? Cursor, int Limit) : IQuery<Result<ProjectListResponse>>;

/// <summary>
/// Handler of <see cref="ListProjectsQuery"/>: exactly one statement per page (P3) —
/// <c>ProjectsFirstPage</c> without a cursor, <c>ProjectsNextPage</c> with the row
/// comparison as an index condition on <c>project_org_status_created_id_idx</c> (D8).
/// One extra row is read to decide whether a next page exists, so no count query runs.
/// </summary>
[Permission(Permissions.Project.Read)]
internal sealed class ListProjectsQueryHandler(ITenantDb db, ITenantScopeAccessor scopes)
    : IQueryHandler<ListProjectsQuery, Result<ProjectListResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<ProjectListResponse>> Handle(
        ListProjectsQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Tenancy, nameof(ListProjectsQuery));

        var orgId = scopes.Require().OrgId;

        if (query.OrgId != orgId)
        {
            return Result.Failure<ProjectListResponse>(TenancyErrors.OrgNotFound);
        }

        IReadOnlyList<ProjectDto> rows;

        if (query.Cursor is null)
        {
            rows = await db.QueryAsync<ProjectDto>(
                TenancyReadSql.ProjectsFirstPage,
                new { org_id = orgId, status = TenancyStatus.Active, limit = query.Limit + 1 },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var cursorCreatedAt, out var cursorId))
            {
                return Result.Failure<ProjectListResponse>(Error.Invalid(
                    "the cursor is not a value this endpoint issued",
                    [new ErrorDetail("cursor", "malformed")]));
            }

            rows = await db.QueryAsync<ProjectDto>(
                TenancyReadSql.ProjectsNextPage,
                new
                {
                    org_id = orgId,
                    status = TenancyStatus.Active,
                    cursor_created_at = cursorCreatedAt,
                    cursor_id = cursorId,
                    limit = query.Limit + 1,
                },
                cancellationToken).ConfigureAwait(false);
        }

        var hasMore = rows.Count > query.Limit;
        IReadOnlyList<ProjectDto> page = hasMore ? [.. rows.Take(query.Limit)] : rows;

        return Result.Success(new ProjectListResponse(
            page,
            hasMore ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null));
    }
}
