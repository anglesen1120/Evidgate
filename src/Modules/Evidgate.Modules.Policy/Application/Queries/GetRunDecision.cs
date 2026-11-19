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
/// Handler of <see cref="GetRunDecisionQuery"/>. The run and its project come from the Orchestrator contract rather than a
/// join, so the only statement here reads this module's <c>evaluation</c> table; an unknown run, another tenant's run and a
/// sibling project's run take one 404, so the answer is no existence oracle. The scope is read-only: nothing writes.
/// </summary>
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
        var plan = await plans.GetEvaluationRunHeaderAsync(orgId, query.RunId, cancellationToken).ConfigureAwait(false);
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
    /// The evaluated body: every member is copied from the one row the statement returned, so a response can never mix two
    /// verdicts, and <c>mode</c> is that row's own <c>live</c> slot.
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
    /// 404 <c>not_found</c>: no run with that id is visible to this tenant — one body for an unknown id, another tenant's
    /// run and a sibling project's run, matching what <c>GET /v1/runs/{r}</c> answers.
    /// </summary>
    private static Error RunNotFound => Error.NotFound("no run with that id is visible to this tenant");
}
