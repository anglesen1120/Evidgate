using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Catalog.Application.Queries;

/// <summary>Cursor page of a project's fixtures (<c>GET /v1/projects/{p}/fixtures</c>, 03.3.1 §5).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Cursor">Opaque cursor, or null.</param>
/// <param name="Limit">Page size, 1–200.</param>
internal sealed record ListFixturesQuery(Guid ProjectId, string? Cursor, int Limit) : IQuery<Result<FixtureListResponse>>;

/// <summary>Handler of <see cref="ListFixturesQuery"/>: project check, then one keyset statement (one extra row decides the next page).</summary>
[Permission(Permissions.Suite.Read)]
internal sealed class ListFixturesQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<ListFixturesQuery, Result<FixtureListResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<FixtureListResponse>> Handle(ListFixturesQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(ListFixturesQuery));

        if (CatalogScope.Project(query.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<FixtureListResponse>(mismatch);
        }

        var exists = await db.ScalarAsync<int?>(CatalogReadSql.ProjectExists, new { project_id = query.ProjectId }, cancellationToken).ConfigureAwait(false);
        if (exists is null)
        {
            return Result.Failure<FixtureListResponse>(CatalogErrors.ProjectNotFound);
        }

        var orgId = scopes.Require().OrgId;

        IReadOnlyList<FixtureRow> rows;
        if (query.Cursor is null)
        {
            rows = await db.QueryAsync<FixtureRow>(
                FixtureReadSql.FixturesFirstPage,
                new { org_id = orgId, project_id = query.ProjectId, limit = query.Limit + 1 },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var createdAt, out var id))
            {
                return Result.Failure<FixtureListResponse>(Error.Invalid("the cursor is not a value this endpoint issued", [new ErrorDetail("cursor", "malformed")]));
            }

            rows = await db.QueryAsync<FixtureRow>(
                FixtureReadSql.FixturesNextPage,
                new { org_id = orgId, project_id = query.ProjectId, cursor_created_at = createdAt, cursor_id = id, limit = query.Limit + 1 },
                cancellationToken).ConfigureAwait(false);
        }

        var hasMore = rows.Count > query.Limit;
        var page = (hasMore ? rows.Take(query.Limit) : rows).Select(row => row.ToDto()).ToList();

        return Result.Success(new FixtureListResponse(page, hasMore ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null));
    }
}

/// <summary>One fixture with its head revision (<c>GET /v1/fixtures/{f}</c>, 03.3.1 §5).</summary>
/// <param name="FixtureId">The fixture from the route.</param>
internal sealed record GetFixtureQuery(Guid FixtureId) : IQuery<Result<FixtureDetailDto>>;

/// <summary>Handler of <see cref="GetFixtureQuery"/>: 404 for another org's or another project's fixture; the body is never read.</summary>
[Permission(Permissions.Suite.Read)]
internal sealed class GetFixtureQueryHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetFixtureQuery, Result<FixtureDetailDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<FixtureDetailDto>> Handle(GetFixtureQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Catalog, nameof(GetFixtureQuery));

        var fixture = await db.QuerySingleOrDefaultAsync<FixtureRow>(FixtureReadSql.FixtureById, new { fixture_id = query.FixtureId }, cancellationToken).ConfigureAwait(false);
        if (fixture is null || CatalogScope.Project(fixture.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<FixtureDetailDto>(FixtureErrors.NotFound);
        }

        // The head is nullable only for a fixture inserted outside this API; a detail route that fabricated one would
        // claim a body the fixture does not have.
        FixtureRevisionDto? head = null;
        if (fixture.HeadRevisionId is { } headId)
        {
            head = await db.QuerySingleOrDefaultAsync<FixtureRevisionDto>(FixtureReadSql.RevisionById, new { revision_id = headId }, cancellationToken).ConfigureAwait(false);
        }

        return Result.Success(new FixtureDetailDto(fixture.Id, fixture.OrgId, fixture.ProjectId, fixture.FixtureKey, fixture.CreatedAt, head));
    }
}

/// <summary>
/// The flat row both fixture reads return: the identity plus the columns of its head revision, which the lateral join
/// adds in one statement. One shape for the list and the detail means the page and the item can never disagree.
/// </summary>
internal sealed class FixtureRow
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid ProjectId { get; set; }

    public string FixtureKey { get; set; } = string.Empty;

    public Guid? HeadRevisionId { get; set; }

    public int? HeadRevisionNo { get; set; }

    public string? HeadRevisionHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The wire shape of this row.</summary>
    public FixtureDto ToDto() =>
        new(Id, OrgId, ProjectId, FixtureKey, HeadRevisionId, HeadRevisionNo, HeadRevisionHash, CreatedAt);
}
