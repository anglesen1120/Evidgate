using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Mediator;

namespace Evidgate.Modules.Healing.Application;

/// <summary>One incident (<c>GET /v1/incidents/{i}</c>).</summary>
/// <param name="IncidentId">The incident id from the route.</param>
internal sealed record GetIncidentQuery(Guid IncidentId) : IQuery<Result<IncidentView>>;

/// <summary>
/// Handler of <see cref="GetIncidentQuery"/>: <c>healing:triage</c> reads. An unknown id and another tenant's id
/// answer 404; a visible incident of a project outside the credential's or the request's scope is 403
/// <c>project_scope_mismatch</c>, like the list.
/// </summary>
[Permission(Permissions.Healing.Triage)]
internal sealed class GetIncidentQueryHandler(
    IncidentReadStore store,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetIncidentQuery, Result<IncidentView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<IncidentView>> Handle(GetIncidentQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Healing, nameof(GetIncidentQuery));

        // RLS answers null for an unknown or another tenant's id: that is the 404. A visible row of a project
        // outside the credential's or the request's scope is the 403 the other healing routes give.
        var incident = await store.GetAsync(query.IncidentId, cancellationToken).ConfigureAwait(false);
        if (incident is null)
        {
            return Result.Failure<IncidentView>(Error.NotFound("no incident with that id is visible to this tenant"));
        }

        return HealingScopeGuard.Project(incident.ProjectId, principals, tenant) is { } mismatch
            ? Result.Failure<IncidentView>(mismatch)
            : Result.Success(incident);
    }
}
