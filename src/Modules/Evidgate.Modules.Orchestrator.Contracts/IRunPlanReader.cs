namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// The read-only run-scope lookup 06.1.2's <c>POST /v1/artifacts/upload-url</c> needs (00 §5.5 C5: another
/// module reaches the Orchestrator only through this project). One statement over the ambient tenant scope, so
/// RLS makes another org's run invisible and an unknown, foreign-org or foreign-project id is the same answer.
/// </summary>
public interface IRunPlanReader
{
    /// <summary>
    /// The run's project, the project's state and whether <paramref name="executionKey"/> is one of the run's
    /// planned executions — or null when no run with that id is visible to the tenant.
    /// </summary>
    /// <param name="runId">The run the caller named.</param>
    /// <param name="executionKey">The attempt's execution key, sha256 hex (00 §7).</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    ValueTask<RunPlanLookup?> FindAsync(Guid runId, string executionKey, CancellationToken cancellationToken);
}

/// <summary>The run-scope answer of <see cref="IRunPlanReader.FindAsync"/>.</summary>
/// <param name="ProjectId">The project the run belongs to; the caller compares it with its own scope.</param>
/// <param name="ProjectState">The project's state (<c>active</c>, <c>suspended</c>, <c>deleted</c>; migration 0005).</param>
/// <param name="Planned">
/// True when <c>execution_key</c> is a planned execution of the run. <c>planned_execution</c> has no
/// <c>attempt_no</c> column, so only the key's membership is answered here; the attempt number is a shape
/// check (<c>&gt;= 1</c>) on the command.
/// </param>
public sealed record RunPlanLookup(Guid ProjectId, string ProjectState, bool Planned);
