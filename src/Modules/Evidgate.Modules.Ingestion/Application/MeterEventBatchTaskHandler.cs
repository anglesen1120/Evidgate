using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Ingestion.Application.Commands;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Ingestion.Application;

/// <summary>
/// Payload of the <c>usage.batch_accepted</c> task: one accepted batch and the metric it is metered under, as the
/// ingest commit's outbox row writes it.
/// </summary>
/// <param name="EventBatchId">The accepted batch the ledger row bills.</param>
/// <param name="Metric">The wire metric name; only the accepted-event metric exists in V1.</param>
/// <param name="Quantity">The accepted count the producer observed; re-read and compared, never trusted.</param>
internal sealed record MeterEventBatchPayload(Guid EventBatchId, string Metric, int Quantity);

/// <summary>The metering source of one accepted batch, read back inside the attempt's tenant scope.</summary>
/// <param name="RunId">The run the batch was posted to, from which the project is resolved through the Orchestrator.</param>
/// <param name="Accepted">The authoritative count of events the batch committed.</param>
/// <param name="CreatedAt">The batch's commit instant: the metered work's time and the billing period's month.</param>
internal sealed record MeterEventBatch(Guid RunId, int Accepted, DateTimeOffset CreatedAt);

/// <summary>The RLS-scoped read of <see cref="MeterEventBatch"/>; another org's batch is not a row at all.</summary>
internal interface IMeterEventBatchReader
{
    /// <summary>Reads one batch of <paramref name="orgId"/>'s tenant, or null when it is not there.</summary>
    Task<MeterEventBatch?> ReadAsync(Guid orgId, Guid batchId, CancellationToken cancellationToken);
}

/// <summary>
/// Meters one accepted ingest batch into the usage ledger: one row per batch per metric, keyed
/// <c>batch:&lt;event_batch_id&gt;:&lt;metric&gt;</c> so a resend or a retried attempt cannot bill twice. Tenant-bound:
/// the batch is re-read on the claim's scope and its own accepted count is the quantity, so a payload cannot inflate
/// a bill. A batch metered for a month that is already billed is recorded in the next open period and audited, so late
/// metering is billed rather than dead-lettered. Every other failure throws and the queue's retry cap dead-letters the
/// row; a duplicate key is a success, and a key that already holds a different payload is logged at Error and also
/// completes the task, because the ledger's conflict is permanent and a throw would only requeue it.
/// </summary>
/// <param name="scopeFactory">Creates the per-attempt scope the scoped reader, run lookup and ledger are resolved from.</param>
/// <param name="logger">Records a billing-key conflict, which the ledger reports instead of billing.</param>
internal sealed partial class MeterEventBatchTaskHandler(
    IServiceScopeFactory scopeFactory,
    ILogger<MeterEventBatchTaskHandler> logger)
    : ITaskHandler<MeterEventBatchPayload>
{
    /// <summary>The ledger unit of one metered batch (<c>results</c>, one per accepted batch).</summary>
    private const string BatchUnit = "batch";

    /// <inheritdoc />
    public string TaskType => IngestEventBatchHandler.UsageTaskType;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        MeterEventBatchPayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (scope is null)
        {
            throw new InvalidOperationException(
                "usage.batch_accepted is tenant-bound: a claimed row without an org_id is dead-lettered as missing_tenant, never run unscoped");
        }

        if (!string.Equals(payload.Metric, IngestEventBatchHandler.UsageMetric, StringComparison.Ordinal))
        {
            // The payload's metric is untrusted text: the message names no value from it, only the fact.
            throw new InvalidOperationException(
                "the usage.batch_accepted payload names a metric this build does not meter");
        }

        // Task handlers are singletons, so the scoped services are resolved from this attempt's scope; the tenant scope
        // itself stays the consumer's and the ledger joins its transaction.
        await using var attempt = scopeFactory.CreateAsyncScope();
        var batch = await attempt.ServiceProvider.GetRequiredService<IMeterEventBatchReader>()
            .ReadAsync(scope.OrgId, payload.EventBatchId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the event batch {payload.EventBatchId} is not visible to this tenant");

        if (batch.Accepted <= 0 || payload.Quantity != batch.Accepted)
        {
            throw new InvalidOperationException(
                $"the event batch {payload.EventBatchId} metered {payload.Quantity} against {batch.Accepted} accepted events");
        }

        var run = await attempt.ServiceProvider.GetRequiredService<IRunEventScope>()
            .ReadAsync(batch.RunId, [], cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"the run of event batch {payload.EventBatchId} is not visible to this tenant");

        var occurredAt = batch.CreatedAt.ToUniversalTime();
        var entry = new UsageEntryInput(
            $"batch:{payload.EventBatchId:D}:{payload.Metric}",
            UsageMetric.Results,
            run.ProjectId,
            batch.Accepted,
            BatchUnit,
            occurredAt.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            occurredAt);

        var ledger = attempt.ServiceProvider.GetRequiredService<IUsageLedger>();

        // One call per metric (V1 has one). A duplicate key is this task's own replay — the ledger compared the stored
        // payload with the one built here — so it is a silent success. A conflicting key is permanent: the ledger wrote
        // nothing, so this attempt is logged and completes instead of being requeued for the same refusal.
        var result = await ledger
            .RecordAsync(new RecordUsageBatchRequest([entry]), cancellationToken)
            .ConfigureAwait(false);

        if (result.Conflicts.Contains(entry.BillingKey, StringComparer.Ordinal))
        {
            LogBillingKeyConflict(payload.EventBatchId, scope.OrgId);
        }
    }

    [LoggerMessage(
        EventId = 4112,
        Level = LogLevel.Error,
        Message = "Usage ledger billing key conflict for event batch {EventBatchId} in org {Org}: the key already holds a different payload, so the batch was not billed and this task completes")]
    private partial void LogBillingKeyConflict(Guid eventBatchId, Guid org);
}
