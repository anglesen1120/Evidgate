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
            await EnqueueReconcileAsync(prepared, Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Enqueues the one reconcile trigger this attempt's rows need. Every trigger this task writes carries the reprocess
    /// marker whatever state the run was in when the rows were admitted: the trigger is the only thing that compares a
    /// finished run or a live one that already holds a revision, and without the marker the queue's ordinary shapes would
    /// settle the run instead of appending to it. The declaration fields stay at zero because the reconciler re-reads the
    /// slot's stored declaration under its own lock.
    /// </summary>
    /// <param name="prepared">The request and its synthetic producer.</param>
    /// <param name="batchId">This attempt's own identity, so two attempts never share a trigger key.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The enqueue.</returns>
    private async Task EnqueueReconcileAsync(
        Prepared prepared,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        await outbox.EnqueueAsync(
                RunReconcileContract.OutboxTaskType,
                RunReconcileContract.ReprocessPayloadJson(prepared.RunId, prepared.ProducerId),
                RunReconcileContract.AggregateType,
                prepared.RunId,
                RunReconcileContract.ReprocessBatchIdempotencyKey(prepared.RunId, prepared.ProducerId, batchId),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
