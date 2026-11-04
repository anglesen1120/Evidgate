namespace Evidgate.Modules.Policy.Contracts;

/// <summary>
/// The policy surface other modules call in-process (00 §5.5). Every member runs inside the caller's tenant transaction
/// (the caller opened it through <c>ITenantDb</c>); none opens its own.
/// </summary>
public interface IPolicyContracts
{
    /// <summary>
    /// Resolves the policy revision a new run pins (07.1.1-T4.2, FR-07-2). One lock-free read of the project's
    /// activation pointer and the revision it names (finding F4): the read takes no row lock, so a run that overlaps an
    /// activation and pins the revision that was active at its own commit is a legitimate READ COMMITTED ordering, and a
    /// run create never holds the project row the activation locks <c>FOR NO KEY UPDATE</c>. Returns null when the
    /// project has no active revision, and also when the project is missing or soft-deleted (the caller has already
    /// refused those).
    /// </summary>
    /// <param name="projectId">The project the run is created in.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<ActivePolicy?> GetActivePolicyAsync(Guid projectId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the artifact kinds one policy revision requires a run to produce: the body's
    /// <c>required_artifacts</c> array (<c>contracts/schemas/policy-revision.v1.json</c>: kind names matching
    /// <c>^[a-z][a-z0-9_]{0,63}$</c>, at most 100 entries). One lock-free, read-only statement over the ambient
    /// tenant scope: the revision is matched by its own <c>org_id</c> on top of RLS, takes no row lock and writes
    /// nothing. The kinds come back de-duplicated and in ordinal order, so a caller can compare them as sets
    /// without sorting again. An unknown or invisible revision id answers an empty list, and so does a revision
    /// whose body carries no <c>required_artifacts</c> (or a value that is not an array): "no requirement" is not
    /// an error here, exactly like "no active policy" on <see cref="GetActivePolicyAsync"/>.
    /// </summary>
    /// <param name="policyRevisionId">The revision whose required artifact kinds the caller needs.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyList<string>> GetRequiredArtifactKindsAsync(Guid policyRevisionId, CancellationToken cancellationToken);

    /// <summary>
    /// Answers which of a run's evidence revisions already have a stored <c>live</c> evaluation: one read-only
    /// statement over the ambient tenant scope, matched on the caller's org on top of RLS, taking no lock and writing
    /// nothing. Only <c>mode = 'live'</c> rows are returned — a <c>shadow</c> row is a second opinion about the same
    /// evidence and is never the verdict a run reports, so it is never named here. A revision with no stored live
    /// evaluation is simply absent from the answer, the same "nothing there" shape as the other members.
    /// </summary>
    /// <param name="runId">The run whose revisions the caller is reading; a foreign or unknown run answers nothing.</param>
    /// <param name="evidenceRevisions">The <c>evidence_revision.revision_no</c> values to look up, at most one page.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyList<LiveEvaluation>> GetLiveEvaluationsAsync(
        Guid runId,
        IReadOnlyList<int> evidenceRevisions,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the newest stored verdict of each named run — the highest <c>evidence_revision</c> <c>live</c> row — in
    /// one statement over the ambient tenant scope, never one statement per run. A <c>shadow</c> row is a second
    /// opinion about the same evidence and is never the verdict. A run with no live evaluation is absent from the
    /// answer, which the caller reads as "not evaluated yet"; empty input performs no query.
    /// </summary>
    /// <remarks>
    /// The caller must pass ids it has already scoped to the project and to the set its own caller may see: the
    /// tenant predicate is the only boundary this read applies, so an id from another project of the same org —
    /// or one its caller was not authorized to name — would come back as a verdict. Policy cannot re-derive either
    /// boundary, and must not guess it.
    /// </remarks>
    /// <param name="runIds">At most 200 run ids, one page of a list read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyList<RunLiveDecision>> GetLatestLiveDecisionsAsync(
        IReadOnlyList<Guid> runIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// The <c>evaluation</c> row a frozen evidence revision was judged with (<c>0059</c>), as a reader of the revisions
/// needs it: the revision it evaluated and the stored verdict's id.
/// </summary>
/// <param name="EvidenceRevision">The <c>evaluation.evidence_revision</c> this verdict belongs to.</param>
/// <param name="EvaluationId">The <c>evaluation.id</c> of that revision's <c>live</c> row.</param>
public sealed record LiveEvaluation(int EvidenceRevision, Guid EvaluationId);

/// <summary>The newest stored live evaluation of a run, never a shadow verdict.</summary>
/// <param name="RunId">The evaluated run.</param>
/// <param name="Decision">PASS, FAIL or INCONCLUSIVE.</param>
/// <param name="EvidenceRevision">The evaluated evidence revision.</param>
/// <param name="EvaluationId">The stored evaluation id.</param>
public sealed record RunLiveDecision(Guid RunId, string Decision, int EvidenceRevision, Guid EvaluationId);

/// <summary>
/// The active policy revision of a project, as run creation needs it (05.2.1 plan §10 T1 hand-over; ruling T93).
/// </summary>
/// <param name="PolicyRevisionId">The revision to store on <c>run.policy_revision_id</c>.</param>
/// <param name="Hash">The revision's content address, <c>sha256:&lt;64 lower-case hex&gt;</c> (00 §7).</param>
/// <param name="CollectionTimeoutMinutes">The body's <c>collection_timeout_minutes</c>, or null when the body does not set it.</param>
/// <param name="LateWindowMinutes">The body's <c>late_window_minutes</c>, or null when the body does not set it.</param>
public sealed record ActivePolicy(Guid PolicyRevisionId, string Hash, int? CollectionTimeoutMinutes, int? LateWindowMinutes);
