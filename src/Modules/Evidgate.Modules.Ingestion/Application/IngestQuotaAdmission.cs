using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Domain.Events;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Ingestion.Application;

/// <summary>
/// The events route's quota admission: one <see cref="IQuotaGate"/> call per batch, never per event (ruling
/// T53), after the body is parsed and canonicalised and before the run row is read, so the reservation is
/// ordered before every lock the batch takes and is rolled back with the batch's own transaction when a later
/// step refuses it.
/// <para>
/// The reserved quantity is the unit the ledger bills: one <c>results</c> unit per event the batch will newly
/// commit. Events already on file with the identical canonical payload are not reserved, so a resend of a
/// committed batch reserves nothing and is answered with its per-event <c>duplicate</c> acks instead of being
/// charged twice; events this batch goes on to refuse (a different payload, a reused sequence) keep their
/// reservation only until the batch's transaction rolls back.
/// </para>
/// <para>
/// A block refuses the batch, which is what a committed upload never sees: the gate runs inside the new
/// batch's transaction, so evidence that is already committed stays committed. An exception is a failure of
/// the admission itself and propagates: the batch is refused by the host's 500 <c>internal_error</c> envelope,
/// never admitted unmetered.
/// </para>
/// </summary>
internal sealed class IngestQuotaAdmission(
    IQuotaGate gate,
    ITenantDb db,
    IngestQuotaVeto veto,
    TimeProvider time)
{
    /// <summary>
    /// Reserves the batch's metered quantity, or answers the refusal that keeps the batch from committing.
    /// </summary>
    /// <param name="orgId">The caller's tenant, also the <c>event</c> index scope of the replay probe.</param>
    /// <param name="projectId">The project the token scopes the batch to, whose counter it is charged to.</param>
    /// <param name="runId">The run the batch was posted to.</param>
    /// <param name="events">The parsed batch, in request order.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Null when the batch may proceed; the refusal to answer instead when it may not commit.</returns>
    internal async Task<Error?> AdmitAsync(
        Guid orgId,
        Guid? projectId,
        Guid runId,
        IReadOnlyList<ParsedEvent> events,
        CancellationToken cancellationToken)
    {
        // The counter is one project's; event:write is a service-only permission whose token always carries a
        // project scope, so a caller without one is refused here rather than admitted unmetered.
        if (projectId is not { } scopedProject)
        {
            return Error.Forbidden("the events route requires a project-scoped producer token");
        }

        // An event_id repeated inside the batch is one committed event, so the batch's distinct ids are the
        // units the ledger will bill; the split by payload is decided later, by the insert and its read-back.
        var distinct = events.DistinctBy(e => e.EventId).ToList();
        var stored = await IngestEventReads
            .ReadStoredAsync(db, orgId, runId, distinct.Select(e => e.EventId), cancellationToken)
            .ConfigureAwait(false);

        var fresh = distinct.Count(e =>
            !stored.TryGetValue(e.EventId, out var hash) || !string.Equals(hash, e.PayloadHash, StringComparison.Ordinal));

        if (fresh == 0)
        {
            // A resend of a committed batch: the ledger bills nothing for it (accepted = 0 emits no usage
            // row), so charging the period here would be a double count against a bill that never moves.
            return null;
        }

        // The billing month of the admitted work, in the UTC form the counter and the ledger both store. The
        // batch commits milliseconds later, so a reservation made across a month boundary can land one month
        // before its ledger row; that window is one instant and the two months carry the same limit.
        var period = time.GetUtcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var answer = await gate
            .CheckAsync(new QuotaCheckRequest(UsageMetric.Results, period, scopedProject, fresh), cancellationToken)
            .ConfigureAwait(false);

        if (answer.Decision != QuotaDecision.Block)
        {
            return null;
        }

        EvidgateTelemetry.RecordIngestQuotaDenied(IngestBatchRejection.QuotaExceeded);

        // The operator raise route the gate names is not published to a tenant: only the wire code and the
        // retry wait leave here, and the envelope stays the one the burst refusal already uses.
        if (answer.RetryAfter is { } retryAfter)
        {
            veto.Refuse(retryAfter);
        }

        return new Error(
            ApiErrorCode.QuotaExceeded,
            "the project's ingest quota for the billing period is reached",
            StatusCodes.Status429TooManyRequests,
            Retryable: true);
    }
}
