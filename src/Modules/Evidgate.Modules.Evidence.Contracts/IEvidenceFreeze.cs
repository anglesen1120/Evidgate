namespace Evidgate.Modules.Evidence.Contracts;

/// <summary>
/// The one call 05's reconciler (05.3.2-T2) makes into Evidence to freeze a run's evidence (06.5.1, FR-06-1, FR-06-14,
/// rulings T3 and T17). It is a cross-module service call, <b>not</b> a Mediator query or command: it runs
/// <b>inside the caller's ambient tenant transaction</b> and never opens, commits or rolls back a transaction, so the
/// caller's rollback undoes the <c>attempt_id</c> resolution it performed (00 §5.5 C3). It makes no network call (P5) and
/// writes no outbox row.
/// <para>
/// Reading is one statement and writing is at most one statement, whatever the number of artifacts or requirements
/// (00 §5.7 P2/P3): the <c>ready</c> artifacts of the run are left-joined to <c>case_attempt</c> by
/// <c>(execution_key, attempt_no)</c>, and the pairs that resolved for the first time are persisted by one idempotent
/// <c>UPDATE … WHERE attempt_id IS NULL</c>. Only <c>ready</c> artifacts count (00 §8.4).
/// </para>
/// <para>
/// <b>No-mutation contract (06.5.1 §6).</b> A <c>ready</c> artifact's <c>object_key</c> and <c>checksum</c> never change, and its
/// <c>attempt_id</c> is only ever filled from <c>NULL</c>: a second call over the same state returns an equal
/// <see cref="FreezeResult"/> and issues no <c>UPDATE</c>, and a call made after a late artifact turned <c>ready</c> resolves
/// only that artifact's row. 05 alone persists the <c>evidence_revision</c>; nothing here writes it, and a frozen revision is
/// never mutated by a later call.
/// </para>
/// </summary>
public interface IEvidenceFreeze
{
    /// <summary>
    /// Freezes the evidence of <see cref="FreezeRequest.RunId"/>: lists its <c>ready</c> artifacts with
    /// <c>(execution_key, attempt_no)</c> resolved to <c>case_attempt</c>, reports the pairs that do not resolve as
    /// <see cref="FreezeResult.Orphans"/> (ruling T17: never dropped, never resolved to a wrong attempt, <c>attempt_id</c> stays
    /// null), compares the caller's <see cref="FreezeRequest.Required"/> set with what is <c>ready</c> and returns the gaps in
    /// <see cref="FreezeResult.Missing"/>, plus the deterministic <see cref="FreezeResult.ArtifactSetHash"/>.
    /// <para>
    /// A run the tenant scope cannot see (unknown id, another org, another project) yields no artifacts, so every requirement is
    /// reported missing: missing evidence never reads as complete.
    /// </para>
    /// </summary>
    /// <param name="request">The run and the requirement set the caller resolved from the manifest and policy.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> or an element of its requirements is null.</exception>
    /// <exception cref="ArgumentException">
    /// The request is malformed: an empty run id, more than <see cref="FreezeRequest.MaxRequired"/> requirements, or a
    /// requirement with a bad execution key, kind, reason code or attempt number (a caller bug, never a data condition).
    /// </exception>
    /// <exception cref="InvalidOperationException">No tenant scope is open, or a stored <c>attempt_id</c> disagrees with the
    /// attempt its <c>(execution_key, attempt_no)</c> resolves to (an integrity violation; nothing is rewritten).</exception>
    Task<FreezeResult> FreezeAsync(FreezeRequest request, CancellationToken cancellationToken);
}

/// <summary>What the caller asks to freeze.</summary>
/// <param name="RunId">The run whose evidence is frozen; it must be visible to the caller's tenant scope.</param>
/// <param name="Required">
/// The artifacts the caller requires, resolved by 05/03 from the manifest and policy: 06 compares against this set and never
/// decides which artifacts a case needs (06.5.1 §1 non-goals). Empty means nothing is required.
/// </param>
public sealed record FreezeRequest(Guid RunId, IReadOnlyList<RequiredArtifactSpec> Required)
{
    /// <summary>The largest requirement set one call accepts; the comparison is in memory over the single read.</summary>
    public const int MaxRequired = 10_000;
}

/// <summary>One required artifact: a <c>ready</c> artifact of <see cref="Kind"/> must exist for the execution.</summary>
/// <param name="ExecutionKey">The planned execution's key, sha256 hex (00 §7): <c>^[0-9a-f]{64}$</c>.</param>
/// <param name="Kind">The artifact kind (00 §7); one of the closed list the <c>artifact</c> table accepts.</param>
/// <param name="ReasonCode">
/// The 00 §8.5 <c>missing[]</c> code the caller wants reported when the requirement is not met; forwarded verbatim in
/// <see cref="MissingReason.Code"/>. Shape <c>^[a-z][a-z0-9_]{0,63}$</c>; the vocabulary itself is 00 §8.5's, 06 does not invent codes.
/// </param>
/// <param name="AttemptNo">
/// The attempt the artifact must belong to, or null when an artifact of any recorded attempt of the execution satisfies the
/// requirement. When given it is <c>&gt;= 1</c>.
/// </param>
public sealed record RequiredArtifactSpec(string ExecutionKey, string Kind, string ReasonCode, int? AttemptNo = null);

/// <summary>The frozen evidence of one run (06.5.1 §3).</summary>
/// <param name="Ready">
/// The <c>ready</c> artifacts whose <c>(execution_key, attempt_no)</c> resolved to a <c>case_attempt</c>, ordered by
/// <c>(execution_key, attempt_no, kind, artifact id)</c> so the artifacts of one attempt are adjacent (the grouping 06.5.1 AC1 names).
/// </param>
/// <param name="Orphans">
/// The <c>ready</c> artifacts whose pair has no <c>case_attempt</c> (ruling T17), same order. Each contributes one
/// <see cref="MissingReason"/> with code <see cref="FreezeCodes.ArtifactMissing"/> to <see cref="Missing"/>.
/// </param>
/// <param name="Missing">
/// The gaps: one <see cref="MissingSource.Orphan"/> reason per orphan, then one <see cref="MissingSource.Requirement"/> reason per
/// unmet requirement. Deterministically ordered; empty means the evidence is complete as far as 06 can tell.
/// </param>
/// <param name="ArtifactSetHash">
/// <c>sha256:&lt;hex&gt;</c> (00 §7) of the RFC 8785 canonical JSON of the ready set — resolved and orphaned artifacts together — as an
/// array of <c>{artifact_id, object_key, checksum}</c> ordered by <c>(execution_key, attempt_no, kind, id)</c>. It is independent of
/// database row order and excludes <c>attempt_id</c>, so resolving an orphan later does not change it.
/// </param>
public sealed record FreezeResult(
    IReadOnlyList<FrozenArtifact> Ready,
    IReadOnlyList<OrphanArtifact> Orphans,
    IReadOnlyList<MissingReason> Missing,
    string ArtifactSetHash);

/// <summary>A <c>ready</c> artifact bound to its <c>case_attempt</c>.</summary>
/// <param name="ArtifactId">The artifact id.</param>
/// <param name="ExecutionKey">The planned execution's key.</param>
/// <param name="AttemptNo">The attempt number, <c>&gt;= 1</c>.</param>
/// <param name="AttemptId">The <c>case_attempt</c> id, resolved server-side from <c>(execution_key, attempt_no)</c> and persisted.</param>
/// <param name="Kind">The artifact kind.</param>
/// <param name="ObjectKey">The server-chosen final object key.</param>
/// <param name="Checksum">The <c>sha256:&lt;hex&gt;</c> content address.</param>
/// <param name="Sensitivity">The artifact's sensitivity (<c>normal</c> or <c>sensitive</c>).</param>
public sealed record FrozenArtifact(
    Guid ArtifactId,
    string ExecutionKey,
    int AttemptNo,
    Guid AttemptId,
    string Kind,
    string ObjectKey,
    string Checksum,
    string Sensitivity);

/// <summary>A <c>ready</c> artifact whose <c>(execution_key, attempt_no)</c> has no <c>case_attempt</c>; its <c>attempt_id</c> stays null.</summary>
/// <param name="ArtifactId">The artifact id.</param>
/// <param name="ExecutionKey">The execution key the artifact named at upload.</param>
/// <param name="AttemptNo">The attempt number the artifact named at upload.</param>
/// <param name="Kind">The artifact kind.</param>
/// <param name="ObjectKey">The server-chosen final object key.</param>
/// <param name="Checksum">The <c>sha256:&lt;hex&gt;</c> content address.</param>
/// <param name="Sensitivity">The artifact's sensitivity.</param>
public sealed record OrphanArtifact(
    Guid ArtifactId,
    string ExecutionKey,
    int AttemptNo,
    string Kind,
    string ObjectKey,
    string Checksum,
    string Sensitivity);

/// <summary>Why a <see cref="MissingReason"/> exists: the two gap classes 06.5.1 AC2 and AC3 keep distinct.</summary>
public enum MissingSource
{
    /// <summary>A <c>ready</c> artifact with no <c>case_attempt</c> for its pair (ruling T17); code <see cref="FreezeCodes.ArtifactMissing"/>.</summary>
    Orphan = 0,

    /// <summary>A caller-required artifact with no <c>ready</c> artifact bound to a recorded attempt; code is the requirement's own.</summary>
    Requirement = 1,
}

/// <summary>One entry of <see cref="FreezeResult.Missing"/>, 05 maps <see cref="Code"/> straight into <c>evidence_revision.missing[]</c>.</summary>
/// <param name="Code">The 00 §8.5 code: <see cref="FreezeCodes.ArtifactMissing"/> for an orphan, the requirement's <see cref="RequiredArtifactSpec.ReasonCode"/> otherwise.</param>
/// <param name="Source">Whether an orphan or an unmet requirement produced the entry.</param>
/// <param name="ExecutionKey">The execution the gap belongs to.</param>
/// <param name="Kind">The artifact kind concerned.</param>
/// <param name="AttemptNo">The attempt number concerned; null only for a requirement that accepts any attempt.</param>
/// <param name="ArtifactId">The orphan's artifact id; null for a requirement gap.</param>
public sealed record MissingReason(
    string Code,
    MissingSource Source,
    string ExecutionKey,
    string Kind,
    int? AttemptNo,
    Guid? ArtifactId);

/// <summary>The only <c>missing[]</c> code 06 originates (ruling T17); every other code is forwarded from the caller's requirement.</summary>
public static class FreezeCodes
{
    /// <summary>00 §8.5 <c>artifact_missing</c>.</summary>
    public const string ArtifactMissing = "artifact_missing";
}
