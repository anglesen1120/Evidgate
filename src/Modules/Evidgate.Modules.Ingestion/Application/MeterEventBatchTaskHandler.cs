using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Ingestion.Application.Commands;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;

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
/// a bill. Every failure throws and the queue's retry cap dead-letters the row; a duplicate key is a success.
/// </summary>
/// <param name="scopeFactory">Creates the per-attempt scope the scoped reader, run lookup and ledger are resolved from.</param>
internal sealed class MeterEventBatchTaskHandler(IServiceScopeFactory scopeFactory)
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

        // One call per metric (V1 has one). A key the ledger reports as a duplicate is either this task's own replay —
        // the stored row equals the one built here — or a row this batch never wrote, which must never be rewritten.
        var result = await ledger
            .RecordAsync(new RecordUsageBatchRequest([entry]), cancellationToken)
            .ConfigureAwait(false);

        if (result.Duplicates.Contains(entry.BillingKey, StringComparer.Ordinal))
        {
            await EnsureDuplicateIsThisBatchAsync(ledger, payload.EventBatchId, entry, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Compares the stored row of a duplicate key with the entry this attempt built. A missing row (the ledger just
    /// reported it, so this is a contract failure) or a different metric, project, quantity, unit or period means the
    /// batch was billed as something else: the attempt throws, so the queue retries and dead-letters the row instead of
    /// accepting a wrong bill. The message names the batch and the differing fields, never their values.
    /// </summary>
    private static async Task EnsureDuplicateIsThisBatchAsync(
        IUsageLedger ledger,
        Guid eventBatchId,
        UsageEntryInput entry,
        CancellationToken cancellationToken)
    {
        var stored = await ledger.FindAsync(entry.BillingKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the usage ledger reported the billing key of batch {eventBatchId} as a duplicate but holds no such row");

        var differ = new List<string>(5);
        if (!string.Equals(stored.Metric, UsageLedgerMetricNames.Results, StringComparison.Ordinal))
        {
            differ.Add("metric");
        }

        if (stored.ProjectId != entry.ProjectId)
        {
            differ.Add("project_id");
        }

        if (stored.Quantity != entry.Quantity)
        {
            differ.Add("quantity");
        }

        if (!string.Equals(stored.Unit, entry.Unit, StringComparison.Ordinal))
        {
            differ.Add("unit");
        }

        if (!string.Equals(stored.Period, entry.Period, StringComparison.Ordinal))
        {
            differ.Add("period");
        }

        if (differ.Count > 0)
        {
            throw new InvalidOperationException(
                $"the usage ledger row of batch {eventBatchId} differs in {string.Join(", ", differ)}: a recorded bill is never rewritten");
        }
    }
}
