using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
/// carries the key has it <em>replaced</em> by the stamping activity's own carrier (a
/// producer that embeds client JSON must never pass a client-chosen parent trace or
/// ids through), and a document that cannot be re-read (never valid JSON for the
/// <c>jsonb</c> column anyway) is returned exactly as it came in: correlation never
/// fails a producer.
/// </para>
/// <para>
/// <b>What is trusted.</b> Only two sources feed the ids: the attributes the edge put
/// on the span (<see cref="RequestIdAttribute"/>, and <see cref="RunIdAttribute"/> —
/// the latter written only from a routed <c>{r}</c>) and, for the run, the producer's
/// own top-level <c>run_id</c> payload member. Ambient baggage is deliberately never a
/// source: ASP.NET Core copies a client's <c>baggage</c> header into
/// <see cref="Activity.Baggage"/>, so reading it here would let a caller choose another
/// tenant's run id or an unbounded request id. Everything read back is validated the
/// same way (a bounded printable request id, a GUID run id, a bounded
/// <c>tracestate</c>), so a row written by any other path degrades instead of
/// injecting.
/// </para>
/// <para>
/// <b>Why the names are repeated.</b> The Persistence assembly deliberately does not
/// reference <c>Evidgate.BuildingBlocks.Observability</c> (the same reason
/// <c>OutboxDispatchMetrics</c> repeats the meter name), so the four attribute names,
/// the two baggage names, the request-id shape and the activity-source name are
/// repeated here by value. A rename there must update these constants too.
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

    /// <summary>Cap on an accepted request id (mirrors <c>CorrelationNames.MaxRequestIdLength</c>).</summary>
    public const int MaxRequestIdLength = 128;

    /// <summary>
    /// Cap on a carried W3C <c>tracestate</c>. The header is client-controlled on any
    /// public request and the propagator copies it onto the request's
    /// <see cref="Activity"/>; without a bound it would be persisted into every payload
    /// the request produces and re-emitted by the consumer. A longer value is dropped,
    /// never truncated (a truncated list is not a valid <c>tracestate</c>).
    /// </summary>
    public const int MaxTraceStateLength = 256;

    /// <summary>
    /// The one activity source of this assembly's queue spans (<see cref="TaskOperationName"/>,
    /// <c>TaskClaimer.ClaimOperationName</c>, <c>OutboxDispatcher.DispatchOperationName</c>).
    /// One instance, so a listener or <c>AddSource</c> sees every span of the queue through
    /// one name and one sampling decision path.
    /// </summary>
    internal static ActivitySource Source { get; } = new(ActivitySourceName);

    /// <summary>
    /// Adds the current activity's <c>traceparent</c> and correlation ids to
    /// <paramref name="payloadJson"/> under <see cref="CarrierKey"/>, replacing a carrier
    /// the document already carried.
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
            if (root.ValueKind != JsonValueKind.Object)
            {
                return payloadJson;
            }

            // Only the span attributes and the producer's own payload member are read;
            // ambient baggage is a client-supplied header at the edge, so it is never a
            // source of an id here (see the type's remarks).
            var requestId = NormalizeRequestId(activity.GetTagItem(RequestIdAttribute) as string);
            var runId = NormalizeRunId(TopLevelString(root, RunIdPayloadMember))
                ?? NormalizeRunId(activity.GetTagItem(RunIdAttribute) as string);
            var traceState = NormalizeTraceState(activity.TraceStateString);

            // One payload-sized buffer, written as UTF-8 once: the writer fills it and the
            // string is built from the written span, with no intermediate stream copy.
            var buffer = new ArrayBufferWriter<byte>(payloadJson.Length + 512);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();

                writer.WriteStartObject(CarrierKey);
                writer.WriteString(TraceParentField, activity.Id);
                if (traceState is not null)
                {
                    writer.WriteString(TraceStateField, traceState);
                }

                if (requestId is not null)
                {
                    writer.WriteString(RequestIdField, requestId);
                }

                if (runId is not null)
                {
                    writer.WriteString(RunIdField, runId);
                }

                writer.WriteEndObject();

                foreach (var property in root.EnumerateObject())
                {
                    // The carrier above is this activity's. A document that already carried
                    // the key (client JSON embedded at the top level, or a payload stamped
                    // by an earlier hop) does not get to keep its own.
                    if (string.Equals(property.Name, CarrierKey, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    property.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
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
    /// carrier yields the empty value, so the consumer simply starts a root trace; every
    /// member that survives the read has passed the same shape checks the edge applies
    /// (a bounded printable request id, a GUID run id, a bounded <c>tracestate</c>).
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

            return Normalize(new CorrelationCarrier(traceParent, traceState, requestId, runId));
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

        carrier = Normalize(carrier);

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

        carrier = Normalize(carrier);

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

    /// <summary>
    /// The request id an inbound value may be exported as: a bounded printable-ASCII
    /// token (the edge's rule, mirrored here because this assembly cannot reference it).
    /// </summary>
    /// <param name="value">A candidate request id, from a span attribute or a header.</param>
    /// <returns>The value, or null when it is absent or unsafe.</returns>
    public static string? NormalizeRequestId(string? value) =>
        IsSafeRequestId(value) ? value : null;

    /// <summary>Parses a run id, so only a GUID ever reaches a span tag, a log attribute or the carrier.</summary>
    /// <param name="value">A candidate run id.</param>
    /// <returns>The GUID in <c>"D"</c> form, or null when the value is not a GUID.</returns>
    public static string? NormalizeRunId(string? value) =>
        Guid.TryParse(value, out var runId) ? runId.ToString("D") : null;

    /// <summary>
    /// The <c>tracestate</c> a carrier may store: a bounded, printable value. A value the
    /// client sent is dropped rather than truncated or persisted unbounded
    /// (<see cref="MaxTraceStateLength"/>).
    /// </summary>
    /// <param name="value">A candidate <c>tracestate</c>.</param>
    /// <returns>The value, or null when it is absent or out of bounds.</returns>
    public static string? NormalizeTraceState(string? value) =>
        IsSafeTraceState(value) ? value : null;

    /// <summary>Whether a request id may be exported as-is: printable ASCII, at most <see cref="MaxRequestIdLength"/> characters.</summary>
    /// <param name="value">A candidate request id.</param>
    public static bool IsSafeRequestId([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxRequestIdLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            // Visible ASCII only (0x21–0x7E), exactly as the edge accepts an X-Request-Id:
            // a control character would forge a log line, and a space is not part of a token.
            if (character is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a <c>tracestate</c> may be carried: at most <see cref="MaxTraceStateLength"/>
    /// characters of printable ASCII (the W3C list separator is a comma, and a space may
    /// follow it).
    /// </summary>
    /// <param name="value">A candidate <c>tracestate</c>.</param>
    public static bool IsSafeTraceState([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxTraceStateLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Applies the three shape checks to a carrier that came from anywhere other than
    /// <see cref="Stamp"/>'s own validated inputs — a stored payload, or a caller that
    /// built one. Idempotent.
    /// </summary>
    private static CorrelationCarrier Normalize(CorrelationCarrier carrier) =>
        new(
            carrier.TraceParent,
            NormalizeTraceState(carrier.TraceState),
            NormalizeRequestId(carrier.RequestId),
            NormalizeRunId(carrier.RunId));

    private static string? TopLevelString(JsonElement root, string member) =>
        root.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// The correlation a stored payload carries: the producer's W3C context and the ids
/// an operator searches by. Every member is optional — a row written before this
/// task, or by SQL, has none — and every member is validated where it is read
/// (<see cref="TaskCorrelation.Read"/>): a request id only when it is a bounded
/// printable token, a run id only when it is a GUID, a <c>tracestate</c> only when it
/// is bounded. The <c>traceparent</c> is kept as text and parsed by the W3C rules when
/// the consumer span is started, so an unparsable one simply starts a root trace.
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
