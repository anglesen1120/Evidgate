using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The refusal half of <see cref="ReprocessReportHandler"/>: a document the parser profile or the mapper refused leaves
/// its own row and its reconcile trigger in their own short transaction, and writes no event at all.
/// </summary>
internal sealed partial class ReprocessReportHandler
{
    /// <summary>
    /// The one malformed refusal of an attempt that already resolved the pinned release: the row names the release that
    /// judged the document, the trigger names the row, and the outcome keeps the reason word the parse reported.
    /// </summary>
    /// <param name="prepared">The pinned release and the request the refusal is recorded under.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="reason">The code word the parse reported.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The named outcome of the attempt.</returns>
    private async Task<ParseRawReportOutcome> RefuseMalformedAsync(
        Prepared prepared,
        Guid orgId,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var scope = await scopes
            .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
            .ConfigureAwait(false);
        await using var context = contexts.Create();

        if (!await IsRefusableAsync(context, prepared, cancellationToken).ConfigureAwait(false))
        {
            return new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, ArtifactLeftReady);
        }

        if (await InsertRawReportAsync(orgId, prepared, RefusedReport(prepared, reason), cancellationToken)
            .ConfigureAwait(false))
        {
            await EnqueueReconcileAsync(
                    prepared,
                    declaredLastSequence: 0,
                    declaredCount: 0,
                    late: false,
                    afterCancel: false,
                    Guid.CreateVersion7(),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new ParseRawReportOutcome(ParseRawReportOutcome.Malformed, reason);
    }

    /// <summary>
    /// Whether this artifact may carry a refusal row at all: it is this run's raw report and it is still <c>ready</c>. A
    /// report that left <c>ready</c> while the document was parsed is out of evidence, so it gets no row.
    /// </summary>
    /// <param name="context">The tenant write model the artifact is read through.</param>
    /// <param name="prepared">The run and the artifact the refusal names.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>True when the refusal row may be written for this artifact.</returns>
    private async Task<bool> IsRefusableAsync(
        EvidenceDbContext context,
        Prepared prepared,
        CancellationToken cancellationToken)
    {
        var artifact = await context.Artifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == prepared.ArtifactId, cancellationToken)
            .ConfigureAwait(false);

        if (artifact is not null
            && string.Equals(artifact.Kind, ArtifactKind.RawReport, StringComparison.Ordinal)
            && artifact.RunId == prepared.RunId
            && string.Equals(artifact.State, ArtifactState.Ready, StringComparison.Ordinal))
        {
            return true;
        }

        LogSkipped(logger, prepared.ParsingId, ArtifactLeftReady);
        return false;
    }

    /// <summary>The trigger of a mapped attempt, named by the set's own declaration.</summary>
    /// <param name="prepared">The request and its synthetic producer.</param>
    /// <param name="events">The set whose declaration the trigger names.</param>
    /// <param name="late">True when this evidence arrived after the run finished incomplete.</param>
    /// <param name="afterCancel">True when the run was already canceled, so this is its post-cancel evidence.</param>
    /// <param name="batchId">The accepted set's own idempotency id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The enqueue.</returns>
    private Task EnqueueReconcileAsync(
        Prepared prepared,
        SerializedEventSet events,
        bool late,
        bool afterCancel,
        Guid batchId,
        CancellationToken cancellationToken) =>
        EnqueueReconcileAsync(
            prepared, events.MaxSequence, events.DeclaredCount, late, afterCancel, batchId, cancellationToken);

    /// <summary>
    /// Enqueues the one reconcile trigger this attempt's rows need: the reprocess shape for a run that was canceled before
    /// the re-read rows were admitted, the late-evidence shape for a run that already finished as incomplete, and the
    /// ordinary slot-closed shape for every other run — and for a refusal, which stores no event, so no freeze path can
    /// see it. Every shape carries the new evidence to the freeze path without waiting for a finalize a synthetic
    /// producer can never trigger; only the canceled one needs a marker, because a canceled run is otherwise never
    /// compared and its evidence would never reach a revision.
    /// </summary>
    /// <param name="prepared">The request and its synthetic producer.</param>
    /// <param name="declaredLastSequence">The set's declared high-water mark (0 for a refusal).</param>
    /// <param name="declaredCount">The set's declared count (0 for a refusal).</param>
    /// <param name="late">True when this evidence arrived after the run finished incomplete.</param>
    /// <param name="afterCancel">True when the run was already canceled, so this is its post-cancel evidence.</param>
    /// <param name="batchId">The accepted set's own idempotency id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The enqueue.</returns>
    private async Task EnqueueReconcileAsync(
        Prepared prepared,
        long declaredLastSequence,
        int declaredCount,
        bool late,
        bool afterCancel,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        if (afterCancel)
        {
            await outbox.EnqueueAsync(
                    RunReconcileContract.OutboxTaskType,
                    RunReconcileContract.ReprocessPayloadJson(prepared.RunId, prepared.ProducerId),
                    RunReconcileContract.AggregateType,
                    prepared.RunId,
                    RunReconcileContract.ReprocessBatchIdempotencyKey(prepared.RunId, prepared.ProducerId, batchId),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (late)
        {
            await outbox.EnqueueAsync(
                    RunReconcileContract.OutboxTaskType,
                    RunReconcileContract.LatePayloadJson(prepared.RunId, prepared.ProducerId),
                    RunReconcileContract.AggregateType,
                    prepared.RunId,
                    RunReconcileContract.LateBatchIdempotencyKey(prepared.RunId, prepared.ProducerId, batchId),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await outbox.EnqueueAsync(
                RunReconcileContract.OutboxTaskType,
                RunReconcileContract.PayloadJson(prepared.RunId, prepared.ProducerId, declaredLastSequence, declaredCount),
                RunReconcileContract.AggregateType,
                prepared.RunId,
                RunReconcileContract.BatchIdempotencyKey(prepared.RunId, prepared.ProducerId, batchId),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
