using System.Globalization;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// The declared-versus-observed comparison of 05.3.2-T2.2 without a database: given the run's producer slots and the
/// one grouped aggregate row per producer, it names every <c>missing[]</c> reason the acknowledged events support.
/// Pure and deterministic (no I/O, no clock, no ambient scope), so the integration tests can exercise it directly
/// through <c>InternalsVisibleTo</c> and the reconciler stays a thin reader around it.
/// </summary>
/// <remarks>
/// The comparison is order-insensitive by construction: it never looks at a sequence number, only at the counts of the
/// aggregate, so storage order, late arrivals and gaps of length one all surface as a count disagreement. A shard whose
/// slot never closed is reported once as <see cref="ReconcileMissingCodes.MissingShard"/> and its events are not
/// compared; observed rows for producers outside the plan are ignored (the <c>event_slot_fk</c> makes them impossible).
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

            if (shard.DeclaredCount is not { } declaredCount || shard.DeclaredLastSequence is not { } declaredLastSequence)
            {
                // A closed slot without either declared value cannot be compared; fail closed instead of treating the
                // absent pair as a declaration of zero events.
                missing.Add(new ReconcileMissing(
                    ReconcileMissingCodes.CountMismatch,
                    shard.ProducerId,
                    DeclaredMissingDetail));
            }
            else if (seen.CountUpToDeclared != declaredCount || seen.CountBeyondDeclared > 0)
            {
                missing.Add(new ReconcileMissing(
                    ReconcileMissingCodes.CountMismatch,
                    shard.ProducerId,
                    CountMismatchDetail(declaredCount, declaredLastSequence, seen.CountUpToDeclared, seen.CountBeyondDeclared)));
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
        new(producerId, TotalCount: 0, CountUpToDeclared: 0, CountBeyondDeclared: 0, MinSequence: null, MaxSequence: null, UnknownStatusCount: 0, UnfinishedStartedCount: 0);

    /// <summary>The four numbers of a count disagreement, invariantly formatted and payload-free.</summary>
    /// <param name="declaredCount">The slot's declared event count.</param>
    /// <param name="declaredLastSequence">The slot's declared high-water mark.</param>
    /// <param name="observed">Stored events with a sequence up to the declared high-water mark.</param>
    /// <param name="beyond">Stored events past the declared high-water mark.</param>
    private static string CountMismatchDetail(
        int declaredCount,
        long declaredLastSequence,
        long observed,
        long beyond) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"declared_count={declaredCount};observed={observed};declared_last_sequence={declaredLastSequence};beyond={beyond}");
}
