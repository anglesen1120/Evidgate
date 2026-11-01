using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Policy.Contracts;
using Evidgate.Modules.Policy.Infrastructure.Persistence;
using Mediator;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Policy.Application.Queries;

/// <summary>
/// One run's decision as a polled read (<c>GET /v1/runs/{r}/decision</c>): the newest <c>live</c> evaluation, or a
/// pending body while the run has none. A <c>shadow</c> evaluation is a second opinion and is never served.
/// </summary>
/// <param name="RunId">The run from the route.</param>
internal sealed record GetRunDecisionQuery(Guid RunId) : IQuery<Result<Decision>>;

/// <summary>
/// Handler of <see cref="GetRunDecisionQuery"/>. The run's existence and project scope come from the Orchestrator
/// contract rather than a join, so this module's only statement reads its own <c>evaluation</c> table: an unknown run,
/// another tenant's run and — for a project-scoped credential — a sibling project's run take the one <c>404 not_found</c>
/// of <c>GET /v1/runs/{r}</c>, so the answer is no existence oracle.
/// </summary>
/// <remarks>
/// The request's tenant transaction is read-only for a query, so nothing here writes, locks or enqueues an outbox row;
/// the decision body costs the plan read plus one indexed lookup on the live key, whatever the size of the row's
/// reason arrays.
/// </remarks>
[Permission(Permissions.Decision.Read)]
internal sealed class GetRunDecisionQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IOrchestratorEvaluationPlan plans,
    IOptions<RunDecisionOptions> options)
    : IQueryHandler<GetRunDecisionQuery, Result<Decision>>
{
    /// <inheritdoc />
    public async ValueTask<Result<Decision>> Handle(GetRunDecisionQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(GetRunDecisionQuery));

        var orgId = scopes.Require().OrgId;
        var plan = await plans.GetEvaluationRunPlanAsync(orgId, query.RunId, cancellationToken).ConfigureAwait(false);
        if (plan is null || PolicyScope.Project(plan.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<Decision>(RunNotFound);
        }

        var url = options.Value.RunUrl(plan.ProjectId, query.RunId);
        var row = await db.QuerySingleOrDefaultAsync<RunDecisionRow>(
                RunDecisionSql.LatestLive,
                new { org_id = orgId, run_id = query.RunId },
                cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(row is null ? Decision.Pending(url) : Evaluated(row, url));
    }

    /// <summary>
    /// The evaluated body: every member is copied from the one row the statement returned, so the response can never mix
    /// two verdicts, and <c>mode</c> is the row's own <c>live</c> slot.
    /// </summary>
    private static Decision Evaluated(RunDecisionRow row, string? url) =>
        new(
            DecisionStatus.Evaluated,
            row.DecisionValue,
            row.EvaluationId,
            row.EvidenceRevision,
            row.Completeness,
            row.Outcome,
            row.Missing,
            row.Reasons,
            row.PolicyHash,
            row.ManifestHash,
            row.EvaluatorVersion,
            row.Mode,
            url);

    /// <summary>
    /// 404 <c>not_found</c>: no run with that id is visible to this tenant — one body for an unknown id, another
    /// tenant's run and a sibling project's run, and the same text <c>GET /v1/runs/{r}</c> answers with.
    /// </summary>
    private static Error RunNotFound => Error.NotFound("no run with that id is visible to this tenant");
}
