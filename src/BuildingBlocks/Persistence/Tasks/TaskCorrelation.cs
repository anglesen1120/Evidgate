using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// The correlation and trace-context carrier of the durable queue (10.3.1-T1.2, AC1,
/// NFR-10-1): it stamps the producing activity's W3C <c>traceparent</c> and the
/// correlation ids into the stored payload, reads them back when the row is claimed,
/// and opens the consumer activity and log scope that carry them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the payload.</b> <c>outbox</c> and <c>task</c> have no headers column
/// (00 §5.3) and this task owns no migration, so the carrier travels inside the one
/// JSON document both tables already copy verbatim
/// (<c>OutboxDispatchSql.EnqueueTasks</c> inserts <c>outbox.payload</c> into
/// <c>task.payload</c>). It is one reserved top-level object,
/// <c>"_evidgate_correlation"</c>, holding <c>traceparent</c>, <c>tracestate</c> and
/// the ids; every handler binds its own record with
/// <c>TaskPayloadSerializer</c>'s snake_case options, which ignore unknown top-level
/// members, and no reader of a specific member is affected. A payload that already
/// carries the key is left untouched, and a document that cannot be re-read (never
/// valid JSON for the <c>jsonb</c> column anyway) is returned exactly as it came in:
/// correlation never fails a producer.
/// </para>
/// <para>
/// <b>Why the names are repeated.</b> The Persistence assembly deliberately does not
/// reference <c>Evidgate.BuildingBlocks.Observability</c> (the same reason
/// <c>OutboxDispatchMetrics</c> repeats the meter name), so the four attribute names
/// and the activity-source name are repeated here by value. A rename there must update
/// these constants too.
/// </para>
/// </remarks>
public static class TaskCorrelation
{
    /// <summary>Reserved top-level payload key of the carrier object.</summary>
    public const string CarrierKey = "_evidgate_correlation";

    /// <summary>Carrier member holding the W3C <c>traceparent</c> of the producing activity.</summary>
    public const string TraceParentField = "traceparent";

    /// <summary>Carrier member holding the W3C <c>tracestate</c>, when the producer had one.</summary>
    public const string TraceStateField = "tracestate";

    /// <summary>Carrier member holding the producing request's id.</summary>
    public const string RequestIdField = "request_id";

    /// <summary>Carrier member holding the run the work belongs to.</summary>
    public const string RunIdField = "run_id";

    /// <summary>Payload member a producer writes for the run (the orchestrator payloads' own name).</summary>
    public const string RunIdPayloadMember = "run_id";

    /// <summary>Span tag and log attribute of the request id (mirrors <c>CorrelationNames.RequestId</c>).</summary>
    public const string RequestIdAttribute = "evidgate.request_id";

    /// <summary>Span tag and log attribute of the run id (mirrors <c>CorrelationNames.RunId</c>).</summary>
    public const string RunIdAttribute = "evidgate.run_id";

    /// <summary>Span tag and log attribute of the claimed task row (mirrors <c>CorrelationNames.JobId</c>).</summary>
    public const string JobIdAttribute = "evidgate.job_id";

    /// <summary>Span tag and log attribute of the trace id (mirrors <c>CorrelationNames.TraceId</c>).</summary>
    public const string TraceIdAttribute = "evidgate.trace_id";

    /// <summary>Baggage item of the request id (mirrors <c>CorrelationNames.RequestIdBaggage</c>).</summary>
    public const string RequestIdBaggage = "evidgate.request_id";

    /// <summary>Baggage item of the run id (mirrors <c>CorrelationNames.RunIdBaggage</c>).</summary>
    public const string RunIdBaggage = "evidgate.run_id";

    /// <summary>Operation name of the consumer span of one claimed task.</summary>
    public const string TaskOperationName = "task.execute";

    /// <summary>Activity-source name (mirrors <c>EvidgateTelemetry.ActivitySourceName</c>).</summary>
    public const string ActivitySourceName = "Evidgate";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    /// <summary>
    /// Adds the current activity's <c>traceparent</c> and correlation ids to
    /// <paramref name="payloadJson"/> under <see cref="CarrierKey"/>.
    /// </summary>
    /// <param name="payloadJson">The producer's payload document.</param>
    /// <returns>The stamped document, or <paramref name="payloadJson"/> unchanged when there is nothing to stamp.</returns>
    public static string Stamp(string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);

        var activity = Activity.Current;
        if (activity is null || activity.IdFormat != ActivityIdFormat.W3C || activity.Id is null)
        {
            return payloadJson;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty(CarrierKey, out _))
            {
                return payloadJson;
            }

            var requestId = activity.GetTagItem(RequestIdAttribute) as string
                ?? activity.GetBaggageItem(RequestIdBaggage);
            var runId = TopLevelString(root, RunIdPayloadMember) ?? activity.GetBaggageItem(RunIdBaggage);

            using var buffer = new MemoryStream(payloadJson.Length + 512);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();

                writer.WriteStartObject(CarrierKey);
                writer.WriteString(TraceParentField, activity.Id);
                if (!string.IsNullOrEmpty(activity.TraceStateString))
                {
                    writer.WriteString(TraceStateField, activity.TraceStateString);
                }

                if (!string.IsNullOrEmpty(requestId))
                {
                    writer.WriteString(RequestIdField, requestId);
                }

                if (!string.IsNullOrEmpty(runId))
                {
                    writer.WriteString(RunIdField, runId);
                }

                writer.WriteEndObject();

                foreach (var property in root.EnumerateObject())
                {
                    property.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            // A document this method cannot re-read is the column's problem, not the
            // producer's: correlation is observability and never changes an outcome.
            return payloadJson;
        }
    }

    /// <summary>
    /// Reads the carrier of a stored payload. A missing, unparsable or malformed
    /// carrier yields the empty value, so the consumer simply starts a root trace.
    /// </summary>
    /// <param name="payloadJson">The stored payload of a claimed row.</param>
    public static CorrelationCarrier Read(string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            // The run may be a producer's own top-level member even when the payload
            // was written by SQL rather than through OutboxWriter (a replayed or
            // seeded row), so it is read from the document as a fallback.
            var runId = TopLevelString(root, RunIdPayloadMember);
            var requestId = default(string);
            var traceParent = default(string);
            var traceState = default(string);

            if (root.TryGetProperty(CarrierKey, out var carrier) && carrier.ValueKind == JsonValueKind.Object)
            {
                traceParent = TopLevelString(carrier, TraceParentField);
                traceState = TopLevelString(carrier, TraceStateField);
                requestId = TopLevelString(carrier, RequestIdField);
                runId ??= TopLevelString(carrier, RunIdField);
            }

            return new CorrelationCarrier(traceParent, traceState, requestId, runId);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// Opens the consumer span of one claimed task (10.3.1-T1.2, AC1): its parent is
    /// the producer's activity when the payload carries a valid W3C context, so the
    /// outbox write and the task run land in one trace; otherwise it is a root.
    /// </summary>
    /// <param name="claim">The claimed row being executed.</param>
    /// <param name="carrier">The payload's correlation carrier.</param>
    /// <returns>The span, or null when nothing listens to tracing.</returns>
    public static Activity? StartTaskActivity(TaskClaim claim, CorrelationCarrier carrier)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var activity = ActivityContext.TryParse(carrier.TraceParent, carrier.TraceState, out var parent)
            ? Source.StartActivity(TaskOperationName, ActivityKind.Consumer, parent)
            : Source.StartActivity(TaskOperationName, ActivityKind.Consumer);

        if (activity is null)
        {
            return null;
        }

        // job_id is the claimed row's own id: that is what an operator pastes into the
        // dead-letter list to find this attempt's work (01.5.1-T4).
        activity.SetTag(JobIdAttribute, claim.Id.ToString("D"));
        activity.SetTag("task.type", claim.TaskType);
        activity.SetTag("task.queue", claim.Queue);
        activity.SetTag("task.attempt", claim.Attempts);

        if (!string.IsNullOrEmpty(carrier.RequestId))
        {
            activity.SetTag(RequestIdAttribute, carrier.RequestId);
            activity.AddBaggage(RequestIdBaggage, carrier.RequestId);
        }

        if (!string.IsNullOrEmpty(carrier.RunId))
        {
            activity.SetTag(RunIdAttribute, carrier.RunId);
            activity.AddBaggage(RunIdBaggage, carrier.RunId);
        }

        if (activity.TraceId != default)
        {
            activity.SetTag(TraceIdAttribute, activity.TraceId.ToHexString());
        }

        return activity;
    }

    /// <summary>
    /// Opens the log scope of one claimed task, so a log line the handler writes
    /// carries the ids even through a provider that does not read activities (the
    /// exported records get them from <see cref="StartTaskActivity"/>'s span either way).
    /// </summary>
    /// <param name="logger">The consumer's logger.</param>
    /// <param name="claim">The claimed row being executed.</param>
    /// <param name="carrier">The payload's correlation carrier.</param>
    /// <returns>The scope, or null when the logger has no scopes enabled.</returns>
    public static IDisposable? BeginTaskScope(ILogger logger, TaskClaim claim, CorrelationCarrier carrier)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(claim);

        var state = new List<KeyValuePair<string, object?>>(4)
        {
            new(JobIdAttribute, claim.Id.ToString("D")),
        };

        if (!string.IsNullOrEmpty(carrier.RunId))
        {
            state.Add(new(RunIdAttribute, carrier.RunId));
        }

        if (!string.IsNullOrEmpty(carrier.RequestId))
        {
            state.Add(new(RequestIdAttribute, carrier.RequestId));
        }

        return logger.BeginScope(state);
    }

    private static string? TopLevelString(JsonElement root, string member) =>
        root.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// The correlation a stored payload carries: the producer's W3C context and the ids
/// an operator searches by. Every member is optional — a row written before this
/// task, or by SQL, has none.
/// </summary>
/// <param name="TraceParent">W3C <c>traceparent</c> of the producing activity.</param>
/// <param name="TraceState">W3C <c>tracestate</c> of the producing activity.</param>
/// <param name="RequestId">The producing request's id.</param>
/// <param name="RunId">The run the work belongs to.</param>
public readonly record struct CorrelationCarrier(
    string? TraceParent,
    string? TraceState,
    string? RequestId,
    string? RunId);
