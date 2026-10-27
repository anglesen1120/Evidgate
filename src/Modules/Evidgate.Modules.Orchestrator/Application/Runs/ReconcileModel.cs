namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// The 00 §8.5 <c>missing[]</c> codes the reconciler can name from event evidence (05.3.2-T2.2). The full closed list
/// (<c>artifact_missing</c>, <c>setup_error</c>, <c>build_mismatch</c>, <c>stale_epoch</c>) is owned by the migration
/// <c>0049</c> CHECK; 05.3.2-T2.3/T2.4 add their own codes beside these and never rename them.
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
/// round trip). Every stored event counts as acknowledged, whatever its <c>late</c>/<c>after_cancel</c>/<c>stale</c> flag.
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
internal sealed record ObservedShard(
    string ProducerId,
    long CountUpToDeclared,
    long CountBeyondDeclared,
    long? MinSequence,
    long? MaxSequence,
    long UnknownStatusCount,
    long UnfinishedStartedCount);

/// <summary>One reason the evidence is incomplete.</summary>
/// <param name="Code">A <see cref="ReconcileMissingCodes"/> value (00 §8.5).</param>
/// <param name="ProducerId">The shard concerned.</param>
/// <param name="Detail">A short machine-readable detail (numbers only, never a payload).</param>
internal sealed record ReconcileMissing(string Code, string? ProducerId, string Detail);

/// <summary>
/// The event-evidence half of the reconcile result. 05.3.2-T2.3 appends the artifact/setup reasons and T2.4 turns the
/// whole into a frozen <c>evidence_revision</c>; this type only reports and changes nothing.
/// </summary>
/// <param name="Missing">Reasons in a stable order (producer id, then code); empty when every shard matches.</param>
/// <param name="ShardsPlanned">Planned shards (producer slots of the run).</param>
/// <param name="ShardsClosed">Planned shards that finalized.</param>
internal sealed record ReconcileComparison(
    IReadOnlyList<ReconcileMissing> Missing,
    int ShardsPlanned,
    int ShardsClosed)
{
    /// <summary>True when the event evidence shows no gap (never a completeness verdict: T2.3/T2.4 decide that).</summary>
    public bool EventsMatch => Missing.Count == 0;
}

/// <summary>Why a reconcile attempt did or did not compare.</summary>
internal enum ReconcileDisposition
{
    /// <summary>The run row does not exist in this tenant.</summary>
    RunNotFound,

    /// <summary>The run is not <c>collecting</c> (terminal, canceled, or not yet finalized): nothing is compared, nothing changes.</summary>
    NotCollectable,

    /// <summary>The comparison ran; <see cref="ReconcileOutcome.Comparison"/> holds the result.</summary>
    Compared,
}

/// <summary>The result of one reconcile attempt; the run state is never changed by T2.1/T2.2.</summary>
/// <param name="Disposition">What happened.</param>
/// <param name="Comparison">Set only when <paramref name="Disposition"/> is <see cref="ReconcileDisposition.Compared"/>.</param>
internal sealed record ReconcileOutcome(ReconcileDisposition Disposition, ReconcileComparison? Comparison);

/// <summary>Runs one reconcile attempt for a run inside the caller's tenant transaction.</summary>
internal interface IRunReconciler
{
    /// <summary>
    /// Locks the run row <c>FOR NO KEY UPDATE</c>, lists the planned shards, reads the observed events per producer in
    /// one statement and compares. Deterministic: the same rows give the same outcome.
    /// </summary>
    /// <param name="runId">The run to reconcile.</param>
    /// <param name="cancellationToken">Cancelled on shutdown or lease loss.</param>
    ValueTask<ReconcileOutcome> ReconcileAsync(Guid runId, CancellationToken cancellationToken);
}
