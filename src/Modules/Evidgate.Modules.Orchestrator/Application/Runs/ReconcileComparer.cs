using System.Globalization;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// The declared-versus-observed comparison of 05.3.2-T2.2/T2.3 without a database: given the run's producer slots and
/// the one grouped aggregate row per producer, it names every <c>missing[]</c> reason that evidence supports and the
/// stored-event watermark of the run.
/// Pure and deterministic (no I/O, no clock, no ambient scope), so the integration tests can exercise it directly
/// through <c>InternalsVisibleTo</c> and the reconciler stays a thin reader around it.
/// </summary>
/// <remarks>
/// The comparison never walks the stored sequences; it reads the aggregate's counts, its two bounds and its two flag
/// counts. For a closed shard that declared at least one event it enforces the gap rule of
/// <c>contracts/events/ordering.md</c> §3, which is derived from exactly those columns: the sequences in
/// <c>[min stored sequence, declared_last_sequence]</c> must all be stored, and the highest stored sequence must be the
/// declared high-water mark. A count alone cannot see either — three stored sequences 0, 1, 2 under a declaration of
/// three events ending at 3 is a hole — so an equal count with a hole is still a disagreement. Storage order, late
/// arrivals and a hole below the lowest stored sequence (an unwritten prefix) change nothing.
/// <para>
/// Every stored event counts as acknowledged whatever its <c>stale</c> flag, so a stale event can fill a sequence slot
/// and leave the count bounds agreeing; the stale count keeps that from looking complete — a closed shard holding any
/// stale event names <see cref="ReconcileMissingCodes.StaleEpoch"/>, and a closed shard holding a
/// <c>blocked</c> <c>test.finished</c> names <see cref="ReconcileMissingCodes.SetupError"/>.
/// </para>
/// <para>
/// A shard whose slot never closed is reported once as <see cref="ReconcileMissingCodes.MissingShard"/> and neither its
/// events nor its flags are compared. An observed row for a producer the plan does not name cannot exist — the aggregate
/// starts from the run's own slots and <c>event_slot_fk</c> ties every event to one of them — so the observed rows are
/// exactly the plan's producers; the watermark is their stored-event total and no producer can be counted twice (a
/// repeated producer row, impossible from the grouped read, is still ignored rather than merged).
/// </para>
/// <para>
/// The events are not the whole of completeness: a planned execution that recorded no attempt is named once as
/// <see cref="ReconcileMissingCodes.MissingResult"/>, because a producer that declares fewer events than the plan
/// expected can agree with its own declaration while a planned case never ran. That reason is read from the plan rows
/// and the recorded attempts alone — no event decides it — so it survives every matching count.
/// </para>
/// </remarks>
internal static class ReconcileComparer
{
    /// <summary>The detail of a closed shard whose first close left the declared pair null: nothing to compare against.</summary>
    internal const string DeclaredMissingDetail = "declared_missing";

    /// <summary>
    /// Compares the planned shards against the observed events, and the run's plan against the attempts it recorded.
    /// Reasons are ordered by producer id (ordinal), then code (ordinal), then detail (ordinal): one closed shard
    /// reports <c>count_mismatch</c>, <c>setup_error</c>, <c>stale_epoch</c> and <c>unknown_status</c> in that order, an
    /// open shard reports only <c>missing_shard</c>, and a planned execution with no attempt reports one
    /// <c>missing_result</c> of its own.
    /// </summary>
    /// <param name="planned">The run's producer slots as the shard plan; one row per shard.</param>
    /// <param name="observed">The grouped aggregate rows, at most one per producer.</param>
    /// <param name="coverage">
    /// The planned executions that recorded no attempt, as the bounded coverage read answered them. It is required
    /// because a comparison without it silently reports matching evidence for a plan nobody ran: an empty list is the
    /// statement that every planned execution reported, never a read that was skipped.
    /// </param>
    /// <returns>The reasons, the planned/closed shard counts and the stored-event watermark.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ReconcileComparison Compare(
        IReadOnlyList<PlannedShard> planned,
        IReadOnlyList<ObservedShard> observed,
        ExecutionCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);

        var missing = ShardReasons(planned, observed, out var closedShards, out var eventWatermark);
        AddCoverageReasons(missing, coverage);

        return new ReconcileComparison(Order(missing), planned.Count, closedShards, eventWatermark);
    }

    /// <summary>
    /// The event half of the comparison alone: the planned shards against the observed events, with no plan coverage.
    /// A waiting attempt reports it as its log line, because it decides nothing and does not read the coverage.
    /// </summary>
    /// <param name="planned">The run's producer slots as the shard plan; one row per shard.</param>
    /// <param name="observed">The grouped aggregate rows, at most one per producer.</param>
    /// <returns>The reasons, the planned/closed shard counts and the stored-event watermark.</returns>
    /// <exception cref="ArgumentNullException">Either list is null.</exception>
    public static ReconcileComparison CompareEvents(
        IReadOnlyList<PlannedShard> planned,
        IReadOnlyList<ObservedShard> observed)
    {
        var missing = ShardReasons(planned, observed, out var closedShards, out var eventWatermark);

        return new ReconcileComparison(Order(missing), planned.Count, closedShards, eventWatermark);
    }

    /// <summary>
    /// The declared-versus-observed and readiness reasons of the event evidence, in the order the shards are visited;
    /// <see cref="Order"/> puts them in their stated order once every reason is in.
    /// </summary>
    /// <param name="planned">The run's producer slots as the shard plan; one row per shard.</param>
    /// <param name="observed">The grouped aggregate rows, at most one per producer.</param>
    /// <param name="closedShards">How many planned shards finalized.</param>
    /// <param name="eventWatermark">The stored-event total of the observed rows read.</param>
    private static List<ReconcileMissing> ShardReasons(
        IReadOnlyList<PlannedShard> planned,
        IReadOnlyList<ObservedShard> observed,
        out int closedShards,
        out long eventWatermark)
    {
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(observed);

        // The grouped statement returns exactly one row per slot of the run, so an observed producer is one the plan
        // names (<c>event_slot_fk</c> makes an outside-plan producer impossible); a repeated row is still ignored rather
        // than merged, so the result stays a pure function of the input sequence. Events are immutable, so the stored
        // total only grows and is a watermark T2.4 can persist as it stands, summed over the rows the comparison read.
        var observations = new Dictionary<string, ObservedShard>(observed.Count, StringComparer.Ordinal);
        long stored = 0;
        foreach (var row in observed)
        {
            if (observations.TryAdd(row.ProducerId, row))
            {
                stored += row.CountUpToDeclared + row.CountBeyondDeclared;
            }
        }

        var missing = new List<ReconcileMissing>();
        var closed = 0;

        foreach (var shard in planned.OrderBy(static slot => slot.ProducerId, StringComparer.Ordinal))
        {
            if (!shard.Closed)
            {
                // An open slot never finalized: exactly one reason, and its events are deliberately not compared.
                missing.Add(new ReconcileMissing(
                    ReconcileMissingCodes.MissingShard,
                    shard.ProducerId,
                    string.Create(CultureInfo.InvariantCulture, $"shard_index={shard.ShardIndex}")));
                continue;
            }

            closed++;

            // An absent row means the producer stored no event: zero acknowledged events, no status evidence.
            var seen = observations.TryGetValue(shard.ProducerId, out var row) ? row : NoObservation(shard.ProducerId);

            if (shard.DeclaredCount is null || shard.DeclaredLastSequence is null)
            {
                // A closed slot without either declared value cannot be compared; fail closed instead of treating the
                // absent pair as a declaration of zero events.
                missing.Add(new ReconcileMissing(
                    ReconcileMissingCodes.CountMismatch,
                    shard.ProducerId,
                    DeclaredMissingDetail));
            }
            else if (CountMismatchDetail(shard, seen) is { } countDetail)
            {
                // One entry per shard: every violated bound is a ';'-joined fragment of the same reason.
                missing.Add(new ReconcileMissing(
                    ReconcileMissingCodes.CountMismatch,
                    shard.ProducerId,
                    countDetail));
            }

            // A blocked finish is a failed setup (00 §8.3), and a stale event is an older epoch: neither depends on the
            // declaration, so both are named for a shard that already reported declared_missing as well.
            if (seen.BlockedCount > 0)
            {
                missing.Add(new ReconcileMissing(
                    ReconcileMissingCodes.SetupError,
                    shard.ProducerId,
                    string.Create(CultureInfo.InvariantCulture, $"blocked={seen.BlockedCount}")));
            }

            if (seen.StaleCount > 0)
            {
                // A stale event still filled its sequence slot above, so the counts can agree while the evidence is not
                // trustworthy: this reason is what keeps the shard from looking complete (D-S119-Q2 risk).
                missing.Add(new ReconcileMissing(
                    ReconcileMissingCodes.StaleEpoch,
                    shard.ProducerId,
                    string.Create(CultureInfo.InvariantCulture, $"stale={seen.StaleCount}")));
            }

            // A null normalized_status and a started attempt with no finish do not depend on the declaration, so this
            // reason is also named for a shard that already reported declared_missing.
            if (seen.UnknownStatusCount > 0 || seen.UnfinishedStartedCount > 0)
            {
                missing.Add(new ReconcileMissing(
                    ReconcileMissingCodes.UnknownStatus,
                    shard.ProducerId,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"null_status={seen.UnknownStatusCount};unfinished={seen.UnfinishedStartedCount}")));
            }
        }

        closedShards = closed;
        eventWatermark = stored;

        return missing;
    }

    /// <summary>
    /// The stated order, made explicit rather than implied by the loop that adds the reasons: producer id (ordinal), then
    /// code (ordinal), then the detail, which is what separates two planned executions of one shard. Each shard code is
    /// added at most once per shard, so the ordering is total and stays stable for equal keys.
    /// </summary>
    /// <param name="missing">Every reason the comparison named.</param>
    private static ReconcileMissing[] Order(List<ReconcileMissing> missing) =>
        missing
            .OrderBy(static reason => reason.ProducerId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static reason => reason.Code, StringComparer.Ordinal)
            .ThenBy(static reason => reason.Detail, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Names the plan's executions that recorded no attempt: one <c>missing_result</c> per row, and when the bounded read
    /// stopped at its cap, one more reason carrying the true total. The reason is independent of the events — a producer
    /// can declare a smaller count and agree with it — so only the plan's own rows can say that a planned case never
    /// reported a status, and the count keeps a huge plan from hiding behind the cap. The detail carries the execution key,
    /// the plan row's identity and never a payload, so the entries of one shard stay distinguishable.
    /// </summary>
    /// <param name="missing">The reasons collected so far.</param>
    /// <param name="coverage">The uncovered executions read and the true total.</param>
    private static void AddCoverageReasons(List<ReconcileMissing> missing, ExecutionCoverage coverage)
    {
        foreach (var execution in coverage.Uncovered)
        {
            missing.Add(new ReconcileMissing(
                ReconcileMissingCodes.MissingResult,
                execution.ShardId,
                $"execution_key={execution.ExecutionKey}"));
        }

        if (coverage.Total > coverage.Uncovered.Count)
        {
            missing.Add(new ReconcileMissing(
                ReconcileMissingCodes.MissingResult,
                ProducerId: null,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"uncovered_total={coverage.Total};listed={coverage.Uncovered.Count}")));
        }
    }

    /// <summary>The observation of a producer that stored no event.</summary>
    /// <param name="producerId">The producer id the plan names.</param>
    private static ObservedShard NoObservation(string producerId) =>
        new(
            producerId,
            CountUpToDeclared: 0,
            CountBeyondDeclared: 0,
            MinSequence: null,
            MaxSequence: null,
            UnknownStatusCount: 0,
            UnfinishedStartedCount: 0,
            StaleCount: 0,
            BlockedCount: 0);

    /// <summary>
    /// The detail of a closed shard whose declaration at least one bound breaks, or null when the declared pair and the
    /// stored evidence agree. It is one <c>;</c>-joined record of numbers only (never an event value, never a payload):
    /// the declaration against the stored counts first, then one segment per further violated bound, so every key
    /// appears at most once.
    /// <list type="bullet">
    /// <item><description>
    /// <c>declared_count</c>, <c>observed</c>, <c>declared_last_sequence</c>, <c>beyond</c> — the count bound: the
    /// events up to the declared high-water mark against <c>declared_count</c>, and whether any event went past it.
    /// </description></item>
    /// <item><description>
    /// <c>max_sequence</c> — the end bound, checked for a declaration of at least one event: the highest stored
    /// sequence must be the declared high-water mark (a count cannot see where the stored range ends).
    /// </description></item>
    /// <item><description>
    /// <c>min_sequence</c>, <c>sequence_span</c> — the contiguity bound, checked for a declaration of at least one
    /// event: the gap rule of <c>contracts/events/ordering.md</c> §3, where the sequences in
    /// <c>[min stored sequence, declared_last_sequence]</c> must all be stored, so the stored count minus one equals
    /// <c>sequence_span = declared_last_sequence - min_sequence</c> (no <c>+ 1</c>, which would wrap at
    /// <c>long.MaxValue</c>). A range that is empty or inverted (the producer started above its declared last sequence)
    /// has no hole to report and is the count bound's business alone.
    /// </description></item>
    /// </list>
    /// </summary>
    /// <param name="shard">The closed shard, whose declared pair is not null.</param>
    /// <param name="seen">The shard's observation.</param>
    /// <returns>The payload-free detail, or null when every bound holds.</returns>
    private static string? CountMismatchDetail(PlannedShard shard, ObservedShard seen)
    {
        var declaredCount = shard.DeclaredCount!.Value;
        var declaredLastSequence = shard.DeclaredLastSequence!.Value;

        var violated = seen.CountUpToDeclared != declaredCount || seen.CountBeyondDeclared > 0;
        var detail = CountFragment(
            declaredCount,
            declaredLastSequence,
            seen.CountUpToDeclared,
            seen.CountBeyondDeclared);

        // A declaration of zero events has no range to be contiguous or to end on: its only evidence is that nothing
        // was stored at all, which the count bound above already decides (both counts zero).
        if (declaredCount > 0)
        {
            if (seen.MaxSequence is { } maxSequence && maxSequence != declaredLastSequence)
            {
                detail = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{detail};max_sequence={maxSequence}");
                violated = true;
            }

            if (seen.MinSequence is { } minSequence)
            {
                // span = last - min cannot overflow (both are >= 0); comparing it with count - 1 avoids the `+ 1`
                // that wraps at long.MaxValue.
                var span = declaredLastSequence - minSequence;
                if (span >= 0 && seen.CountUpToDeclared - 1 != span)
                {
                    detail = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{detail};min_sequence={minSequence};sequence_span={span}");
                    violated = true;
                }
            }
        }

        return violated ? detail : null;
    }

    /// <summary>The four numbers of the count bound, invariantly formatted and payload-free.</summary>
    /// <param name="declaredCount">The slot's declared event count.</param>
    /// <param name="declaredLastSequence">The slot's declared high-water mark.</param>
    /// <param name="observed">Stored events with a sequence up to the declared high-water mark.</param>
    /// <param name="beyond">Stored events past the declared high-water mark.</param>
    private static string CountFragment(
        int declaredCount,
        long declaredLastSequence,
        long observed,
        long beyond) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"declared_count={declaredCount};observed={observed};declared_last_sequence={declaredLastSequence};beyond={beyond}");
}
