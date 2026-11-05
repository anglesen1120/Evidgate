using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Orchestrator.Application.Releases;

/// <summary>Read one release (<c>GET /v1/releases/{id}</c>).</summary>
/// <param name="ReleaseId">The release from the route.</param>
internal sealed record GetReleaseQuery(Guid ReleaseId) : IQuery<Result<ReleaseDetail>>;

/// <summary>
/// The read path of a release: the header under RLS, then its components. An unknown id, another tenant's id and a
/// release of a sibling project all answer the one 404, so neither existence nor a project's release set leaks
/// across the scope.
/// </summary>
[Permission(Permissions.Run.Read)]
internal sealed class GetReleaseQueryHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetReleaseQuery, Result<ReleaseDetail>>
{
    /// <inheritdoc />
    public async ValueTask<Result<ReleaseDetail>> Handle(GetReleaseQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(GetReleaseQuery));

        var row = await db.QuerySingleOrDefaultAsync<ReleaseRow>(
            ReleaseSql.ReleaseById,
            new { release_id = query.ReleaseId },
            cancellationToken).ConfigureAwait(false);
        if (row is null || OrchestratorScope.Project(row.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<ReleaseDetail>(OrchestratorErrors.ReleaseNotFound);
        }

        var components = await db.QueryAsync<ComponentRow>(
            ReleaseSql.ComponentsByRelease,
            new { org_id = row.OrgId, release_id = query.ReleaseId },
            cancellationToken).ConfigureAwait(false);

        return Result.Success(new ReleaseDetail(
            row.ReleaseId,
            row.ProjectId,
            row.Name,
            row.State,
            row.CreatedBy,
            row.CreatedAt,
            row.Version,
            [.. components.Select(component => new ReleaseComponentDetail(
                component.ComponentKey,
                component.Digest,
                component.EnvironmentRevisionId))]));
    }

    private sealed class ReleaseRow
    {
        public Guid ReleaseId { get; set; }

        public Guid OrgId { get; set; }

        public Guid ProjectId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public string CreatedBy { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }

        public int Version { get; set; }
    }

    private sealed class ComponentRow
    {
        public string ComponentKey { get; set; } = string.Empty;

        public string Digest { get; set; } = string.Empty;

        public Guid EnvironmentRevisionId { get; set; }
    }
}
