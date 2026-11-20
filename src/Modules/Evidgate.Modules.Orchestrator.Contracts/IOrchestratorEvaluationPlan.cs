namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// One run's evaluation plan as the Policy evaluator reads it: the run's own scope columns, its manifest's planned
/// count and the planned executions the result rows are assembled against. A cross-module service call, <b>not</b> a
/// Mediator query: it runs inside the caller's ambient tenant transaction, never opens, commits or rolls back one.
/// <para>
/// The read is tenant-exact beyond row level security: the trusted <c>org_id</c> is a predicate as well as a scope, so
/// a foreign org or an unknown run is the same answer — null.
/// </para>
/// </summary>
public interface IOrchestratorEvaluationPlan
{
    /// <summary>
    /// The evaluation plan of <paramref name="runId"/>, or null when this tenant has no such run. One statement serves
    /// the header and every planned execution, whatever the plan's size.
    /// </summary>
    /// <param name="orgId">The caller's trusted tenant id, so the read is tenant-exact beyond row level security.</param>
    /// <param name="runId">The run whose plan is read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<EvaluationRunPlan?> GetEvaluationRunPlanAsync(Guid orgId, Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// The run's own scope columns only, or null when this tenant has no such run. One bounded statement on
    /// <c>run</c> that never touches the manifest or the planned executions, so its cost does not grow with the plan.
    /// </summary>
    /// <param name="orgId">The caller's trusted tenant id, so the read is tenant-exact beyond row level security.</param>
    /// <param name="runId">The run whose scope is read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<EvaluationRunHeader?> GetEvaluationRunHeaderAsync(Guid orgId, Guid runId, CancellationToken cancellationToken);
}

/// <summary>The run-scope columns of an evaluation plan, without the manifest count or any planned execution.</summary>
/// <param name="ProjectId">The run's <c>project_id</c>; the caller compares it with its own scope.</param>
/// <param name="PolicyRevisionId">The run's <c>policy_revision_id</c>, nullable like the column.</param>
/// <param name="Purpose">The run's <c>purpose</c> (<c>regular</c> or <c>replay</c>).</param>
/// <param name="IsFork">The run's <c>is_fork</c>.</param>
/// <param name="SuiteRevisionId">The run's <c>suite_revision_id</c>.</param>
public sealed record EvaluationRunHeader(
    Guid ProjectId,
    Guid? PolicyRevisionId,
    string Purpose,
    bool IsFork,
    Guid SuiteRevisionId);

/// <summary>
/// The plan one evaluation judges: the run's pinned scope, the manifest it froze and every planned execution.
/// </summary>
/// <param name="PolicyRevisionId">The run's <c>policy_revision_id</c>, nullable like the column: null when the run pinned no policy.</param>
/// <param name="ProjectId">The run's <c>project_id</c>; the caller compares it with its own scope.</param>
/// <param name="Purpose">The run's <c>purpose</c> (<c>regular</c> or <c>replay</c>).</param>
/// <param name="IsFork">The run's <c>is_fork</c>: a fork run is advisory-only and its evaluation can never pass.</param>
/// <param name="PlannedCount">The pinned manifest's <c>planned_count</c>, the count the run itself planned, not the number of execution rows below.</param>
/// <param name="SuiteRevisionId">The run's <c>suite_revision_id</c>, the key the catalogue read takes, so the catalogue never reads <c>run</c>.</param>
/// <param name="Executions">The run's planned executions, in the plan's <c>execution_key</c> order, the order the evaluator assembles its result rows in.</param>
public sealed record EvaluationRunPlan(
    Guid? PolicyRevisionId,
    Guid ProjectId,
    string Purpose,
    bool IsFork,
    int PlannedCount,
    Guid SuiteRevisionId,
    IReadOnlyList<EvaluationPlannedExecution> Executions);

/// <summary>One case the run planned to execute: its key, the case revision it names and whether it gates the decision.</summary>
/// <param name="ExecutionKey">The planned execution's key, the 64 lowercase hex the run planned.</param>
/// <param name="CaseRevisionId">The <c>test_revision.id</c> the planned row carries, the key the case read takes.</param>
/// <param name="Required">The planned row's <c>required</c>: a failed required execution decides the run, an optional one does not.</param>
public sealed record EvaluationPlannedExecution(string ExecutionKey, Guid CaseRevisionId, bool Required);
