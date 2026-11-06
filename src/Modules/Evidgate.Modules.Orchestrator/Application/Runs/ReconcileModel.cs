namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// The 00 §8.5 <c>missing[]</c> codes the reconciler names. The closed list is owned by the
/// <c>evidence_revision_missing_check</c> CHECK — <c>0049</c> declared it over seven codes and <c>0056</c> re-created it
/// with <c>missing_result</c> appended — and every constant here is one of its literals, verbatim: the codes of the
/// event-evidence comparison (<c>missing_shard</c>, <c>count_mismatch</c>, <c>unknown_status</c>), of the event-derived
/// readiness check (<c>setup_error</c>), of the plan's execution coverage
/// (<see cref="MissingResult"/>) and <c>artifact_missing</c>, which the freeze result contributes. None of them is ever
/// renamed.
/// <para>
/// <c>stale_epoch</c> stays in the CHECK but has no constant here: no writer sets <c>event.stale</c> — nothing stores a
/// producer's current epoch to compare against (no job or job-lease table, and no epoch column on the slot) — so the
/// reason could never fire and a code that cannot be produced is not a decision this module makes. The flag and the
/// reason return together with lease epochs, when an authoritative current epoch exists to compare at insert time.
/// </para>
/// </summary>
internal static class ReconcileMissingCodes
{
    /// <summary>A planned shard whose producer slot is not closed (it never finalized).</summary>
    public const string MissingShard = "missing_shard";

    /// <summary>
    /// A closed shard whose acknowledged events disagree with its declared <c>(last_sequence, count)</c>: a count other
    /// than the declared one, an event past the declared high-water mark, a highest stored sequence other than the
    /// declared last sequence, or a hole in <c>[min stored sequence, declared last sequence]</c>
    /// (<c>contracts/events/ordering.md</c> §3). One entry per shard however many of those bounds it breaks.
    /// </summary>
    public const string CountMismatch = "count_mismatch";

    /// <summary>In a closed shard: a <c>test.finished</c> with a null <c>normalized_status</c>, or a <c>test.started</c> attempt with no <c>test.finished</c>.</summary>
    public const string UnknownStatus = "unknown_status";

    /// <summary>
    /// A required artifact of the run is not <c>ready</c> (00 §8.4): an unmet requirement or an orphan of the freeze
    /// result (05.3.2-T2.4, which forwards <c>MissingReason.Code</c> verbatim).
    /// </summary>
    public const string ArtifactMissing = "artifact_missing";

    /// <summary>
    /// In a closed shard: a <c>test.finished</c> whose normalized status is <c>blocked</c> — a setup that failed, so
    /// the run cannot be complete (00 §8.3; 05.3.2-T2.3, T14).
    /// </summary>
    public const string SetupError = "setup_error";

    /// <summary>
    /// A planned execution of the run with no recorded <c>case_attempt</c>: no status was ever reported for it, so the
    /// run is incomplete however the producers' declarations agree. One entry per execution, keyed by its
    /// <c>execution_key</c>, plus one counted entry when the bounded coverage read returned only part of the plan.
    /// </summary>
    public const string MissingResult = "missing_result";
}

/// <summary>One planned shard as the run's producer slots define it (the shard plan, one row per slot).</summary>
/// <param name="ProducerId">The deterministic producer id of the slot.</param>
/// <param name="ShardIndex">1-based shard index.</param>
/// <param name="Closed">Whether <c>closed_at</c> is set.</param>
/// <param name="DeclaredLastSequence">The first close's declared high-water mark; null while the slot is open.</param>
/// <param name="DeclaredCount">The first close's declared event count; null while the slot is open.</param>
internal sealed record PlannedShard(
    string ProducerId,
    int ShardIndex,
    bool Closed,
    long? DeclaredLastSequence,
    int? DeclaredCount);

/// <summary>
/// One planned execution of the run that reported no status: a <c>planned_execution</c> row whose
/// <c>execution_key</c> has no <c>case_attempt</c>.
/// </summary>
/// <param name="ExecutionKey">The plan row's <c>execution_key</c> (<c>^[0-9a-f]{64}$</c>).</param>
/// <param name="ShardId">
/// The plan row's <c>shard_id</c> (<c>shard-N</c>), which is the producer id of the slot the execution's events arrive
/// on, so the reason it produces is attributed to the shard that should have reported it.
/// </param>
internal sealed record PlannedExecution(string ExecutionKey, string ShardId);

/// <summary>
/// The plan's executions that recorded no attempt, as one bounded read answers them. A run can plan tens of thousands of
/// executions, so the read stops at a cap and every row carries the true total; the comparer names the rows it read and
/// one counted reason for the rest, so the gap is never understated and the work stays bounded.
/// </summary>
/// <param name="Uncovered">The uncovered executions the read answered, never more than the read's cap.</param>
/// <param name="Total">How many planned executions of the run recorded no attempt, however many rows were read.</param>
internal sealed record ExecutionCoverage(IReadOnlyList<PlannedExecution> Uncovered, long Total)
{
    /// <summary>A plan every execution of which recorded an attempt.</summary>
    public static ExecutionCoverage Complete { get; } = new([], 0);
}

/// <summary>
/// What the acknowledged <c>event</c> rows of one producer say, computed in one grouped statement (no per-event
/// round trip). Every stored event counts as acknowledged, whatever its <c>late</c>/<c>after_cancel</c>/<c>stale</c>
/// flag, and a blocked finish is counted beside that total so it is reported instead of hidden.
/// </summary>
/// <param name="ProducerId">The producer id.</param>
/// <param name="CountUpToDeclared">Events with <c>sequence &lt;= declared_last_sequence</c> (0 for an open shard).</param>
/// <param name="CountBeyondDeclared">Events with <c>sequence &gt; declared_last_sequence</c> (all events for an open shard).</param>
/// <param name="MinSequence">
/// Lowest stored sequence; null when the producer has no event. Together with the declared high-water mark it is the
/// base of the gap rule: the sequences in <c>[MinSequence, declared_last_sequence]</c> must all be stored
/// (<c>contracts/events/ordering.md</c> §3), and a hole below it is an unwritten prefix that is never reported.
/// </param>
/// <param name="MaxSequence">
/// Highest stored sequence; null when the producer has no event. A closed shard that declared at least one event must
/// end exactly on its declared high-water mark, so this is compared too (a count alone cannot see where it ends).
/// </param>
/// <param name="UnknownStatusCount"><c>test.finished</c> events with a null <c>normalized_status</c>.</param>
/// <param name="UnfinishedStartedCount">Distinct <c>(execution_key, attempt_no)</c> of the producer that have a <c>test.started</c> event and no <c>test.finished</c> event from any producer of the run: a status that was never reported (COORDINATOR-DECISION S119, case b of <c>unknown_status</c>).</param>
/// <param name="BlockedCount">
/// <c>test.finished</c> events whose normalized status is <c>blocked</c>: a failed setup marks its dependants blocked
/// (00 §8.3), so a closed shard reports <see cref="ReconcileMissingCodes.SetupError"/>.
/// </param>
internal sealed record ObservedShard(
    string ProducerId,
    long CountUpToDeclared,
    long CountBeyondDeclared,
    long? MinSequence,
    long? MaxSequence,
    long UnknownStatusCount,
    long UnfinishedStartedCount,
    long BlockedCount);

/// <summary>One reason the evidence is incomplete.</summary>
/// <param name="Code">A <see cref="ReconcileMissingCodes"/> value (00 §8.5).</param>
/// <param name="ProducerId">The shard concerned: a producer slot's id, or a planned execution's <c>shard_id</c>.</param>
/// <param name="Detail">
/// A short machine-readable detail: numbers and the identity of the row concerned (a shard index, an execution key),
/// never an event value and never a payload.
/// </param>
internal sealed record ReconcileMissing(string Code, string? ProducerId, string Detail);

/// <summary>
/// The comparison half of the reconcile result: the declared-versus-observed reasons of the event evidence, the
/// readiness reasons derived from it, and the one reason per planned execution that never reported a status. The freeze
/// of the artifact set is added on top by the finisher, which turns the whole into a frozen <c>evidence_revision</c>;
/// this type only reports and changes nothing.
/// </summary>
/// <param name="Missing">
/// Reasons in a stable order (producer id, then code, then detail, all ordinal); empty when every shard matches and
/// every planned execution reported a status.
/// </param>
/// <param name="ShardsPlanned">Planned shards (producer slots of the run).</param>
/// <param name="ShardsClosed">Planned shards that finalized.</param>
/// <param name="EventWatermark">
/// Every stored event the comparison read: the sum of <see cref="ObservedShard.CountUpToDeclared"/> and
/// <see cref="ObservedShard.CountBeyondDeclared"/> over the observed rows, one per producer of the run. Every observed
/// producer is one the plan names — the aggregate starts from the run's slots and <c>event_slot_fk</c> ties every event
/// to one of them, so an outside-plan producer is not merely ignored but cannot exist — and a repeated producer row is
/// ignored like it is for the reasons, so it is never added twice. Events are immutable, so the value only grows: it is
/// the <c>evidence_revision.event_watermark</c> T2.4 persists (COORDINATOR-DECISION S123, D-S123-Q2).
/// </param>
internal sealed record ReconcileComparison(
    IReadOnlyList<ReconcileMissing> Missing,
    int ShardsPlanned,
    int ShardsClosed,
    long EventWatermark)
{
    /// <summary>
    /// True when the comparison named no reason at all — the event evidence agrees and every planned execution reported a
    /// status — which is the first half of a completeness verdict; the finisher adds the freeze result and the shard
    /// counts before it decides one.
    /// </summary>
    public bool EventsMatch => Missing.Count == 0;
}

/// <summary>Why a reconcile attempt did or did not compare.</summary>
internal enum ReconcileDisposition
{
    /// <summary>The run row does not exist in this tenant.</summary>
    RunNotFound,

    /// <summary>
    /// The run cannot be compared by this trigger: it is terminal (<c>complete</c>, or <c>canceled</c> for a trigger that
    /// carries no reprocess evidence), or <c>created</c>,
    /// or <c>queued</c>/<c>running</c> with its collection deadline still ahead (a verdict there would close a run that
    /// is still starting), or finished <c>incomplete</c> while this trigger carries no late evidence or the run's late
    /// window is closed. Nothing is compared, nothing changes.
    /// </summary>
    NotCollectable,

    /// <summary>
    /// The run is <c>collecting</c>, the comparison ran, and at least one planned shard is still open while the run's
    /// <c>collection_deadline_at</c> has not passed: the attempt must <b>wait</b> instead of deciding, so nothing is
    /// frozen, no <c>evidence_revision</c> is appended, the pointer does not move and the run stays <c>collecting</c>
    /// (COORDINATOR-DECISION S123, F1). A finalize that closes the last open shard enqueues the next trigger, and the
    /// deadline (05.3.2-T3.1) forces the decision when no shard ever closes.
    /// <para>
    /// The deadline is evaluated by the run-lock statement (<c>r.collection_deadline_at &lt;= now()</c>, the database
    /// clock) so no application clock takes part; an open shard with a deadline still ahead of <c>now()</c> is exactly
    /// this disposition, and an open shard past the deadline finishes as <c>incomplete</c> with <c>missing_shard</c>.
    /// </para>
    /// </summary>
    Pending,

    /// <summary>The comparison ran on a <c>collecting</c> run; <see cref="ReconcileOutcome.Comparison"/> holds the result.</summary>
    Compared,

    /// <summary>
    /// The comparison ran on a run that already finished as <c>incomplete</c>: late evidence arrived inside its window,
    /// so the attempt appends revision N+1 and moves the run's pointer to it, and the run state does not move. A
    /// finished run has no forward edge left, and a revision, not a state, is what carries the new completeness.
    /// </summary>
    ComparedLate,

    /// <summary>
    /// The comparison ran on a run that had already been <c>canceled</c>, for a reprocess its admission accepted: the
    /// re-reading of the run's own stored report wrote new rows after cancel, so the attempt appends revision N+1 and
    /// moves the run's pointer to it, and the run stays <c>canceled</c> — a canceled run is terminal and never becomes
    /// complete, whatever its evidence now says (00 §8.1). Like the late path, the revision is the only place
    /// this evidence ever counts. A <c>complete</c> run answers the same way, and stays <c>complete</c>.
    /// </summary>
    ComparedReprocess,

    /// <summary>
    /// A reprocess marker named a run that is still live (<c>created</c>, <c>queued</c>, <c>running</c> or
    /// <c>collecting</c>) and holds no <c>evidence_revision</c> yet: nothing is compared, nothing is frozen and no state
    /// moves, because the run's collection settlement freezes this evidence into its first revision. Comparing here would
    /// either decide a run that is still collecting or append a revision ahead of its baseline.
    /// </summary>
    ReprocessAwaitsCollection,

    /// <summary>
    /// The comparison ran on a <c>queued</c> or <c>running</c> run whose <c>collection_deadline_at</c> has passed: its
    /// producers never started or never finalized, so no finalize will ever enqueue the reconcile that decides such a
    /// run, and the deadline is the run's promise to the caller. The attempt closes it <c>incomplete</c> with the
    /// reasons the comparison named — never <c>complete</c>, because missing evidence never reads as a pass.
    /// <see cref="ReconcileOutcome.Comparison"/> holds the result; the finisher settles it as
    /// <see cref="ReconcileSettlement.CloseOverdue"/>, which moves the run through the declared
    /// <c>queued|running → incomplete</c> edge.
    /// </summary>
    ComparedOverdue,
}

/// <summary>The result of one reconcile attempt; the run state is never changed by T2.1/T2.2.</summary>
/// <param name="Disposition">What happened.</param>
/// <param name="Comparison">
/// Set when <paramref name="Disposition"/> is <see cref="ReconcileDisposition.Compared"/>,
/// <see cref="ReconcileDisposition.ComparedLate"/>, <see cref="ReconcileDisposition.ComparedReprocess"/> or
/// <see cref="ReconcileDisposition.ComparedOverdue"/> — and also
/// for <see cref="ReconcileDisposition.Pending"/>, whose comparison is the event evidence alone (its counts are the
/// attempt's log line): a waiting attempt reads no plan coverage, because coverage cannot turn a wait into a verdict.
/// A pending attempt finishes nothing: only the compared dispositions reach the finisher.
/// </param>
internal sealed record ReconcileOutcome(ReconcileDisposition Disposition, ReconcileComparison? Comparison);

/// <summary>Runs one reconcile attempt for a run inside the caller's tenant transaction.</summary>
internal interface IRunReconciler
{
    /// <summary>
    /// Locks the run row <c>FOR NO KEY UPDATE</c>, lists the planned shards, reads the observed events per producer in
    /// one statement and compares; an attempt that decides reads the plan's executions with no recorded attempt in one
    /// more statement first. A run that finished as <c>incomplete</c> is compared too — late evidence is only counted
    /// through a revision — but only for an attempt that <paramref name="lateTrigger"/> (or
    /// <paramref name="reprocessTrigger"/>) marks as carrying admitted evidence; that attempt answers
    /// <see cref="ReconcileDisposition.ComparedLate"/> and never waits, because its verdict is written and no finalize can
    /// close a shard any more. The window that evidence was admitted in is the ingest route's decision, already taken
    /// when it accepted the batch, so a late trigger is honoured whatever the clock says when the queue reaches it. A
    /// <c>canceled</c> run is compared for a trigger that <paramref name="reprocessTrigger"/> marks, and answers
    /// <see cref="ReconcileDisposition.ComparedReprocess"/>: a reprocess re-reads bytes the run already holds, and its new
    /// rows reach a revision like late evidence does, while the run stays terminal. A <c>queued</c> or <c>running</c> run
    /// is compared once
    /// <c>collection_deadline_at</c> has passed (<see cref="ReconcileDisposition.ComparedOverdue"/>): its producers never
    /// started or never finalized, so no finalize will enqueue the reconcile that closes it, and the deadline settles it
    /// <c>incomplete</c>; before its deadline the same run stays <see cref="ReconcileDisposition.NotCollectable"/> —
    /// it is not collecting, so there is no wait to hold. Every other trigger on a finished run answers
    /// <see cref="ReconcileDisposition.NotCollectable"/>: a redelivered or replayed finalize/deadline trigger carries no
    /// new evidence, so it must not append a revision. Deterministic for the same rows and the same clock reading: the
    /// comparison depends on the rows alone, and the disposition additionally on the deadline the lock statement
    /// evaluated, so the same rows answer <see cref="ReconcileDisposition.Pending"/> before
    /// <c>collection_deadline_at</c> and <see cref="ReconcileDisposition.Compared"/> after it. The caller decides nothing
    /// on <c>Pending</c>.
    /// </summary>
    /// <param name="runId">The run to reconcile.</param>
    /// <param name="lateTrigger">
    /// Whether this trigger was enqueued for late evidence the ingest route accepted (only that producer sets it). It is
    /// a precondition, not a verdict: the run must also still be <c>incomplete</c>.
    /// </param>
    /// <param name="reprocessTrigger">
    /// Whether this trigger was enqueued for a reprocess the run's admission accepted (only the reprocess task sets it,
    /// on every trigger it writes). A precondition, not a verdict: it moves an <c>incomplete</c> run onto the same path as
    /// <paramref name="lateTrigger"/>, and a <c>complete</c> or <c>canceled</c> run answers
    /// <see cref="ReconcileDisposition.ComparedReprocess"/> — a revision beside a state that never moves. On a live run it
    /// answers <see cref="ReconcileDisposition.ComparedReprocess"/> when the run already holds a revision and
    /// <see cref="ReconcileDisposition.ReprocessAwaitsCollection"/> when it holds none, and it never takes the deadline's
    /// forced close.
    /// </param>
    /// <param name="cancellationToken">Cancelled on shutdown or lease loss.</param>
    ValueTask<ReconcileOutcome> ReconcileAsync(
        Guid runId,
        bool lateTrigger,
        bool reprocessTrigger,
        CancellationToken cancellationToken);
}
