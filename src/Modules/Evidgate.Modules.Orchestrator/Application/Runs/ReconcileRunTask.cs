using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// Consumes one <c>orchestrator.run.reconcile</c> row (05.3.2 §5/§7, <c>ReconcileRunTask</c>): the trigger a finalize
/// enqueues when it newly closes a producer slot (<see cref="RunReconcileContract"/>). Tenant-bound, so the consumer
/// opens the row's tenant transaction around it and commits it only when the attempt returns. The handler resolves and
/// decides nothing itself: it hands the run id to <see cref="IRunReconciler"/> and, for a compared attempt, that attempt's
/// comparison to <see cref="IReconcileFinisher"/>, which is why a run that is gone, not collectable, or still
/// <c>pending</c> (a planned shard is open and the collection deadline has not passed) is a normal completion — the task
/// row is the record of what ran, and nothing changed.
/// <para>
/// <see cref="IServiceScopeFactory"/> is the plumbing that reaches the scoped <see cref="IRunReconciler"/>: the task
/// queue registers every handler as a singleton (<c>AddTaskHandler</c>), and a scoped service must not be captured by a
/// singleton. The DI scope is not the tenant scope — that one is ambient (<see cref="ITenantDb"/> reads it from
/// <see cref="ITenantScopeAccessor"/>) and stays the consumer's, including its commit.
/// </para>
/// </summary>
/// <param name="scopeFactory">Creates the per-attempt scope the reconciler is resolved from.</param>
/// <param name="logger">The logger of the one structured line per attempt.</param>
internal sealed partial class ReconcileRunTaskHandler(
    IServiceScopeFactory scopeFactory,
    ILogger<ReconcileRunTaskHandler> logger) : ITaskHandler<RunReconcilePayload>
{
    /// <inheritdoc />
    public string TaskType => RunReconcileContract.OutboxTaskType;

    /// <inheritdoc />
    public bool RequiresTenant => true;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        RunReconcilePayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (scope is null)
        {
            throw new InvalidOperationException(
                "orchestrator.run.reconcile is tenant-bound: a claimed row without an org_id is dead-lettered as missing_tenant, never run unscoped");
        }

        await using var attemptScope = scopeFactory.CreateAsyncScope();

        var reconciler = attemptScope.ServiceProvider.GetRequiredService<IRunReconciler>();
        var outcome = await reconciler
            .ReconcileAsync(payload.RunId, payload.LateEvidence, payload.ReprocessEvidence, cancellationToken)
            .ConfigureAwait(false);

        // The four values are taken first so the log call's arguments stay locals (CA1873: no work behind a disabled
        // log level). They are numbers and one interned token; the payload is never among them.
        var comparison = outcome.Comparison;
        var disposition = DispositionName(outcome.Disposition);

        // A trigger naming a run this tenant cannot see is the one outcome an operator must notice: it is logged at
        // Warning with the run and the disposition only, so a lost or misfiled task is greppable without reading rows.
        if (outcome.Disposition == ReconcileDisposition.RunNotFound)
        {
            LogRunNotFound(logger, payload.RunId, disposition);
            return;
        }

        var shardsPlanned = comparison?.ShardsPlanned ?? 0;
        var shardsClosed = comparison?.ShardsClosed ?? 0;
        var missingCount = comparison?.Missing.Count ?? 0;

        LogReconciled(logger, payload.RunId, disposition, shardsPlanned, shardsClosed, missingCount);

        // T2.3/T2.4: a compared attempt is finished in the SAME ambient tenant transaction (the reconciler has already
        // locked the run FOR NO KEY UPDATE and its comparison is this attempt's own read). RunNotFound, NotCollectable
        // and Pending reach no finisher at all — nothing to freeze, nothing to append, nothing to transition: a pending
        // attempt waits for the open shard or for the collection deadline, so the run stays collecting. An overdue
        // attempt (queued or running past its deadline) is settled as a forced close; a late attempt (the run already
        // finished as incomplete and new evidence arrived) and a reprocess attempt (a run that already holds a verdict —
        // incomplete, complete or canceled — whose own report was re-read) append a revision and settle no state.
        // An exception here is not caught: it must abort the attempt so the consumer rolls the transaction back and
        // retries it.
        if (comparison is null
            || outcome.Disposition is not (ReconcileDisposition.Compared
                or ReconcileDisposition.ComparedLate
                or ReconcileDisposition.ComparedReprocess
                or ReconcileDisposition.ComparedOverdue))
        {
            return;
        }

        var settlement = outcome.Disposition switch
        {
            ReconcileDisposition.ComparedLate => ReconcileSettlement.AppendLateEvidence,
            ReconcileDisposition.ComparedReprocess => ReconcileSettlement.AppendReprocessEvidence,
            ReconcileDisposition.ComparedOverdue => ReconcileSettlement.CloseOverdue,
            _ => ReconcileSettlement.DecideCollection,
        };

        var finisher = attemptScope.ServiceProvider.GetRequiredService<IReconcileFinisher>();
        var finalization = await finisher
            .FinishAsync(payload.RunId, comparison, settlement, cancellationToken)
            .ConfigureAwait(false);

        LogFinalized(logger, payload.RunId, finalization.Completeness, finalization.RevisionNo);

        // Only a settlement that decides the run feeds the rate: a late or reprocess attempt appends a revision (or
        // answers with the one its evidence already has) and settles no state, so it is not the decision the rate
        // describes. The measurement waits for the commit this handler does not own, so a rolled-back attempt
        // records nothing.
        if (settlement is ReconcileSettlement.DecideCollection or ReconcileSettlement.CloseOverdue
            && string.Equals(finalization.Completeness, ReconcileFinisher.Incomplete, StringComparison.Ordinal))
        {
            scope.AfterCommit(EvidgateTelemetry.RecordRunIncomplete);
        }
    }

    /// <summary>
    /// The disposition as the stable lower-case token the log line carries (a string rather than a boxed enum, so the
    /// line stays greppable whatever the sink does with enums).
    /// </summary>
    /// <param name="disposition">The attempt's outcome.</param>
    private static string DispositionName(ReconcileDisposition disposition) => disposition switch
    {
        ReconcileDisposition.RunNotFound => "run_not_found",
        ReconcileDisposition.NotCollectable => "not_collectable",
        ReconcileDisposition.Pending => "pending",
        ReconcileDisposition.Compared => "compared",
        ReconcileDisposition.ComparedLate => "compared_late",
        ReconcileDisposition.ComparedReprocess => "compared_reprocess",
        ReconcileDisposition.ReprocessAwaitsCollection => "reprocess_awaits_collection",
        ReconcileDisposition.ComparedOverdue => "compared_overdue",
        _ => "unknown",
    };

    /// <summary>
    /// The one line per attempt: the run, what the attempt did and the three counts. Never the payload (no producer id,
    /// no declared pair) and never an event value (00 §5.7 S3).
    /// </summary>
    [LoggerMessage(
        EventId = 2422,
        Level = LogLevel.Information,
        Message = "reconciled run {RunId}: {Disposition}, {ShardsPlanned} shard(s) planned, {ShardsClosed} closed, {MissingCount} missing reason(s)")]
    private static partial void LogReconciled(
        ILogger logger,
        Guid runId,
        string disposition,
        int shardsPlanned,
        int shardsClosed,
        int missingCount);

    /// <summary>
    /// The one extra line a finished attempt writes: the completeness the revision persisted and its <c>revision_no</c>.
    /// Numbers and one interned token only — never the payload, never a missing code list, never an artifact identity
    /// (00 §5.7 S3).
    /// </summary>
    [LoggerMessage(
        EventId = 2424,
        Level = LogLevel.Information,
        Message = "finalized run {RunId}: evidence revision {RevisionNo} is {Completeness}")]
    private static partial void LogFinalized(
        ILogger logger,
        Guid runId,
        string completeness,
        int revisionNo);

    /// <summary>
    /// The run of this trigger is not visible in the caller's tenant: the attempt compared nothing and changed nothing,
    /// but a row naming an invisible run is evidence worth a Warning rather than a routine completion. Fields: the run
    /// and the disposition, never the payload (00 §5.7 S3).
    /// </summary>
    [LoggerMessage(
        EventId = 2423,
        Level = LogLevel.Warning,
        Message = "reconcile found no run {RunId} in this tenant: {Disposition}, nothing compared")]
    private static partial void LogRunNotFound(
        ILogger logger,
        Guid runId,
        string disposition);
}
