using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Policy.Contracts;
using Evidgate.Modules.Policy.Domain;
using Evidgate.Modules.Policy.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Policy.Application.Queries;

/// <summary>One release's stored decision as a polled read (<c>GET /v1/releases/{id}/decision</c>).</summary>
/// <param name="ReleaseId">The release from the route.</param>
internal sealed record GetReleaseDecisionQuery(Guid ReleaseId) : IQuery<Result<ReleaseDecision>>;

/// <summary>
/// Handler of <see cref="GetReleaseDecisionQuery"/>: the newest stored aggregate, or the pending body while there is
/// none. A pure read: it reads two things (the release's project through the Orchestrator contract, then one row of
/// this module's table) and writes, enqueues and dispatches nothing. An unknown, another tenant's and a sibling
/// project's release take one 404.
/// </summary>
[Permission(Permissions.Decision.Read)]
internal sealed class GetReleaseDecisionQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IReleaseAggregationReader releases)
    : IQueryHandler<GetReleaseDecisionQuery, Result<ReleaseDecision>>
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <inheritdoc />
    public async ValueTask<Result<ReleaseDecision>> Handle(GetReleaseDecisionQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(GetReleaseDecisionQuery));

        var projectId = await releases.GetProjectIdAsync(query.ReleaseId, cancellationToken).ConfigureAwait(false);
        if (projectId is not { } project || PolicyScope.Project(project, principals, tenant) is not null)
        {
            return Result.Failure<ReleaseDecision>(Error.NotFound("no release with that id is visible to this tenant"));
        }

        var row = await db.QuerySingleOrDefaultAsync<Row>(
            ReleaseDecisionSql.Latest,
            new { org_id = scopes.Require().OrgId, release_id = query.ReleaseId },
            cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return Result.Success(ReleaseDecision.Pending(query.ReleaseId));
        }

        var components = JsonSerializer.Deserialize<List<ComponentEvaluation>>(row.ComponentEvaluations, Options) ?? [];
        return Result.Success(new ReleaseDecision(
            ReleaseDecisionStatus.Aggregated,
            row.DecisionValue,
            query.ReleaseId,
            row.Id,
            row.PolicyRevisionId,
            row.Completeness,
            row.Reasons,
            [.. components.Select(c => new ReleaseComponentDecision(c.ComponentId, c.RunId, c.EvidenceRevision, c.Status, c.Decision, c.Reasons))],
            row.InputsHash,
            row.CreatedAt));
    }

    private sealed class Row
    {
        public Guid Id { get; set; }

        public Guid PolicyRevisionId { get; set; }

        public string ComponentEvaluations { get; set; } = "[]";

        public string Completeness { get; set; } = string.Empty;

        public string DecisionValue { get; set; } = string.Empty;

        public string[] Reasons { get; set; } = [];

        public string InputsHash { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }
    }
}
