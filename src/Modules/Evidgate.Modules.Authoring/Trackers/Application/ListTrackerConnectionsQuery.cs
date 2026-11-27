using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Contracts;
using Mediator;

namespace Evidgate.Modules.Authoring.Trackers.Application;

/// <summary>Lists the project's tracker connections, whatever their state, without any token.</summary>
/// <param name="ProjectId">The project from the route.</param>
internal sealed record ListTrackerConnectionsQuery(Guid ProjectId) : IQuery<Result<TrackerConnectionListResponse>>;

/// <summary>Handler of <see cref="ListTrackerConnectionsQuery"/>.</summary>
/// <param name="db">The tenant-scoped database, for the project check.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, whose token may be narrowed to one project.</param>
/// <param name="tenant">The request's tenant context, for the project scope check.</param>
/// <param name="store">The connection store.</param>
[Permission(Permissions.Project.Read)]
internal sealed class ListTrackerConnectionsQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    ITrackerConnectionStore store) : IQueryHandler<ListTrackerConnectionsQuery, Result<TrackerConnectionListResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<TrackerConnectionListResponse>> Handle(ListTrackerConnectionsQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(ListTrackerConnectionsQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<TrackerConnectionListResponse>(scopeError);
        }

        if (await AuthoringProjectProbe.MissingAsync(db, scopes.Require().OrgId, query.ProjectId, cancellationToken).ConfigureAwait(false) is { } missing)
        {
            return Result.Failure<TrackerConnectionListResponse>(missing);
        }

        var views = await store.ListAsync(query.ProjectId, cancellationToken).ConfigureAwait(false);
        return Result.Success(new TrackerConnectionListResponse([.. views.Select(TrackerConnectionMapping.ToDto)]));
    }
}
