namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// One row of <c>GET /v1/runs/{r}/attempts</c> (F52, S143; 09.3.1-T4's attempts table), read from
/// <c>case_attempt</c> (0032). The wire names are <c>attempt_id</c>, <c>execution_key</c>, <c>case_id</c>,
/// <c>attempt_no</c>, <c>raw_status</c>, <c>normalized_status</c>, <c>started_at</c>, <c>finished_at</c>,
/// <c>duration_ms</c>, <c>artifact_count</c> and <c>healing_candidate</c> (the host's <c>SnakeCaseLower</c> policy).
/// <para>
/// The raw/normalised pair is kept verbatim: the producer's <c>raw_status</c> beside the server's
/// <c>normalized_status</c> (00 §8.5 vocabulary), so <c>blocked</c> is never reported as <c>passed</c> (AC3). There is
/// no <c>retry_of</c> column — the retry chain IS the pair <c>(execution_key, attempt_no)</c>, unique per run by
/// <c>case_attempt_run_execution_attempt_key</c> — and rows are ordered <c>(execution_key, attempt_no)</c> ascending,
/// so one execution's attempts are adjacent and an <c>attempt_no</c> of 2 or more is a retry of the previous attempt of
/// the same <c>execution_key</c>.
/// </para>
/// </summary>
/// <param name="AttemptId">The stable key of one attempt row, <c>case_attempt.id</c>.</param>
/// <param name="ExecutionKey">
/// <c>case_attempt.execution_key</c>: the planned execution (case revision + variant + target env), a lowercase
/// sha256 hex.
/// </param>
/// <param name="CaseId">The case the attempt ran, <c>case_attempt.case_id</c>.</param>
/// <param name="AttemptNo">1-based within the execution; 2 or more is a retry of the previous attempt.</param>
/// <param name="RawStatus"><c>case_attempt.raw_status</c> exactly as the producer sent it (0..64 characters).</param>
/// <param name="NormalizedStatus"><c>case_attempt.normalized_status</c>, one of the 00 §8.5 values.</param>
/// <param name="StartedAt"><c>case_attempt.started_at</c>; null when the producer sent no start.</param>
/// <param name="FinishedAt"><c>case_attempt.finished_at</c>; null when the producer sent no finish.</param>
/// <param name="DurationMs"><c>case_attempt.duration_ms</c>; null when unrecorded.</param>
/// <param name="ArtifactCount">
/// How many artifacts the attempt has: the run's <c>artifact</c> rows (0028) matching <c>(execution_key,
/// attempt_no)</c> in state <c>ready</c>. A rejected, deleted or still-staged artifact is not counted.
/// </param>
/// <param name="HealingCandidate">
/// The healing candidate attached to the attempt, or null — which is every row today: no candidate row exists until
/// the 08.3.1 candidate model (shadow rows with 08.3.3). Never a fabricated marker.
/// </param>
public sealed record RunAttemptSummary(
    Guid AttemptId,
    string ExecutionKey,
    Guid CaseId,
    int AttemptNo,
    string RawStatus,
    string NormalizedStatus,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int? DurationMs,
    int ArtifactCount,
    RunHealingCandidate? HealingCandidate);

/// <summary>
/// The candidate a healed attempt points at (08.3.1), a shape the contract reserves (F52, S143) so the attempts read
/// does not need a second change when the candidate model lands; it is always <c>null</c> on the wire today.
/// </summary>
/// <param name="CandidateHash">The candidate's <c>sha256:</c> hash (00 §7, RFC 8785).</param>
/// <param name="Mode"><c>shadow</c> for a V1 shadow-only candidate (08.3.3), <c>official</c> once an approval bound it (08.6.1).</param>
public sealed record RunHealingCandidate(string CandidateHash, string Mode);

/// <summary>
/// Cursor page of <c>GET /v1/runs/{r}/attempts</c> (F52, S143; the 00 §9.1 envelope), keyset-ordered on
/// <c>(execution_key, attempt_no)</c> ascending.
/// </summary>
/// <param name="Items">The run's attempts in <c>(execution_key, attempt_no)</c> ascending order.</param>
/// <param name="NextCursor">Opaque cursor, or null on the last page.</param>
public sealed record RunAttemptPage(IReadOnlyList<RunAttemptSummary> Items, string? NextCursor);
