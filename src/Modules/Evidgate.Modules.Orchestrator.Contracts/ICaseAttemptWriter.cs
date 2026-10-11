namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// The write interface 04's event-batch commit calls to persist case attempts (05.3.3-T2.1, FR-05-15, ruling T50). It runs
/// <b>inside the caller's tenant transaction</b>: it never opens a connection or a transaction, never commits and never rolls
/// back, so a rollback of the caller leaves no attempt row (AC5: nothing is visible before the caller commits).
/// </summary>
public interface ICaseAttemptWriter
{
    /// <summary>The largest batch one call accepts: the events route caps a batch at 500 events (00 §9.3).</summary>
    const int MaxBatchSize = 500;

    /// <summary>
    /// Persists <paramref name="attempts"/> of <paramref name="runId"/> and answers one outcome per attempt, index-aligned.
    /// <para>
    /// One validation pass runs first; when any attempt is <see cref="CaseAttemptWriteStatus.Invalid"/> or repeats an earlier slot of
    /// the same batch with a different payload, <b>no statement runs</b>. Otherwise one statement inserts every attempt with
    /// <c>ON CONFLICT (org_id, run_id, execution_key, attempt_no) DO NOTHING</c>, and a second bounded statement runs only when
    /// some attempt was not inserted, to tell a duplicate (same payload) from a different payload or an unknown execution. The caller
    /// MUST roll back its transaction when any outcome <see cref="CaseAttemptWriteOutcome.IsRefusal"/>.
    /// </para>
    /// </summary>
    /// <param name="runId">The run the attempts belong to; it must be visible to the caller's scope.</param>
    /// <param name="attempts">1..<see cref="MaxBatchSize"/> attempts.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="ArgumentException"><paramref name="attempts"/> is empty or longer than <see cref="MaxBatchSize"/>.</exception>
    /// <exception cref="InvalidOperationException">No tenant scope is open (the tenant-scope guard throws before any statement).</exception>
    ValueTask<IReadOnlyList<CaseAttemptWriteOutcome>> WriteAttemptsAsync(
        Guid runId,
        IReadOnlyList<CaseAttemptInput> attempts,
        CancellationToken cancellationToken);
}

/// <summary>
/// Marks the dependants of a failed setup <c>blocked</c> (05.3.3-T2.2, FR-05-15, AC4): a dependant never reads as passed after its
/// setup failed. Same transaction rules as <see cref="ICaseAttemptWriter"/>.
/// </summary>
public interface IBlockedAttemptResolver
{
    /// <summary>
    /// Finds every execution of the run that depends, directly or transitively, on <paramref name="failedExecutionKey"/> through the
    /// run's suite revision (Catalog's <c>case_dependency</c> DAG), restricted to the run's planned executions with the same
    /// <c>target_env</c>, and writes one <c>blocked</c> attempt (<c>attempt_no = 1</c>, raw and normalised status both
    /// <c>blocked</c>) for each that has no attempt yet. Idempotent: a repeated call writes nothing and returns an empty list.
    /// <para>
    /// Nothing is written, and an empty list is returned, when the run or the execution is unknown to the scope or the execution has no
    /// <c>failed</c>, <c>timed_out</c> or <c>error</c> attempt in this run. Which attempt of a retried setup counts as the final failure is
    /// the caller's decision (04.1.1/07.4.2): it calls this once it has decided the setup failed.
    /// </para>
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="failedExecutionKey">The failed setup's execution key.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The execution keys of the blocked rows written by this call.</returns>
    ValueTask<IReadOnlyList<string>> ResolveDependantsAsync(Guid runId, string failedExecutionKey, CancellationToken cancellationToken);
}
