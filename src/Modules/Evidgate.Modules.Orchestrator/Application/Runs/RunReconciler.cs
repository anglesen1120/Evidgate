using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// The Dapper read behind <see cref="IRunReconciler"/> (05.3.2-T2.1/T2.2): four statements in a fixed order over the
/// ambient tenant scope, so it joins the caller's transaction and RLS scopes every row (00 §5.5 C3/C5), exactly like
/// <see cref="RunEventScopeReader"/>. The run row is locked first and the state decides the outcome: a run that is not
/// <c>collecting</c> is never compared, and nothing is read beyond its state (plan Notes, 05.3.2-T1 line 152: the
/// finalize trigger is not evidence that the run is collectable). Read-only by construction — it never writes and never
/// changes the run state; the only work after the lock is the slot list, the one grouped event aggregate and the
/// coverage read of the plan's executions that recorded no attempt.
/// <para>
/// The lock statement also answers the run's collection deadline (<c>deadline_passed</c>, the database clock), which
/// decides between <see cref="ReconcileDisposition.Pending"/> and <see cref="ReconcileDisposition.Compared"/> when the
/// plan still holds an open shard: before the deadline the attempt waits, past it the open shard is
/// <c>missing_shard</c> and the attempt finishes.
/// </para>
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
        var locked = await db
            .QuerySingleOrDefaultAsync<RunLockRow>(ReconcileSql.LockRun, parameters, cancellationToken)
            .ConfigureAwait(false);

        if (locked is null)
        {
            return new ReconcileOutcome(ReconcileDisposition.RunNotFound, Comparison: null);
        }

        // Statement 2 never runs for a run outside its collection state: nothing to compare, nothing to change.
        if (!string.Equals(locked.State, RunStates.Collecting, StringComparison.Ordinal))
        {
            return new ReconcileOutcome(ReconcileDisposition.NotCollectable, Comparison: null);
        }

        var slots = await db
            .QueryAsync<PlannedSlotRow>(ReconcileSql.ListSlots, parameters, cancellationToken)
            .ConfigureAwait(false);

        var observed = await db
            .QueryAsync<ObservedEventRow>(ReconcileSql.ObservedEvents, parameters, cancellationToken)
            .ConfigureAwait(false);

        // Statement 4: the plan's executions with no recorded attempt. Read after the events under the same run lock, so
        // the comparison and the coverage describe one instant of the run.
        var uncovered = await db
            .QueryAsync<UncoveredExecutionRow>(ReconcileSql.ListUncoveredExecutions, parameters, cancellationToken)
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
                row.CountUpToDeclared,
                row.CountBeyondDeclared,
                row.MinSequence,
                row.MaxSequence,
                row.UnknownStatusCount,
                row.UnfinishedStartedCount,
                row.StaleCount,
                row.BlockedCount);
        }

        var comparison = ReconcileComparer.Compare(planned, seen, MapUncovered(uncovered));

        // An open shard before the collection deadline is not a verdict: the attempt waits (Pending) so the run stays
        // collecting until the last slot closes or the deadline forces the decision (COORDINATOR-DECISION S123, F1).
        // The open-shard decision reads the slot list taken AFTER the run-row lock, so two final finalizes serialise on
        // that lock and decide exactly once. `deadline_passed` comes from the lock statement, evaluated by the database.
        return new ReconcileOutcome(
            HasOpenShard(comparison) && !locked.DeadlinePassed
                ? ReconcileDisposition.Pending
                : ReconcileDisposition.Compared,
            comparison);
    }

    /// <summary>
    /// The planned executions the coverage statement answered, as the comparer's input. An empty answer means every
    /// planned execution recorded an attempt — not that the read was skipped: the reconciler always passes what it read.
    /// </summary>
    /// <param name="rows">The uncovered rows, in the statement's order.</param>
    private static PlannedExecution[] MapUncovered(IReadOnlyList<UncoveredExecutionRow> rows)
    {
        var executions = new PlannedExecution[rows.Count];
        for (var index = 0; index < rows.Count; index++)
        {
            executions[index] = new PlannedExecution(rows[index].ExecutionKey, rows[index].ShardId);
        }

        return executions;
    }

    /// <summary>True when the comparison names an open planned shard (<c>missing_shard</c>).</summary>
    /// <param name="comparison">The comparison of this attempt.</param>
    private static bool HasOpenShard(ReconcileComparison comparison)
    {
        for (var index = 0; index < comparison.Missing.Count; index++)
        {
            var code = comparison.Missing[index].Code;
            if (string.Equals(code, ReconcileMissingCodes.MissingShard, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One row as <see cref="ReconcileSql.LockRun"/> returns it (snake_case → PascalCase, Dapper's underscore map): the
    /// state the attempt decides on and the collection deadline the <b>database</b> evaluated.
    /// </summary>
    private sealed class RunLockRow
    {
        public string State { get; set; } = string.Empty;

        /// <summary>True when <c>collection_deadline_at &lt;= now()</c>; a NULL deadline reads false (never "passed").</summary>
        public bool DeadlinePassed { get; set; }
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

        public long CountUpToDeclared { get; set; }

        public long CountBeyondDeclared { get; set; }

        public long? MinSequence { get; set; }

        public long? MaxSequence { get; set; }

        public long UnknownStatusCount { get; set; }

        public long UnfinishedStartedCount { get; set; }

        public long StaleCount { get; set; }

        public long BlockedCount { get; set; }
    }

    /// <summary>
    /// One row as <see cref="ReconcileSql.ListUncoveredExecutions"/> returns it: a planned execution of the run with no
    /// recorded attempt, and the shard whose producer id the reason carries.
    /// </summary>
    private sealed class UncoveredExecutionRow
    {
        public string ExecutionKey { get; set; } = string.Empty;

        public string ShardId { get; set; } = string.Empty;
    }
}
