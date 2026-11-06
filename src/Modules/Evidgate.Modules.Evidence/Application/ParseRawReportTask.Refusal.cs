using System.Globalization;
using Evidgate.Modules.Evidence.Application.Freeze;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The refusal half of <see cref="ParseRawReportHandler"/>: one <c>raw_report</c> row with outcome <c>malformed</c> or
/// <c>unsupported</c> and, in that row's own transaction, the reconcile trigger the missing evidence needs. No event,
/// slot or receipt is written, so the row and its trigger are the whole of a refusal.
/// </summary>
internal sealed partial class ParseRawReportHandler
{
    /// <summary>
    /// The one write of a refused report's row, in its own short transaction: a refusal produces no event and no slot,
    /// and the trigger it leaves names the run so the freeze path reads the report as missing evidence instead of
    /// waiting for a finalize a synthetic producer can never send. The row is written for a report that is still this
    /// run's <c>ready</c> raw report, and for one its own <c>artifact_parse</c> verdict rejected (see
    /// <see cref="IsParseRejected"/>); any other artifact state stores nothing.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="outcome">One of <see cref="RawReportOutcomes"/>: <c>malformed</c> or <c>unsupported</c>.</param>
    /// <param name="reason">The code word the parse reported, kept as it is when the column admits it.</param>
    /// <param name="parserId">The registry's <c>parser_id</c>, or null when no parser was resolved.</param>
    /// <param name="parserVersion">The registry's <c>version</c>, or null when no parser was resolved.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task WriteRefusedAsync(
        Guid orgId,
        ParseRawReportPayload payload,
        string outcome,
        string? reason,
        string? parserId,
        string? parserVersion,
        CancellationToken cancellationToken)
    {
        await using var scope = await scopes
            .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
            .ConfigureAwait(false);
        await using var context = contexts.Create();

        if (!await IsRefusableAsync(context, payload, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (!RawReportProducerKindShape.IsStorable(payload.StoredProducerKind))
        {
            // The column admits only the registry's own kind vocabulary: a payload whose kind cannot be stored (a
            // hand-written one, never the lookup's own output) gets no row rather than failing the refusal write. The
            // skip is loud because the report's durable refusal is then only the task's outcome; no kind text is logged.
            LogUnstorableProducerKind(logger, payload.ArtifactId);
            return;
        }

        // The row's id is minted here, before the insert, so the trigger of this refusal can name the very row it
        // accompanies: one artifact may hold several refusals (an unsupported verdict, then a malformed one under a
        // parser), and each needs a trigger of its own.
        var rawReportId = Guid.CreateVersion7();

        // A row that already holds this key is an earlier verdict: the conflict is a silent no-op, because a refusal may
        // never overwrite one, a parsed report's row must never be joined by a refusal, and a replay writes no trigger.
        if (await InsertRawReportAsync(
                orgId,
                payload,
                RefusedReport(outcome, reason, parserId, parserVersion),
                rawReportId,
                cancellationToken)
            .ConfigureAwait(false))
        {
            await EnqueueRefusalTriggerAsync(payload, rawReportId, cancellationToken).ConfigureAwait(false);
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether this artifact may carry a refusal row at all: it is this run's raw report and it is either still
    /// <c>ready</c> or was rejected by its own parse verdict. A report that left <c>ready</c> for any other reason is out
    /// of evidence — an upload-reason rejection means no report was ever validly uploaded — so it gets no row.
    /// </summary>
    /// <param name="context">The tenant write model the artifact is read through.</param>
    /// <param name="payload">The run and artifact the refusal names.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>True when the refusal row may be written for this artifact.</returns>
    private async Task<bool> IsRefusableAsync(
        EvidenceDbContext context,
        ParseRawReportPayload payload,
        CancellationToken cancellationToken)
    {
        var artifact = await context.Artifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == payload.ArtifactId, cancellationToken)
            .ConfigureAwait(false);

        if (artifact is not null
            && string.Equals(artifact.Kind, ArtifactKind.RawReport, StringComparison.Ordinal)
            && artifact.RunId == payload.RunId
            && (string.Equals(artifact.State, ArtifactState.Ready, StringComparison.Ordinal)
                || IsParseRejected(artifact)))
        {
            return true;
        }

        LogSkipped(logger, payload.ArtifactId, "left_ready");
        return false;
    }

    /// <summary>
    /// The one reconcile trigger of a refusal: the same run and the same synthetic producer the mapped path's slot
    /// names, so the reconciler compares what the run really stored. Its key names the row this trigger accompanies
    /// (<see cref="RunReconcileContract.BatchIdempotencyKey"/>): one artifact may hold several refusals, each committed
    /// with its own trigger, while a replay writes no row and so no trigger.
    /// </summary>
    /// <param name="payload">The run and artifact the refusal names.</param>
    /// <param name="rawReportId">The refusal row this trigger accompanies, and the row id its key names.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task EnqueueRefusalTriggerAsync(
        ParseRawReportPayload payload,
        Guid rawReportId,
        CancellationToken cancellationToken)
    {
        // The same synthetic producer the mapped path's slot names (see WriteMappedAsync), so the reconciler compares
        // one report by one identity whether it mapped or refused.
        var producerId = ProducerPrefix + payload.ArtifactId.ToString("D", CultureInfo.InvariantCulture);

        await outbox.EnqueueAsync(
                RunReconcileContract.OutboxTaskType,
                RunReconcileContract.PayloadJson(payload.RunId, producerId, declaredLastSequence: 0, declaredCount: 0),
                RunReconcileContract.AggregateType,
                payload.RunId,
                RunReconcileContract.BatchIdempotencyKey(payload.RunId, producerId, rawReportId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a refusal's one row and answers the named outcome the caller returns unchanged. The row's outcome and the
    /// task's outcome are the same word for a refusal, so they cannot drift.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="outcome">One of <see cref="RawReportOutcomes"/>: <c>malformed</c> or <c>unsupported</c>.</param>
    /// <param name="reason">The code word the parse reported.</param>
    /// <param name="parserId">The registry's <c>parser_id</c>, or null when no parser was resolved.</param>
    /// <param name="parserVersion">The registry's <c>version</c>, or null when no parser was resolved.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The named outcome of the attempt.</returns>
    private async Task<ParseRawReportOutcome> RefuseAsync(
        Guid orgId,
        ParseRawReportPayload payload,
        string outcome,
        string? reason,
        string? parserId,
        string? parserVersion,
        CancellationToken cancellationToken)
    {
        await WriteRefusedAsync(orgId, payload, outcome, reason, parserId, parserVersion, cancellationToken)
            .ConfigureAwait(false);

        return new ParseRawReportOutcome(outcome, reason);
    }

    /// <summary>
    /// The one malformed refusal of an attempt that already resolved a parser: the row names the release that judged the
    /// document, and the outcome keeps the reason word the parse reported.
    /// </summary>
    /// <param name="prepared">The parser release the refusal is recorded under.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="reason">The code word the parse reported.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The named outcome of the attempt.</returns>
    private Task<ParseRawReportOutcome> RefuseMalformedAsync(
        Prepared prepared,
        Guid orgId,
        ParseRawReportPayload payload,
        string? reason,
        CancellationToken cancellationToken) =>
        RefuseAsync(
            orgId,
            payload,
            RawReportOutcomes.Malformed,
            reason,
            prepared.ParserId,
            prepared.Version,
            cancellationToken);

    /// <summary>
    /// The one unsupported refusal of an attempt the read scope ended: the row names the parser pair the registry
    /// resolved (or none, for a kind it does not know), and a skip writes nothing.
    /// </summary>
    /// <param name="preparation">What the read scope answered, including the pair a refusal row carries.</param>
    /// <param name="stopped">The outcome that ended the attempt.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The named outcome of the attempt.</returns>
    private async Task<ParseRawReportOutcome> RefuseUnsupportedAsync(
        Preparation preparation,
        ParseRawReportOutcome stopped,
        Guid orgId,
        ParseRawReportPayload payload,
        CancellationToken cancellationToken)
    {
        return stopped.IsRefusal
            ? await RefuseAsync(
                    orgId,
                    payload,
                    stopped.Status,
                    stopped.Reason,
                    preparation.ParserId,
                    preparation.ParserVersion,
                    cancellationToken)
                .ConfigureAwait(false)
            : stopped;
    }

    /// <summary>
    /// The row of a report the parse refused: no event, no declared counter and no discrepancy, because nothing was
    /// mapped. The pair is carried only when the registry named one; an unregistered producer kind has none, and the
    /// row's key is then the kind it declared.
    /// </summary>
    /// <param name="outcome">One of <see cref="RawReportOutcomes"/>: <c>malformed</c> or <c>unsupported</c>.</param>
    /// <param name="reason">The code word the parse reported.</param>
    /// <param name="parserId">The registry's <c>parser_id</c>, when one was resolved.</param>
    /// <param name="parserVersion">The registry's <c>version</c>, when one was resolved.</param>
    /// <returns>The row the refusal write inserts.</returns>
    private static RawReportDraft RefusedReport(string outcome, string? reason, string? parserId, string? parserVersion)
    {
        var pairResolved = !string.IsNullOrEmpty(parserId) && !string.IsNullOrEmpty(parserVersion);

        return new RawReportDraft(
            outcome,
            RefusalReason(outcome, reason),
            pairResolved ? parserId : null,
            pairResolved ? parserVersion : null,
            null,
            null,
            null,
            null,
            0,
            0,
            0,
            0,
            EmptyDiscrepancies);
    }

    /// <summary>
    /// The code word a refusal row stores: the parse's own word when it has the column's shape, and otherwise the
    /// outcome's own word. A child- or profile-supplied string therefore never fails the row's CHECK and hides the
    /// refusal instead.
    /// </summary>
    /// <param name="outcome">The refusal outcome, itself a valid code word.</param>
    /// <param name="reason">The parse's reason, possibly null or not a code word.</param>
    /// <returns>A word matching the column's <c>^[a-z][a-z0-9_]{0,63}$</c> shape.</returns>
    private static string RefusalReason(string outcome, string? reason) =>
        reason is not null && EvidenceFreeze.IsReasonCode(reason) ? reason : outcome;
}
