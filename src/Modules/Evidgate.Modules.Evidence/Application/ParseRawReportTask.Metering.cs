using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The metering half of <see cref="ParseRawReportHandler"/>: the run's first accepted report moves it on, and the events
/// one attempt really inserted become one batch receipt plus one usage outbox row inside the evidence's transaction.
/// Kept beside the write half so the transaction's statement order stays readable in one file.
/// </summary>
internal sealed partial class ParseRawReportHandler
{
    /// <summary>The run state whose first evidence moves it on (the event batch route's own edge).</summary>
    private const string QueuedState = "queued";

    /// <summary>The run state the first accepted evidence of a queued run moves it to.</summary>
    private const string RunningState = "running";

    /// <summary>The prefix of the batch receipt's request id; the report artifact makes it unique per report.</summary>
    private const string RequestIdPrefix = "raw-report-parse:";

    /// <summary>
    /// The run's state, read without a lock. It only decides whether the transition is needed at all, so a replay on a
    /// running run keeps the single locking read the admission takes; the transition re-checks the state under its own
    /// lock, so a stale answer here is harmless.
    /// </summary>
    private const string ReadRunStateSql = """
        select state from public.run where org_id = @org_id and id = @run_id
        """;

    /// <summary>
    /// The batch's own audit row, in the ingest route's statement shape: the receipt the response's
    /// <c>batch_id</c> names there is here the receipt the usage row meters.
    /// </summary>
    private const string InsertBatchSql = """
        insert into public.event_batch (
            id, org_id, run_id, producer_id, event_count, bytes_in, bytes_expanded, accepted,
            duplicate, request_id)
        values (
            @id, @org_id, @run_id, @producer_id, @event_count, @bytes_in, @bytes_expanded, @accepted,
            @duplicate, @request_id)
        """;

    /// <summary>
    /// The transaction's lock order, first: the transition takes the run row <c>FOR NO KEY UPDATE</c>, and the admission
    /// read that follows takes the same row the same way. Because two callers that share-lock a row and then upgrade
    /// deadlock (40P01), and because this handler's admission read is the stronger lock itself, the transition must run
    /// before it. A run that is not <c>queued</c> is never re-transitioned, so a replay or a report on a running,
    /// collecting or terminal run writes no state change, no outbox row and no audit row.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="cancellationToken">Cancels the read and the transition.</param>
    /// <returns>The skipped outcome that ended the attempt, or null when the run may be written.</returns>
    private async Task<ParseRawReportOutcome?> StartRunAsync(
        Guid orgId,
        ParseRawReportPayload payload,
        CancellationToken cancellationToken)
    {
        var state = await db
            .ScalarAsync<string?>(ReadRunStateSql, new { org_id = orgId, run_id = payload.RunId }, cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(state, QueuedState, StringComparison.Ordinal))
        {
            // Already running (or collecting/terminal): nothing to move. An absent run is answered by the admission
            // read, which names it the same way, so this read stays silent about it.
            return null;
        }

        // The transition is a scoped service while a task handler is a singleton, so it comes from this attempt's own
        // scope; it joins the ambient tenant transaction and never commits.
        await using var transitionScope = scopeFactory.CreateAsyncScope();
        var transition = await transitionScope.ServiceProvider
            .GetRequiredService<IRunStateTransition>()
            .TransitionAsync(new TransitionRunStateCommand(payload.RunId, RunningState), cancellationToken)
            .ConfigureAwait(false);

        return transition.IsSuccess
            ? null
            // The only reachable failure of this edge is 404: the run is not visible to this tenant, which the admission
            // read answers with the same named outcome. A no-op (a report that lost the race) is a success.
            : new ParseRawReportOutcome(ParseRawReportOutcome.Skipped, "run_absent");
    }

    /// <summary>
    /// The events this attempt really inserted become one batch receipt and one usage task, in the evidence's own
    /// transaction: a replay inserted nothing, so it adds neither, and a report that mapped nothing meters nothing. A
    /// receipt is therefore written exactly when evidence was, and the count it states is what the ledger bills.
    /// </summary>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="payload">The run, artifact and producer kind.</param>
    /// <param name="producerId">The synthetic producer id of this report.</param>
    /// <param name="reportBytes">The stored report's size, the only honest measure of the bytes this batch was built from.</param>
    /// <param name="events">The serialized set this attempt built (its length is the batch's event count).</param>
    /// <param name="inserted">The ids this attempt inserted; a duplicate is every other event of the set.</param>
    /// <param name="batchId">The accepted batch's id, minted with the reconcile trigger so the two rows agree.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    private async Task MeterBatchAsync(
        Guid orgId,
        ParseRawReportPayload payload,
        string producerId,
        long reportBytes,
        SerializedEventSet events,
        HashSet<Guid> inserted,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        if (inserted.Count == 0)
        {
            // Nothing new to account for: the stored rows already belong to the batch that inserted them, and a second
            // receipt would bill the same evidence twice.
            return;
        }

        await db.ExecuteAsync(
                InsertBatchSql,
                new
                {
                    id = batchId,
                    org_id = orgId,
                    run_id = payload.RunId,
                    producer_id = producerId,
                    event_count = events.Events.Length,
                    bytes_in = reportBytes,
                    // One stored object read as it is: the report is never inflated here, so both sizes agree.
                    bytes_expanded = reportBytes,
                    accepted = inserted.Count,
                    duplicate = events.Events.Length - inserted.Count,
                    // The artifact names the request, so the same report can only ever be receipted under one id: a
                    // second delivery that did insert nothing adds no batch, and this keeps the two traceable together.
                    request_id = RequestIdPrefix + payload.ArtifactId.ToString("D", CultureInfo.InvariantCulture),
                },
                cancellationToken)
            .ConfigureAwait(false);

        // The ingest route's usage row, written with the same task type, payload shape, aggregate and idempotency key:
        // one row per accepted batch per metric, so a retried task (or a redelivered report) bills nothing twice.
        await outbox.EnqueueAsync(
                UsageBatchAcceptedContract.TaskType,
                UsageBatchAcceptedContract.PayloadJson(batchId, inserted.Count),
                UsageBatchAcceptedContract.AggregateType,
                batchId,
                UsageBatchAcceptedContract.IdempotencyKey(batchId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The attempt's one commit: the audit entries the transition recorded are appended to the org's chain inside this
    /// transaction first, so a transition that rolls back leaves no audit row and a committed one leaves exactly one.
    /// </summary>
    /// <param name="scope">The write scope this attempt opened.</param>
    /// <param name="orgId">The tenant the claimed task row names.</param>
    /// <param name="cancellationToken">Cancels the flush and the commit.</param>
    private async Task CommitAttemptAsync(ITenantScope scope, Guid orgId, CancellationToken cancellationToken)
    {
        await audit.FlushAsync(orgId, db, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
