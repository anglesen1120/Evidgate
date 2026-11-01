using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

namespace Evidgate.Modules.Orchestrator.Application.Evaluation;

/// <summary>
/// <see cref="IOrchestratorEvaluationPlan"/> over the caller's tenant transaction. It never opens a connection or a
/// transaction and never commits: the read sees the caller's uncommitted rows and its rollback undoes nothing here
/// because nothing is written.
/// </summary>
/// <param name="db">The Dapper path over the ambient scope; it demands an active scope before it touches the database.</param>
internal sealed class EvaluationPlanReader(ITenantDb db) : IOrchestratorEvaluationPlan
{
    /// <inheritdoc />
    public async ValueTask<EvaluationRunPlan?> GetEvaluationRunPlanAsync(
        Guid orgId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<PlanRow>(
            EvaluationPlanSql.LoadPlan,
            new { org_id = orgId, run_id = runId },
            cancellationToken).ConfigureAwait(false);

        // The header repeats on every row; the left join leaves the execution columns null when the run planned nothing.
        if (rows.Count == 0)
        {
            return null;
        }

        var header = rows[0];

        var executions = new List<EvaluationPlannedExecution>(rows.Count);
        foreach (var row in rows)
        {
            if (row.ExecutionKey is not { } executionKey || row.CaseRevisionId is not { } caseRevisionId)
            {
                continue;
            }

            executions.Add(new EvaluationPlannedExecution(executionKey, caseRevisionId, row.Required is true));
        }

        return new EvaluationRunPlan(
            header.PolicyRevisionId,
            header.ProjectId,
            header.Purpose,
            header.PlannedCount,
            header.SuiteRevisionId,
            executions);
    }

    /// <summary>
    /// One row as <see cref="EvaluationPlanSql.LoadPlan"/> returns it (snake_case → PascalCase, Dapper's underscore map).
    /// The execution columns are nullable because a run with no planned execution answers a header-only row.
    /// </summary>
    private sealed class PlanRow
    {
        public Guid? PolicyRevisionId { get; set; }

        public Guid ProjectId { get; set; }

        public string Purpose { get; set; } = string.Empty;

        public int PlannedCount { get; set; }

        public Guid SuiteRevisionId { get; set; }

        public string? ExecutionKey { get; set; }

        public Guid? CaseRevisionId { get; set; }

        public bool? Required { get; set; }
    }
}
