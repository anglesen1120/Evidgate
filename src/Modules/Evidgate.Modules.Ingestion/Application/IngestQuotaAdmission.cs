using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Ingestion.Application;

/// <summary>
/// The events route's quota admission: one <see cref="IQuotaGate"/> decision per batch, never per event (ruling
/// T53), made after the batch's insert has written its rows and answered how many units the batch adds.
/// <para>
/// The reserved quantity is the unit the ledger bills: the rows this batch's insert actually added, so two
/// identical racing batches reserve the batch once between them (the loser's insert adds nothing and is
/// answered with duplicate acks instead), and a resend of a committed batch reserves nothing at all.
/// </para>
/// <para>
/// The call charges the organization's counter as well as the project's, so an org-wide limit applies to a batch a
/// single project commits; the gate admits the batch only when both have room and charges both or neither.
/// </para>
/// <para>
/// A block refuses the batch: the handler returns a failure, so the tenant transaction rolls the inserted
/// events back with everything else the batch wrote and answers 429. The counter's row locks are therefore held
/// only from this call to the end of the transaction, after every read and insert of the batch. An exception is
/// a failure of the admission itself and propagates: the batch is refused by the host's 500
/// <c>internal_error</c> envelope, never admitted unmetered.
/// </para>
/// </summary>
internal sealed class IngestQuotaAdmission(
    IQuotaGate gate,
    IngestQuotaVeto veto,
    ITenantScopeAccessor scopes)
{
    /// <summary>
    /// Reserves <paramref name="quantity"/> units on the project's counter and the organization's, or answers the
    /// refusal that keeps the batch from committing.
    /// </summary>
    /// <param name="projectId">The project the token scopes the batch to, whose counter it is charged to.</param>
    /// <param name="quantity">The rows this batch's insert added; zero reserves nothing and asks nobody.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Null when the batch may commit; the refusal to answer instead when it may not.</returns>
    internal async Task<Error?> AdmitAsync(
        Guid? projectId,
        int quantity,
        CancellationToken cancellationToken)
    {
        // The counter is one project's; event:write is a service-only permission whose token always carries a
        // project scope, so a caller without one is refused here rather than admitted unmetered.
        if (projectId is not { } scopedProject)
        {
            return Error.Forbidden("the events route requires a project-scoped producer token");
        }

        if (quantity <= 0)
        {
            // Nothing was added, so nothing is billed and the counter is never touched: a resend is answered
            // by its duplicate acks.
            return null;
        }

        // The billing month of the admitted work, read from the transaction's own clock: the instant that stamps the
        // batch's rows is the one the ledger row metering them carries, so a batch never passes the gate in one month
        // and lands in the ledger of the next.
        var period = await gate.CurrentPeriodAsync(cancellationToken).ConfigureAwait(false);
        var request = new QuotaCheckRequest(
            UsageMetric.Results, period, scopedProject, quantity, IncludeTenantScope: true);
        var answer = await gate.CheckAsync(request, cancellationToken).ConfigureAwait(false);

        if (answer.Decision != QuotaDecision.Block)
        {
            return null;
        }

        EvidgateTelemetry.RecordIngestQuotaDenied(IngestBatchRejection.QuotaExceeded);

        // The failure rolls the batch back, and the gate's refusal stamp with it, so the question travels out to the
        // endpoint, which writes the stamp in a scope of its own once the transaction is gone.
        veto.Refuse(scopes.Require().OrgId, request, answer.RetryAfter);

        // The operator raise route the gate names is not published to a tenant: only the wire code and the
        // retry wait leave here, and the envelope stays the one the burst refusal already uses.
        return new Error(
            ApiErrorCode.QuotaExceeded,
            "the ingest quota for the billing period is reached",
            StatusCodes.Status429TooManyRequests,
            Retryable: true);
    }
}
