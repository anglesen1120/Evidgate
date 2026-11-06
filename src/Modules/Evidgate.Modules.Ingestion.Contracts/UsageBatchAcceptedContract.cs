using System.Globalization;
using System.Text.Json;

namespace Evidgate.Modules.Ingestion.Contracts;

/// <summary>
/// The metered source event of an accepted batch: the <c>usage.batch_accepted</c> outbox row's task type, payload
/// shape, aggregate and idempotency key. Two writers commit an accepted batch — the event batch route and the raw
/// report parse task — and both must emit the row the metering handler binds, with the batch receipt as its aggregate.
/// <para>
/// Only the report path reads this contract. The ingest route keeps its own literals
/// (<c>IngestEventBatchHandler.UsageTaskType</c> and <c>UsageMetric</c>, the first of which the metering handler also
/// takes its task type from), and a parity test pins this contract's values equal to them, so the two spellings cannot
/// drift silently; moving the ingest literal onto this contract is the events-route owner's follow-up.
/// </para>
/// </summary>
public static class UsageBatchAcceptedContract
{
    /// <summary>The outbox task type of the usage row (04.8.1 §4, 04.1.1 §7).</summary>
    public const string TaskType = "usage.batch_accepted";

    /// <summary>The V1 usage metric name: one metric, <c>event</c> (04.8.1 §4).</summary>
    public const string Metric = "event";

    /// <summary>The outbox aggregate the row names: the <c>event_batch</c> receipt it meters.</summary>
    public const string AggregateType = "event_batch";

    /// <summary>
    /// The payload the metering handler binds: the batch, the metric and the accepted count. The count is a claim, not
    /// the bill — the handler re-reads the receipt and refuses a payload that disagrees with it.
    /// </summary>
    /// <param name="batchId">The accepted batch receipt the ledger row bills.</param>
    /// <param name="quantity">The events the batch accepted; a batch that accepted none is never metered.</param>
    /// <returns>The payload as the JSON text the outbox stores.</returns>
    public static string PayloadJson(Guid batchId, int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        return JsonSerializer.Serialize(new
        {
            event_batch_id = batchId,
            metric = Metric,
            quantity,
        });
    }

    /// <summary>
    /// The source key of one metered batch: a retried task shows the same key, so the ledger bills the batch once
    /// (T43/AC1 of 12.3.1).
    /// </summary>
    /// <param name="batchId">The accepted batch receipt.</param>
    /// <returns>The <c>usage:batch:&lt;id&gt;:&lt;metric&gt;</c> key.</returns>
    public static string IdempotencyKey(Guid batchId) =>
        $"usage:batch:{batchId.ToString("D", CultureInfo.InvariantCulture)}:{Metric}";
}
