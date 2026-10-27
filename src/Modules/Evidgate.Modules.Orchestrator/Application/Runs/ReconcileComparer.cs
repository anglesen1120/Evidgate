using System.Globalization;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// The declared-versus-observed comparison of 05.3.2-T2.2 without a database: given the run's producer slots and the
/// one grouped aggregate row per producer, it names every <c>missing[]</c> reason the acknowledged events support.
/// Pure and deterministic (no I/O, no clock, no ambient scope), so the integration tests can exercise it directly
/// through <c>InternalsVisibleTo</c> and the reconciler stays a thin reader around it.
/// </summary>
/// <remarks>
/// The comparison never walks the stored sequences; it reads the aggregate's counts and its two bounds. For a closed
/// shard that declared at least one event it enforces the gap rule of <c>contracts/events/ordering.md</c> §3, which is
/// derived from exactly those columns: the sequences in <c>[min stored sequence, declared_last_sequence]</c> must all be
/// stored, and the highest stored sequence must be the declared high-water mark. A count alone cannot see either — three
/// stored sequences 0, 1, 2 under a declaration of three events ending at 3 is a hole — so an equal count with a hole
/// is still a disagreement. Storage order, late arrivals and a hole below the lowest stored sequence (an unwritten
/// prefix) change nothing. A shard whose slot never closed is reported once as
/// <see cref="ReconcileMissingCodes.MissingShard"/> and its events are not compared; observed rows for producers outside
/// the plan are ignored (the <c>event_slot_fk</c> makes them impossible).
/// </remarks>
internal static class ReconcileComparer
{
    /// <summary>The detail of a closed shard whose first close left the declared pair null: nothing to compare against.</summary>
    internal const string DeclaredMissingDetail = "declared_missing";

    /// <summary>
    /// Compares the planned shards against the observed events. Reasons are ordered by producer id (ordinal) and, per
    /// shard, in the fixed code order <c>missing_shard</c>, <c>count_mismatch</c>, <c>unknown_status</c>.
    /// </summary>
    /// <param name="planned">The run's producer slots as the shard plan; one row per shard.</param>
    /// <param name="observed">The grouped aggregate rows, at most one per producer.</param>
    /// <returns>The reasons and the planned/closed shard counts.</returns>
    /// <exception cref="ArgumentNullException">Either list is null.</exception>
    public static ReconcileComparison Compare(
        IReadOnlyList<PlannedShard> planned,
        IReadOnlyList<ObservedShard> observed)
    {
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(observed);

        // The one grouped statement returns at most one row per producer; a repeat is ignored rather than merged so the
        // result stays a pure function of the input sequence.
        var observations = new Dictionary<string, ObservedShard>(observed.Count, StringComparer.Ordinal);
        foreach (var row in observed)
        {
            observations.TryAdd(row.ProducerId, row);
        }

        var missing = new List<ReconcileMissing>();
        var closedShards = 0;

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

            closedShards++;

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

        return new ReconcileComparison(missing.ToArray(), planned.Count, closedShards);
    }

    /// <summary>The observation of a producer that stored no event.</summary>
    /// <param name="producerId">The producer id the plan names.</param>
    private static ObservedShard NoObservation(string producerId) =>
        new(producerId, CountUpToDeclared: 0, CountBeyondDeclared: 0, MinSequence: null, MaxSequence: null, UnknownStatusCount: 0, UnfinishedStartedCount: 0);

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
    /// <c>min_sequence</c>, <c>contiguous_count</c> — the contiguity bound, checked for a declaration of at least one
    /// event: the gap rule of <c>contracts/events/ordering.md</c> §3, where the sequences in
    /// <c>[min stored sequence, declared_last_sequence]</c> must all be stored, so their number is
    /// <c>declared_last_sequence - min_sequence + 1</c>. A range that is empty or inverted (the producer started above
    /// its declared last sequence) has no hole to report and is the count bound's business alone.
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
                var contiguousCount = declaredLastSequence - minSequence + 1;
                if (contiguousCount > 0 && seen.CountUpToDeclared != contiguousCount)
                {
                    detail = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{detail};min_sequence={minSequence};contiguous_count={contiguousCount}");
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
