namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// The in-transaction run-scope lookup 04.1.1's atomic event-batch commit needs (04.1.1-T1, rulings T90/T91;
/// 00 §5.5 C5: another module reaches the Orchestrator only through this project). One statement over the ambient
/// tenant scope, so it joins the caller's transaction and RLS scopes every row: the run row is read
/// <c>FOR SHARE</c>, which serialises it against the <c>FOR NO KEY UPDATE</c> the run state transition takes
/// (a cancel, a completion, the first event; T12/T81), so a committed state change is never missed by a batch
/// that already read the row. An unknown or foreign-org id is the same answer — null.
/// </summary>
public interface IRunEventScope
{
    /// <summary>
    /// One statement on the ambient tenant scope: the run row <c>FOR SHARE</c> (the project id, the project's
    /// state and the run's state) and, in the same statement, which of <paramref name="executionKeys"/> are planned
    /// executions of the run — or null when no run with that id is visible to the tenant (another org answers the
    /// same null).
    /// <para>
    /// The membership probe is one <c>execution_key = ANY(@keys)</c> on the planned-execution unique index
    /// (<c>planned_execution_run_execution_key</c>), never a load of the run's whole planned set (00 §5.7 P3,
    /// T90). The caller passes the batch's distinct keys; a key absent from the answer is not planned.
    /// </para>
    /// </summary>
    /// <param name="runId">The run the batch names.</param>
    /// <param name="executionKeys">
    /// The batch's execution keys, distinct, 0..500 of 64 lowercase hex characters (00 §7). An empty collection
    /// answers an empty planned set.
    /// </param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <exception cref="ArgumentNullException"><paramref name="executionKeys"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="executionKeys"/> holds more than 500 keys, or a key is not 64 lowercase hex characters.
    /// Callers have already validated their envelopes; this is the guard against SQL abuse.
    /// </exception>
    /// <exception cref="InvalidOperationException">No tenant scope is open (the tenant-scope guard throws before any statement).</exception>
    ValueTask<RunEventScope?> ReadAsync(Guid runId, IReadOnlyCollection<string> executionKeys, CancellationToken cancellationToken);
}

/// <summary>The run-scope answer of <see cref="IRunEventScope.ReadAsync"/>.</summary>
/// <param name="ProjectId">The project the run belongs to; the caller compares it with its own scope.</param>
/// <param name="ProjectState">The project's state (<c>active</c>, <c>suspended</c>, <c>deleted</c>; migration 0005).</param>
/// <param name="RunState">
/// The run's state (0031's <c>run_state_check</c>: <c>created</c>, <c>queued</c>, <c>running</c>, <c>collecting</c>,
/// <c>complete</c>, <c>incomplete</c>, <c>canceled</c>). The caller derives <c>after_cancel</c> (T12) and
/// <c>run_closed</c> (T81) from it; the row was read <c>FOR SHARE</c>, so the answer cannot race a committed
/// cancel or completion.
/// </param>
/// <param name="PlannedKeys">
/// The subset of the caller's keys that are planned executions of the run (T90). A key outside it is an unknown
/// execution the caller rejects per event; <c>planned_execution</c> has no <c>attempt_no</c> column, so only
/// membership is answered here.
/// </param>
/// <param name="LateWindowOpen">
/// True when <c>now()</c> is at or before the run's late-window end (T81/T93), read by the one predicate
/// <see cref="RunAdmissionPolicy.LateWindowOpenSql"/>: <c>collection_deadline_at + late_window_minutes</c> for an
/// <c>incomplete</c> run, <c>completed_at + late_window_minutes</c> for a <c>complete</c> one (false while
/// <c>completed_at</c> is null, the fail-closed answer for rows that predate it). The caller stores an event that
/// arrives in that window with <c>late = true</c>. The end is computed in the database against the transaction clock,
/// so the caller's clock never decides the boundary. Both instants are columns of the locked run row
/// (<c>late_window_minutes</c> is the immutable snapshot 07.1.1-T4.2 pinned at run create, <c>completed_at</c> is
/// stamped once by the transition), so the answer cannot race a policy change.
/// </param>
public sealed record RunEventScope(
    Guid ProjectId,
    string ProjectState,
    string RunState,
    IReadOnlySet<string> PlannedKeys,
    bool LateWindowOpen);
