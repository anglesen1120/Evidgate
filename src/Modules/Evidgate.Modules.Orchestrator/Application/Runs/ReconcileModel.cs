namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// The 00 §8.5 <c>missing[]</c> codes the reconciler names. The closed list of seven is owned by the migration
/// <c>0049</c> CHECK, and every constant here is one of its literals, verbatim: the event-evidence codes of
/// 05.3.2-T2.2 (<c>missing_shard</c>, <c>count_mismatch</c>, <c>unknown_status</c>), the event-derived readiness codes
/// of 05.3.2-T2.3 (<c>setup_error</c>, <c>stale_epoch</c>) and <c>artifact_missing</c>, which the freeze result of
/// 05.3.2-T2.4 contributes. None of them is ever renamed.
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
    /// In a closed shard: at least one stored event arrived with <c>stale = true</c> (its epoch is older than the
    /// run's current one), so the evidence cannot be trusted as complete even when the counts agree (05.3.2-T2.3).
    /// </summary>
    public const string StaleEpoch = "stale_epoch";
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
/// What the acknowledged <c>event</c> rows of one producer say, computed in one grouped statement (no per-event
/// round trip). Every stored event counts as acknowledged, whatever its <c>late</c>/<c>after_cancel</c>/<c>stale</c>
/// flag, and the flagged ones are counted beside that total so a stale or blocked event is reported instead of hidden.
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
/// <param name="StaleCount">
/// Stored events with <c>stale = true</c>. They still count as acknowledged (a producer declares the total it sent),
/// so a stale event can fill a sequence slot and the count bounds can agree; this counter keeps that visible and a
/// closed shard reports <see cref="ReconcileMissingCodes.StaleEpoch"/> (COORDINATOR-DECISION S119, Q2 risk closed).
/// </param>
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
    long StaleCount,
    long BlockedCount);

/// <summary>One reason the evidence is incomplete.</summary>
/// <param name="Code">A <see cref="ReconcileMissingCodes"/> value (00 §8.5).</param>
/// <param name="ProducerId">The shard concerned.</param>
/// <param name="Detail">A short machine-readable detail (numbers only, never a payload).</param>
internal sealed record ReconcileMissing(string Code, string? ProducerId, string Detail);

/// <summary>
/// The event-evidence half of the reconcile result. 05.3.2-T2.2 fills the declared-versus-observed reasons, T2.3 adds
/// the <c>setup_error</c>/<c>stale_epoch</c> readiness reasons and T2.4 turns the whole, plus the freeze result, into a
/// frozen <c>evidence_revision</c>; this type only reports and changes nothing.
/// </summary>
/// <param name="Missing">Reasons in a stable order (producer id, then code); empty when every shard matches.</param>
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
    /// <summary>True when the event evidence shows no gap (never a completeness verdict: T2.4 decides that).</summary>
    public bool EventsMatch => Missing.Count == 0;
}

/// <summary>Why a reconcile attempt did or did not compare.</summary>
internal enum ReconcileDisposition
{
    /// <summary>The run row does not exist in this tenant.</summary>
    RunNotFound,

    /// <summary>The run is not <c>collecting</c> (terminal, canceled, or not yet finalized): nothing is compared, nothing changes.</summary>
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

    /// <summary>The comparison ran; <see cref="ReconcileOutcome.Comparison"/> holds the result.</summary>
    Compared,
}

/// <summary>The result of one reconcile attempt; the run state is never changed by T2.1/T2.2.</summary>
/// <param name="Disposition">What happened.</param>
/// <param name="Comparison">
/// Set when <paramref name="Disposition"/> is <see cref="ReconcileDisposition.Compared"/> — and also for
/// <see cref="ReconcileDisposition.Pending"/>, whose comparison is the evidence a finished attempt would have decided on
/// (its counts are the attempt's log line). A pending attempt finishes nothing: only <c>Compared</c> reaches the finisher.
/// </param>
internal sealed record ReconcileOutcome(ReconcileDisposition Disposition, ReconcileComparison? Comparison);

/// <summary>Runs one reconcile attempt for a run inside the caller's tenant transaction.</summary>
internal interface IRunReconciler
{
    /// <summary>
    /// Locks the run row <c>FOR NO KEY UPDATE</c>, lists the planned shards, reads the observed events per producer in
    /// one statement and compares. Deterministic for the same rows and the same clock reading: the comparison depends on
    /// the rows alone, and the disposition additionally on the deadline the lock statement evaluated, so the same rows
    /// answer <see cref="ReconcileDisposition.Pending"/> before <c>collection_deadline_at</c> and
    /// <see cref="ReconcileDisposition.Compared"/> after it. The caller decides nothing on <c>Pending</c>.
    /// </summary>
    /// <param name="runId">The run to reconcile.</param>
    /// <param name="cancellationToken">Cancelled on shutdown or lease loss.</param>
    ValueTask<ReconcileOutcome> ReconcileAsync(Guid runId, CancellationToken cancellationToken);
}
