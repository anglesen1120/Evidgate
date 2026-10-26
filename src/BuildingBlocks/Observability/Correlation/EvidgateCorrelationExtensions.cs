using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace Evidgate.BuildingBlocks.Observability.Correlation;

/// <summary>
/// Slot B of 10.3.1-T1.1/T1.2: the instrumentation the two hosts add on top of
/// <c>AddEvidgateTelemetry</c>, kept in one additive extension of its own so a host file
/// changes by one line.
/// </summary>
/// <remarks>
/// <para>
/// One pipeline stays one pipeline. The log pipeline, including its processor order, is
/// built by <c>AddEvidgateTelemetry</c> (correlation enrichment, then
/// <c>LogRedactionProcessor</c>, then the exporter — an exporter buffers the record at its
/// own <c>OnEnd</c>, so a processor registered after it would never reach the backend).
/// This extension therefore never touches <see cref="OpenTelemetryLoggerOptions"/>: a
/// second <c>AddOpenTelemetry</c> on the logging builder would register a second
/// <c>ILoggerProvider</c> and export every record twice, and registering a processor here
/// would place it behind the exporter.
/// </para>
/// <para>
/// Instrumentation added here, and nowhere else:
/// </para>
/// <list type="bullet">
/// <item><description><b>Npgsql</b> (<c>AddSource("Npgsql")</c>, Npgsql 10's built-in
/// <c>Npgsql</c> activity source; the provider package adds nothing on top of it): a
/// statement inside a request or a handler is a
/// client span of that trace, which is what makes "one trace across HTTP → Mediator →
/// Npgsql" provable (10.3.1-T1.1).</description></item>
/// <item><description><b>HttpClient</b> (<c>AddHttpClientInstrumentation</c>): every
/// outbound call — a federated login provider, an object store, a provider API — is a
/// client span, and the W3C <c>traceparent</c> of the current activity travels with it
/// (00 §9.1, AC1's "propagated end to end").</description></item>
/// </list>
/// <para>
/// The log-record correlation itself (<see cref="CorrelationLogRecordProcessor"/>) is
/// registered by <c>AddEvidgateTelemetry</c>, first in that pipeline, because only the
/// place that knows the exporter's position can put a processor in front of it.
/// </para>
/// <para>
/// The ASP.NET Core instrumentation stays in the Api host, where its login-route query
/// redaction is configured (02.3.1 §5): adding it here would register the server
/// instrumentation a second time. No metric is subscribed here — the metric label
/// allowlist (10.3.1 §8) never carries a correlation id.
/// </para>
/// </remarks>
public static class EvidgateCorrelationExtensions
{
    /// <summary>
    /// Copies the request's correlation — the ids the edge tagged its span with and the
    /// baggage they travel in — onto a span a module or the pipeline just started
    /// (10.3.1-T1.2, AC1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tags are not inherited by a child span, so without this an operator filtering
    /// module or pipeline spans by <c>request_id</c>/<c>run_id</c> would find only the
    /// request span of the trace. It is called where a span is started — never by a
    /// module — so a route added later is covered too.
    /// </para>
    /// <para>
    /// <paramref name="parent"/> is the ambient activity read <em>before</em> the new
    /// span started: the parent's tags are the authoritative ids, its baggage the ones
    /// inherited down the trace. Both are input — baggage is client-controlled on the
    /// request path — so a value is copied only when it is a request id
    /// (<see cref="CorrelationNames.IsSafeRequestId"/>) or a GUID run id; anything else is
    /// dropped instead of propagated to every child span. A span with no correlated parent
    /// (a worker's own work, a scheduled sweep) carries no id, which is the truth about it;
    /// only its <c>trace_id</c> is set, so the log record of the same activity can join it.
    /// </para>
    /// </remarks>
    /// <param name="span">The span that just started, or null when nothing listens.</param>
    /// <param name="parent">The ambient <see cref="Activity.Current"/> from before the span started.</param>
    public static void ApplyAmbient(Activity? span, Activity? parent)
    {
        if (span is null || span.TraceId == default)
        {
            return;
        }

        Copy(span, parent, CorrelationNames.RequestId, CorrelationNames.RequestIdBaggage, CorrelationNames.SafeRequestId);
        Copy(span, parent, CorrelationNames.RunId, CorrelationNames.RunIdBaggage, CorrelationNames.SafeRunId);

        // job_id is never baggage: it exists only as the tag of the task's own span, so
        // the spans a handler opens under it copy the tag and nothing else — and a tag is
        // input too, so only a GUID is copied.
        if (CorrelationNames.SafeJobId(CorrelationNames.CorrelationText(parent?.GetTagItem(CorrelationNames.JobId))) is { } jobId)
        {
            span.SetTag(CorrelationNames.JobId, jobId);
        }

        span.SetTag(CorrelationNames.TraceId, span.TraceId.ToHexString());
    }

    /// <summary>
    /// Sets one attribute from the parent's tag (preferred) or its baggage, when either carries
    /// a value <paramref name="accept"/> confirms: the tag and the baggage are both input, and
    /// baggage is client-controlled on the request path, so a value that is not an id is dropped
    /// instead of propagated to every child span. A tag may be a <see cref="Guid"/> object
    /// (modules tag the run that way), so it is read as correlation text, not with <c>as string</c>.
    /// </summary>
    private static void Copy(
        Activity span,
        Activity? parent,
        string attribute,
        string baggage,
        Func<string?, string?> accept)
    {
        var value = accept(
            CorrelationNames.CorrelationText(parent?.GetTagItem(attribute)) ?? parent?.GetBaggageItem(baggage));

        if (value is not null)
        {
            span.SetTag(attribute, value);
        }
    }

    /// <summary>
    /// Adds Npgsql/HttpClient tracing and the W3C parent extraction the task consumer
    /// needs to the pipeline <c>AddEvidgateTelemetry</c> already built. The log-record
    /// correlation processor is part of that pipeline already (registered there, ahead of
    /// the redaction processor and the exporter); this call never registers another one.
    /// </summary>
    /// <param name="builder">The OpenTelemetry builder the host was given.</param>
    /// <returns>The same builder, so the host keeps chaining.</returns>
    public static OpenTelemetryBuilder AddEvidgateCorrelation(this OpenTelemetryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithTracing(tracing => tracing
            .AddSource("Npgsql")
            .AddHttpClientInstrumentation());

        return builder;
    }
}
