using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Orchestrator.Application.Releases;

/// <summary>Read one release (<c>GET /v1/releases/{id}</c>, 05.6.1 §5).</summary>
/// <param name="ReleaseId">The release from the route.</param>
internal sealed record GetReleaseQuery(Guid ReleaseId) : IQuery<Result<ReleaseDetail>>;

/// <summary>
/// One release and its components (05.6.1 §5): the header, then the component rows in <c>component_key</c> order. Two
/// statements and no join (00 §5.7 P3); the bound-runs section is 05.6.1-T2's, so this read never touches
/// <c>release_run</c> and cannot show a recency-derived set (T15).
/// </summary>
/// <param name="ReleaseId">The release's id.</param>
/// <param name="ProjectId">The project the release belongs to.</param>
/// <param name="Name">The declaration's name.</param>
/// <param name="State"><c>draft</c> or <c>active</c>.</param>
/// <param name="CreatedBy">The audit actor that declared it (<c>&lt;type&gt;:&lt;id&gt;</c>).</param>
/// <param name="CreatedAt">When the declaration was stored.</param>
/// <param name="Version">The optimistic-concurrency token of the row.</param>
/// <param name="Components">Every component, ordered by <c>component_key</c>.</param>
public sealed record ReleaseDetail(
    Guid ReleaseId,
    Guid ProjectId,
    string Name,
    string State,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    int Version,
    IReadOnlyList<ReleaseComponentDetail> Components);

/// <summary>One component of a release: the key, the digest it was built at and the environment revision it was built for.</summary>
/// <param name="ComponentKey">The component's key inside its release.</param>
/// <param name="Digest">The build digest, <c>sha256:</c> + 64 lower-case hex.</param>
/// <param name="EnvironmentRevisionId">The environment revision the digest was built for.</param>
public sealed record ReleaseComponentDetail(string ComponentKey, string Digest, Guid EnvironmentRevisionId);

/// <summary>
/// The read path of a release: the header under RLS, then its components. An unknown id, another tenant's id and a
/// release of a sibling project all answer the one 404 (00 §9.2), so neither existence nor a project's release set leaks
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
