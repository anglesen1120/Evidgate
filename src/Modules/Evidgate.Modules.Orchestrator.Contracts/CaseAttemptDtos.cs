namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// One attempt 04's event commit asks 05 to persist (05.3.3 §5, FR-05-15). The <c>case_id</c> is never an input: the writer
/// resolves it from the planned execution row of <see cref="ExecutionKey"/> (05.3.3-T1 decision 4).
/// </summary>
/// <param name="ExecutionKey">The planned execution's key, <c>^[0-9a-f]{64}$</c> (00 §7).</param>
/// <param name="AttemptNo">The attempt number, starting at 1 (00 §7).</param>
/// <param name="RawStatus">The status exactly as the producer sent it (0..64 code points, no U+0000); stored verbatim, never overwritten.</param>
/// <param name="StartedAt">When the attempt started, or null.</param>
/// <param name="FinishedAt">When the attempt finished, or null; never before <paramref name="StartedAt"/>.</param>
/// <param name="DurationMs">The attempt duration in milliseconds, or null; never negative.</param>
/// <param name="Xfail">
/// True when the allow-listed <c>test.finished</c> <c>ext.xfail</c> is set (05.3.3-T1 decision 1). Producer-asserted data: the
/// caller takes it only from the allow-listed <c>ext</c> and never from the adapter's own <c>ext.normalized</c>.
/// </param>
/// <param name="JobId">The runner job in agent mode (V2), or null in CI mode; no foreign key until 05.7.2-T1.</param>
public sealed record CaseAttemptInput(
    string ExecutionKey,
    int AttemptNo,
    string RawStatus,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    int? DurationMs = null,
    bool Xfail = false,
    Guid? JobId = null);

/// <summary>The result of one attempt in a <see cref="ICaseAttemptWriter.WriteAttemptsAsync"/> batch (00 §9.4 per-event ack).</summary>
public enum CaseAttemptWriteStatus
{
    /// <summary>A new row was written inside the caller's transaction.</summary>
    Accepted,

    /// <summary>The slot <c>(run, execution_key, attempt_no)</c> already holds a row with the same payload; nothing was written (idempotent replay).</summary>
    Duplicate,

    /// <summary>The slot already holds a row with a different payload (or an earlier attempt of this batch did): the first writer wins and nothing was rewritten (AC2, T50). The caller maps it to 422 at the event index.</summary>
    DuplicateDifferentPayload,

    /// <summary>The run is not visible to the tenant/project scope, or it has no planned execution with this key (T90).</summary>
    UnknownExecution,

    /// <summary>The attempt is malformed (see <see cref="CaseAttemptWriteOutcome.Reason"/>); nothing was written.</summary>
    Invalid,
}

/// <summary>The outcome of one attempt; <see cref="Reason"/> is a closed token set for <see cref="CaseAttemptWriteStatus.Invalid"/> and null otherwise.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Reason">
/// For <see cref="CaseAttemptWriteStatus.Invalid"/> only: <c>execution_key_invalid</c>, <c>attempt_no_invalid</c>, <c>raw_status_invalid</c>,
/// <c>duration_invalid</c> or <c>timestamps_invalid</c>. Never carries a producer value.
/// </param>
public sealed record CaseAttemptWriteOutcome(CaseAttemptWriteStatus Status, string? Reason = null)
{
    /// <summary>True for the three outcomes the caller must answer with a refusal and a rollback of its transaction.</summary>
    public bool IsRefusal => Status is CaseAttemptWriteStatus.DuplicateDifferentPayload or CaseAttemptWriteStatus.UnknownExecution or CaseAttemptWriteStatus.Invalid;
}
