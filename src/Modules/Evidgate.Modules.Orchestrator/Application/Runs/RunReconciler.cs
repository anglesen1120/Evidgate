using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// The Dapper read behind <see cref="IRunReconciler"/> (05.3.2-T2.1/T2.2): three statements in a fixed order over the
/// ambient tenant scope, so it joins the caller's transaction and RLS scopes every row (00 §5.5 C3/C5), exactly like
/// <see cref="RunEventScopeReader"/>. The run row is locked first and the state decides the outcome: a run that is not
/// <c>collecting</c> is never compared, and nothing is read beyond its state (plan Notes, 05.3.2-T1 line 152: the
/// finalize trigger is not evidence that the run is collectable). Read-only by construction — it never writes and never
/// changes the run state; the only work after the lock is the slot list and the one grouped event aggregate.
/// </summary>
/// <param name="db">The Dapper path over the ambient scope; it demands an active scope before it touches the database.</param>
/// <param name="scopes">The tenant-scope accessor; <c>Require()</c> is the guard that throws before any statement.</param>
internal sealed class RunReconciler(ITenantDb db, ITenantScopeAccessor scopes) : IRunReconciler
{
    /// <inheritdoc />
    public async ValueTask<ReconcileOutcome> ReconcileAsync(Guid runId, CancellationToken cancellationToken)
    {
        var orgId = scopes.Require().OrgId;
        var parameters = new { org_id = orgId, run_id = runId };

        // Statement 1: the run row, locked FOR NO KEY UPDATE (see ReconcileSql.LockRun for the lock mode).
        var state = await db
            .QuerySingleOrDefaultAsync<string>(ReconcileSql.LockRun, parameters, cancellationToken)
            .ConfigureAwait(false);

        if (state is null)
        {
            return new ReconcileOutcome(ReconcileDisposition.RunNotFound, Comparison: null);
        }

        // Statement 2 never runs for a run outside its collection state: nothing to compare, nothing to change.
        if (!string.Equals(state, RunStates.Collecting, StringComparison.Ordinal))
        {
            return new ReconcileOutcome(ReconcileDisposition.NotCollectable, Comparison: null);
        }

        var slots = await db
            .QueryAsync<PlannedSlotRow>(ReconcileSql.ListSlots, parameters, cancellationToken)
            .ConfigureAwait(false);

        var observed = await db
            .QueryAsync<ObservedEventRow>(ReconcileSql.ObservedEvents, parameters, cancellationToken)
            .ConfigureAwait(false);

        var planned = new PlannedShard[slots.Count];
        for (var index = 0; index < slots.Count; index++)
        {
            var slot = slots[index];
            planned[index] = new PlannedShard(
                slot.ProducerId,
                slot.ShardIndex,
                slot.Closed,
                slot.DeclaredLastSequence,
                slot.DeclaredCount);
        }

        var seen = new ObservedShard[observed.Count];
        for (var index = 0; index < observed.Count; index++)
        {
            var row = observed[index];
            seen[index] = new ObservedShard(
                row.ProducerId,
                row.TotalCount,
                row.CountUpToDeclared,
                row.CountBeyondDeclared,
                row.MinSequence,
                row.MaxSequence,
                row.UnknownStatusCount,
                row.UnfinishedStartedCount);
        }

        return new ReconcileOutcome(ReconcileDisposition.Compared, ReconcileComparer.Compare(planned, seen));
    }

    /// <summary>One row as <see cref="ReconcileSql.ListSlots"/> returns it (snake_case → PascalCase, Dapper's underscore map).</summary>
    private sealed class PlannedSlotRow
    {
        public string ProducerId { get; set; } = string.Empty;

        public int ShardIndex { get; set; }

        public bool Closed { get; set; }

        public long? DeclaredLastSequence { get; set; }

        public int? DeclaredCount { get; set; }
    }

    /// <summary>
    /// One row as <see cref="ReconcileSql.ObservedEvents"/> returns it: the grouped aggregate of the run's events per
    /// producer, at most one row per slot. Counts are PostgreSQL <c>bigint</c> (<c>long</c>) and the sequence bounds are
    /// NULL for a producer that stored no event.
    /// </summary>
    private sealed class ObservedEventRow
    {
        public string ProducerId { get; set; } = string.Empty;

        public long TotalCount { get; set; }

        public long CountUpToDeclared { get; set; }

        public long CountBeyondDeclared { get; set; }

        public long? MinSequence { get; set; }

        public long? MaxSequence { get; set; }

        public long UnknownStatusCount { get; set; }

        public long UnfinishedStartedCount { get; set; }
    }
}
