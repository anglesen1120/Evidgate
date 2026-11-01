namespace Evidgate.Modules.Evidence.Contracts;

/// <summary>
/// The frozen evidence of one run as an evaluator reads it: the revision row and the attempt statuses that revision's
/// freeze stamp admits. A cross-module service call, <b>not</b> a Mediator query: both members run inside the caller's
/// ambient tenant transaction, never open, commit or roll back one, and issue one statement each.
/// <para>
/// The attempt read is the set-based form of "the latest attempt this revision saw": for every execution key of the run
/// that has an attempt created at or before the revision's own <c>created_at</c>, the status of the attempt with the
/// highest <c>attempt_no</c>. An execution whose attempts all postdate the freeze has no entry, which is how the caller
/// tells "no evidence" from "evidence that arrived late".
/// </para>
/// </summary>
public interface IFrozenEvidence
{
    /// <summary>
    /// The run's evidence revision <paramref name="revisionNo"/>, or null when this tenant has no such revision for that
    /// run. <see cref="FrozenEvidenceRevision.Completeness"/> is the reconciler's own answer and is never inferred from
    /// <see cref="FrozenEvidenceRevision.Missing"/>.
    /// </summary>
    /// <param name="orgId">The caller's trusted tenant id, so the read is tenant-exact beyond row level security.</param>
    /// <param name="runId">The run the revision belongs to.</param>
    /// <param name="revisionNo">The revision number the caller pinned.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<FrozenEvidenceRevision?> GetRevisionAsync(Guid orgId, Guid runId, int revisionNo, CancellationToken cancellationToken);

    /// <summary>
    /// The status of the latest attempt the revision admits, per execution key, ordered by
    /// <see cref="FrozenAttemptStatus.ExecutionKey"/> ordinally. Keys whose every attempt postdates the revision's
    /// <c>created_at</c> are absent, never reported with a guessed status.
    /// </summary>
    /// <param name="orgId">The caller's trusted tenant id, so the read is tenant-exact beyond row level security.</param>
    /// <param name="runId">The run whose attempts are read.</param>
    /// <param name="revisionNo">The revision whose freeze stamp bounds the attempts; it must belong to the tenant's run.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyList<FrozenAttemptStatus>> GetLatestAttemptStatusesAsync(Guid orgId, Guid runId, int revisionNo, CancellationToken cancellationToken);
}

/// <summary>The frozen revision an evaluation judges: its number, the reconciler's completeness and gaps, and its stamp.</summary>
/// <param name="RevisionNo">The <c>revision_no</c>, <c>&gt;= 1</c>.</param>
/// <param name="Completeness"><c>complete</c> or <c>incomplete</c>, as the reconciler decided.</param>
/// <param name="Missing">The stored <c>missing[]</c> reason codes; empty when the reconciler stored none.</param>
/// <param name="ManifestHash">The manifest the revision froze against.</param>
/// <param name="CreatedAt">The freeze stamp: the start of the decision latency and the bound on admissible attempts.</param>
public sealed record FrozenEvidenceRevision(
    int RevisionNo,
    string Completeness,
    IReadOnlyList<string> Missing,
    string ManifestHash,
    DateTimeOffset CreatedAt);

/// <summary>One execution's latest admitted attempt: the normalised status the evaluator reads.</summary>
/// <param name="ExecutionKey">The planned execution's key, the 64 lowercase hex the run planned.</param>
/// <param name="Status"><c>passed</c>, <c>failed</c>, <c>skipped</c>, <c>timed_out</c>, <c>error</c> or <c>blocked</c>.</param>
public sealed record FrozenAttemptStatus(string ExecutionKey, string Status);
