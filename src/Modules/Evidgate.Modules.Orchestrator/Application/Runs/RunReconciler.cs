using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// The Dapper read behind <see cref="IRunReconciler"/>: statements in a fixed order over the ambient tenant scope, so it
/// joins the caller's transaction and RLS scopes every row, exactly like <see cref="RunEventScopeReader"/>. The run row
/// is locked first and the state decides the outcome: only a <c>collecting</c> run — or a finished <c>incomplete</c> one
/// this attempt carries late evidence for, inside its late window — is compared, and nothing is read beyond its state,
/// because an enqueued finalize trigger is not evidence that the run is collectable. Read-only by construction — it never
/// writes and never changes the run state; the work after the lock is the slot list, the one grouped event aggregate and,
/// for the attempt that decides, the bounded read of the plan's uncovered executions.
/// <para>
/// The lock statement also answers the run's collection deadline (<c>deadline_passed</c>) and its late window
/// (<c>late_window_open</c>), both on the database clock: the first decides between
/// <see cref="ReconcileDisposition.Pending"/> and <see cref="ReconcileDisposition.Compared"/> when the plan still holds an
/// open shard (before the deadline the attempt waits, past it the open shard is <c>missing_shard</c> and the attempt
/// finishes), the second is the window late evidence was accepted in.
/// </para>
/// </summary>
/// <param name="db">The Dapper path over the ambient scope; it demands an active scope before it touches the database.</param>
/// <param name="scopes">The tenant-scope accessor; <c>Require()</c> is the guard that throws before any statement.</param>
internal sealed class RunReconciler(ITenantDb db, ITenantScopeAccessor scopes) : IRunReconciler
{
    /// <inheritdoc />
    public async ValueTask<ReconcileOutcome> ReconcileAsync(
        Guid runId,
        bool lateTrigger,
        CancellationToken cancellationToken)
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

        // A run that finished as incomplete still takes evidence inside its late window, and that evidence only counts
        // once a revision of its own says so. Both halves must hold: this attempt carries late evidence, and the run is
        // still inside the window the ingest route accepted it through. Anything else — a redelivered finalize or
        // deadline trigger, a replay after the window closed — would append a revision with no late event behind it, so
        // it stays uncollectable.
        var late = lateTrigger
            && string.Equals(locked.State, RunStates.Incomplete, StringComparison.Ordinal)
            && locked.LateWindowOpen;

        // Statement 2 never runs for a run outside its collection state: nothing to compare, nothing to change.
        if (!late && !string.Equals(locked.State, RunStates.Collecting, StringComparison.Ordinal))
        {
            return new ReconcileOutcome(ReconcileDisposition.NotCollectable, Comparison: null);
        }

        var slots = await db
            .QueryAsync<PlannedSlotRow>(ReconcileSql.ListSlots, parameters, cancellationToken)
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

        var observed = await db
            .QueryAsync<ObservedEventRow>(ReconcileSql.ObservedEvents, parameters, cancellationToken)
            .ConfigureAwait(false);

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
                row.BlockedCount);
        }

        // An open shard before the collection deadline is not a verdict: the attempt waits (Pending) so the run stays
        // collecting until the last slot closes or the deadline forces the decision (COORDINATOR-DECISION S123, F1).
        // The open-shard decision reads the slot list taken AFTER the run-row lock, so two final finalizes serialise on
        // that lock and decide exactly once. `deadline_passed` comes from the lock statement, evaluated by the database.
        // A waiting attempt returns here, before the coverage read: it decides nothing, and coverage cannot turn a wait
        // into a verdict, so the bounded read of the plan is spent only on the attempt that compares for real.
        // A finished run never waits: its verdict is already written, its open shard would never close now that no
        // finalize is accepted, and waiting would leave the late evidence unread for good.
        if (!late && HasOpenShard(planned) && !locked.DeadlinePassed)
        {
            return new ReconcileOutcome(
                ReconcileDisposition.Pending,
                ReconcileComparer.CompareEvents(planned, seen));
        }

        var coverage = await db
            .QueryAsync<UncoveredExecutionRow>(ReconcileSql.ListUncoveredExecutions, parameters, cancellationToken)
            .ConfigureAwait(false);

        return new ReconcileOutcome(
            late ? ReconcileDisposition.ComparedLate : ReconcileDisposition.Compared,
            ReconcileComparer.Compare(planned, seen, MapCoverage(coverage)));
    }

    /// <summary>
    /// The bounded coverage read as the comparer's input. No row is the statement that every planned execution recorded
    /// an attempt; the total comes from the statement's own window count, so a read cut short by its cap still carries
    /// the true gap.
    /// </summary>
    /// <param name="rows">The uncovered rows, at most <see cref="ReconcileSql.UncoveredExecutionLimit"/> of them.</param>
    private static ExecutionCoverage MapCoverage(IReadOnlyList<UncoveredExecutionRow> rows)
    {
        if (rows.Count == 0)
        {
            return ExecutionCoverage.Complete;
        }

        var executions = new PlannedExecution[rows.Count];
        for (var index = 0; index < rows.Count; index++)
        {
            executions[index] = new PlannedExecution(rows[index].ExecutionKey, rows[index].ShardId);
        }

        return new ExecutionCoverage(executions, rows[0].UncoveredTotal);
    }

    /// <summary>True when the shard plan still holds a slot that never closed.</summary>
    /// <param name="planned">The run's producer slots as the shard plan, one row per shard.</param>
    private static bool HasOpenShard(PlannedShard[] planned)
    {
        for (var index = 0; index < planned.Length; index++)
        {
            if (!planned[index].Closed)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One row as <see cref="ReconcileSql.LockRun"/> returns it (snake_case → PascalCase, Dapper's underscore map): the
    /// state the attempt decides on, the collection deadline and the late window, both evaluated by the <b>database</b>.
    /// </summary>
    private sealed class RunLockRow
    {
        public string State { get; set; } = string.Empty;

        /// <summary>True when <c>collection_deadline_at &lt;= now()</c>; a NULL deadline reads false (never "passed").</summary>
        public bool DeadlinePassed { get; set; }

        /// <summary>
        /// True when <c>now() &lt;= collection_deadline_at + late_window_minutes</c>: the window the ingest route admits
        /// late evidence through, read from the same row under the same lock. A NULL deadline reads false.
        /// </summary>
        public bool LateWindowOpen { get; set; }
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

        public long BlockedCount { get; set; }
    }

    /// <summary>
    /// One row as <see cref="ReconcileSql.ListUncoveredExecutions"/> returns it: a planned execution of the run with no
    /// recorded attempt, the shard whose producer id the reason carries, and the statement's total for the whole run.
    /// </summary>
    private sealed class UncoveredExecutionRow
    {
        public string ExecutionKey { get; set; } = string.Empty;

        public string ShardId { get; set; } = string.Empty;

        public long UncoveredTotal { get; set; }
    }
}
