using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;

namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The Dapper read of <see cref="IRunPlanReader"/> (05.2.1's <c>run</c>/<c>planned_execution</c> and 0005's
/// <c>project</c>, 06.1.2 §5): one statement over the ambient tenant scope, so it joins the caller's transaction
/// and RLS scopes every row (00 §5.5 C3/C5). Read-only by construction — it never writes.
/// </summary>
internal sealed class RunPlanReader(ITenantDb db) : IRunPlanReader
{
    /// <inheritdoc />
    public async ValueTask<RunPlanLookup?> FindAsync(Guid runId, string executionKey, CancellationToken cancellationToken)
    {
        var row = await db.QuerySingleOrDefaultAsync<RunPlanRow>(
            OrchestratorSql.RunPlan,
            new { run_id = runId, execution_key = executionKey },
            cancellationToken).ConfigureAwait(false);

        return row is null ? null : new RunPlanLookup(row.ProjectId, row.ProjectState, row.Planned);
    }

    /// <summary>One row as <see cref="OrchestratorSql.RunPlan"/> returns it (snake_case → PascalCase, Dapper's underscore map).</summary>
    private sealed class RunPlanRow
    {
        public Guid ProjectId { get; set; }

        public string ProjectState { get; set; } = string.Empty;

        public bool Planned { get; set; }
    }
}
