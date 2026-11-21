using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Catalog.Application.Queries;

/// <summary>Keyset page of a project's drafts, oldest first (<c>GET /v1/projects/{p}/drafts</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Status">Optional state filter.</param>
/// <param name="Cursor">The opaque cursor of the previous page, or null.</param>
/// <param name="Limit">Page size, 1–200.</param>
internal sealed record ListDraftsQuery(Guid ProjectId, string? Status, string? Cursor, int Limit)
    : IQuery<Result<DraftListResponse>>;

/// <summary>
/// Handler of <see cref="ListDraftsQuery"/>: one statement per page on <c>draft_project_list</c>; one extra row decides
/// whether a next page exists, so no count runs. A project the tenant cannot see is 404, a credential scoped elsewhere 403.
/// </summary>
[Permission(Permissions.Case.Read)]
internal sealed class ListDraftsQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<ListDraftsQuery, Result<DraftListResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<DraftListResponse>> Handle(ListDraftsQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(ListDraftsQuery));

        if (CatalogScope.Project(query.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<DraftListResponse>(mismatch);
        }

        if (query.Status is not null && !DraftStatus.All.Contains(query.Status, StringComparer.Ordinal))
        {
            return Result.Failure<DraftListResponse>(Error.Invalid(
                "status must be one of draft, dry_running, dry_passed, dry_failed, pr_open, merged, closed",
                [new ErrorDetail("status", "invalid")]));
        }

        var orgId = scopes.Require().OrgId;
        var exists = await db.ScalarAsync<int?>(CatalogReadSql.ProjectExists, new { project_id = query.ProjectId }, cancellationToken)
            .ConfigureAwait(false);
        if (exists is null)
        {
            return Result.Failure<DraftListResponse>(CatalogErrors.ProjectNotFound);
        }

        IReadOnlyList<DraftRow> rows;
        if (query.Cursor is null)
        {
            rows = await db.QueryAsync<DraftRow>(
                DraftReadSql.DraftsFirstPage,
                new { org_id = orgId, project_id = query.ProjectId, status = query.Status, limit = query.Limit + 1 },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var cursorCreatedAt, out var cursorId))
            {
                return Result.Failure<DraftListResponse>(Error.Invalid(
                    "the cursor is not a value this endpoint issued",
                    [new ErrorDetail("cursor", "malformed")]));
            }

            rows = await db.QueryAsync<DraftRow>(
                DraftReadSql.DraftsNextPage,
                new
                {
                    org_id = orgId,
                    project_id = query.ProjectId,
                    status = query.Status,
                    cursor_created_at = cursorCreatedAt,
                    cursor_id = cursorId,
                    limit = query.Limit + 1,
                },
                cancellationToken).ConfigureAwait(false);
        }

        var hasMore = rows.Count > query.Limit;
        var page = (hasMore ? rows.Take(query.Limit) : rows).Select(row => row.ToDto()).ToList();

        return Result.Success(new DraftListResponse(
            page,
            hasMore ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null));
    }
}

/// <summary>One draft (<c>GET /v1/drafts/{d}</c>).</summary>
/// <param name="DraftId">The draft from the route.</param>
internal sealed record GetDraftQuery(Guid DraftId) : IQuery<Result<DraftDto>>;

/// <summary>
/// Handler of <see cref="GetDraftQuery"/>: one statement; another org's draft and another project's draft are both the
/// same 404, so the id alone never tells a caller which one it hit.
/// </summary>
[Permission(Permissions.Case.Read)]
internal sealed class GetDraftQueryHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetDraftQuery, Result<DraftDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<DraftDto>> Handle(GetDraftQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(GetDraftQuery));

        var row = await DraftReader.RowAsync(db, query.DraftId, cancellationToken).ConfigureAwait(false);

        return row is null || CatalogScope.Project(row.ProjectId, principals, tenant) is not null
            ? Result.Failure<DraftDto>(DraftErrors.NotFound)
            : Result.Success(row.ToDto());
    }
}
