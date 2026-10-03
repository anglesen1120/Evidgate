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

/// <summary>Keyset page of a project's cases (<c>GET /v1/projects/{p}/cases</c>, 03.1.1 §5).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Status">Optional status filter; without it archived cases are left out.</param>
/// <param name="Framework">Optional framework filter.</param>
/// <param name="Cursor">The opaque cursor of the previous page, or null.</param>
/// <param name="Limit">Page size, 1–200 (validated at the edge).</param>
internal sealed record ListCasesQuery(Guid ProjectId, string? Status, string? Framework, string? Cursor, int Limit)
    : IQuery<Result<CaseListResponse>>;

/// <summary>
/// Handler of <see cref="ListCasesQuery"/>: one statement per page (P3) — the first page carries no cursor
/// predicate, the next the row comparison over <c>test_case_list_idx</c>; one extra row decides whether a next
/// page exists, so no count runs. A project the tenant cannot see is 404 and a credential scoped elsewhere 403,
/// both decided before the cases are read.
/// </summary>
[Permission(Permissions.Case.Read)]
internal sealed class ListCasesQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<ListCasesQuery, Result<CaseListResponse>>
{
    private static readonly string[] Statuses = [CaseStatus.Active, CaseStatus.Missing, CaseStatus.Archived];

    /// <inheritdoc />
    public async ValueTask<Result<CaseListResponse>> Handle(ListCasesQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(ListCasesQuery));

        if (CatalogScope.Project(query.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<CaseListResponse>(mismatch);
        }

        if (query.Status is not null && !Statuses.Contains(query.Status, StringComparer.Ordinal))
        {
            return Result.Failure<CaseListResponse>(Error.Invalid(
                "status must be one of active, missing, archived",
                [new ErrorDetail("status", "invalid")]));
        }

        var orgId = scopes.Require().OrgId;
        var exists = await db.ScalarAsync<int?>(CatalogReadSql.ProjectExists, new { project_id = query.ProjectId }, cancellationToken)
            .ConfigureAwait(false);

        if (exists is null)
        {
            return Result.Failure<CaseListResponse>(CatalogErrors.ProjectNotFound);
        }

        IReadOnlyList<CaseRow> rows;
        if (query.Cursor is null)
        {
            rows = await db.QueryAsync<CaseRow>(
                CatalogReadSql.CasesFirstPage,
                new { org_id = orgId, project_id = query.ProjectId, status = query.Status, framework = query.Framework, limit = query.Limit + 1 },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var cursorCreatedAt, out var cursorId))
            {
                return Result.Failure<CaseListResponse>(Error.Invalid(
                    "the cursor is not a value this endpoint issued",
                    [new ErrorDetail("cursor", "malformed")]));
            }

            rows = await db.QueryAsync<CaseRow>(
                CatalogReadSql.CasesNextPage,
                new
                {
                    org_id = orgId,
                    project_id = query.ProjectId,
                    status = query.Status,
                    framework = query.Framework,
                    cursor_created_at = cursorCreatedAt,
                    cursor_id = cursorId,
                    limit = query.Limit + 1,
                },
                cancellationToken).ConfigureAwait(false);
        }

        var hasMore = rows.Count > query.Limit;
        var page = (hasMore ? rows.Take(query.Limit) : rows).Select(row => row.ToDto()).ToList();

        return Result.Success(new CaseListResponse(
            page,
            hasMore ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null));
    }
}

/// <summary>One case with its identity trail (<c>GET /v1/cases/{c}</c>).</summary>
/// <param name="CaseId">The case from the route.</param>
internal sealed record GetCaseQuery(Guid CaseId) : IQuery<Result<CaseDetailDto>>;

/// <summary>Handler of <see cref="GetCaseQuery"/>: one statement; another org's case is indistinguishable from a missing one (404).</summary>
[Permission(Permissions.Case.Read)]
internal sealed class GetCaseQueryHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetCaseQuery, Result<CaseDetailDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<CaseDetailDto>> Handle(GetCaseQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(GetCaseQuery));

        var detail = await CaseReader.DetailAsync(db, query.CaseId, cancellationToken).ConfigureAwait(false);

        if (detail is null)
        {
            return Result.Failure<CaseDetailDto>(CatalogErrors.CaseNotFound);
        }

        // A credential scoped to project A learns nothing about project B's case: the same 404.
        return CatalogScope.Project(detail.Case.ProjectId, principals, tenant) is not null
            ? Result.Failure<CaseDetailDto>(CatalogErrors.CaseNotFound)
            : Result.Success(detail);
    }
}

/// <summary>Revisions of a case, newest first (<c>GET /v1/cases/{c}/revisions</c>, 03.1.3 AC5).</summary>
/// <param name="CaseId">The case from the route.</param>
/// <param name="Cursor">The opaque cursor, or null.</param>
/// <param name="Limit">Page size, 1–200.</param>
internal sealed record GetCaseRevisionsQuery(Guid CaseId, string? Cursor, int Limit) : IQuery<Result<CaseRevisionListResponse>>;

/// <summary>
/// Handler of <see cref="GetCaseRevisionsQuery"/>: 404 for a case the tenant cannot see; otherwise one case check and
/// one page statement (descending keyset on <c>test_revision_case_idx</c>, one extra row decides the next page).
/// </summary>
[Permission(Permissions.Case.Read)]
internal sealed class GetCaseRevisionsQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetCaseRevisionsQuery, Result<CaseRevisionListResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<CaseRevisionListResponse>> Handle(GetCaseRevisionsQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(GetCaseRevisionsQuery));

        var found = await CaseReader.CaseAsync(db, query.CaseId, cancellationToken).ConfigureAwait(false);

        if (found is null || CatalogScope.Project(found.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<CaseRevisionListResponse>(CatalogErrors.CaseNotFound);
        }

        var orgId = scopes.Require().OrgId;
        IReadOnlyList<CaseRevisionDto> rows;

        if (query.Cursor is null)
        {
            rows = await db.QueryAsync<CaseRevisionDto>(
                CatalogReadSql.RevisionsFirstPage,
                new { org_id = orgId, case_id = query.CaseId, limit = query.Limit + 1 },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var cursorCreatedAt, out var cursorId))
            {
                return Result.Failure<CaseRevisionListResponse>(Error.Invalid(
                    "the cursor is not a value this endpoint issued",
                    [new ErrorDetail("cursor", "malformed")]));
            }

            rows = await db.QueryAsync<CaseRevisionDto>(
                CatalogReadSql.RevisionsNextPage,
                new { org_id = orgId, case_id = query.CaseId, cursor_created_at = cursorCreatedAt, cursor_id = cursorId, limit = query.Limit + 1 },
                cancellationToken).ConfigureAwait(false);
        }

        var hasMore = rows.Count > query.Limit;
        IReadOnlyList<CaseRevisionDto> page = hasMore ? [.. rows.Take(query.Limit)] : rows;

        return Result.Success(new CaseRevisionListResponse(
            page,
            hasMore ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null));
    }
}
