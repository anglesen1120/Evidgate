using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace Evidgate.BuildingBlocks.Observability.Correlation;

/// <summary>
/// Slot B of 10.3.1-T1.1/T1.2: the instrumentation and the log correlation the two
/// hosts add on top of <c>AddEvidgateTelemetry</c>, kept in one additive extension of
/// its own so a host file changes by one line.
/// </summary>
/// <remarks>
/// <para>
/// One pipeline stays one pipeline. The extension configures the
/// <see cref="OpenTelemetryLoggerOptions"/> the host already registered
/// (<c>AddEvidgateTelemetry</c> → <c>builder.Logging.AddOpenTelemetry</c>) instead of
/// calling <c>AddOpenTelemetry</c> on the logging builder again: a second call would
/// register a second <c>ILoggerProvider</c> and export every record twice.
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
/// <item><description><b>Log correlation</b>
/// (<see cref="CorrelationLogRecordProcessor"/>): the four correlation ids on every
/// exported log record.</description></item>
/// </list>
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
    /// inherited down the trace. A span with no correlated parent (a worker's own work,
    /// a scheduled sweep) carries no id, which is the truth about it; only its
    /// <c>trace_id</c> is set, so the log record of the same activity can join it.
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

        Copy(span, parent, CorrelationNames.RequestId, CorrelationNames.RequestIdBaggage);
        Copy(span, parent, CorrelationNames.RunId, CorrelationNames.RunIdBaggage);

        // job_id is never baggage: it exists only as the tag of the task's own span, so
        // the spans a handler opens under it copy the tag and nothing else.
        if (parent?.GetTagItem(CorrelationNames.JobId) is string jobId && jobId.Length > 0)
        {
            span.SetTag(CorrelationNames.JobId, jobId);
        }

        span.SetTag(CorrelationNames.TraceId, span.TraceId.ToHexString());
    }

    /// <summary>Sets one attribute from the parent's tag (preferred) or its baggage, when either has a value.</summary>
    private static void Copy(Activity span, Activity? parent, string attribute, string baggage)
    {
        var value = parent?.GetTagItem(attribute) as string
            ?? parent?.GetBaggageItem(baggage);

        if (!string.IsNullOrEmpty(value))
        {
            span.SetTag(attribute, value);
        }
    }

    /// <summary>
    /// Adds Npgsql/HttpClient tracing, the W3C parent extraction the task consumer
    /// needs, and the log-record correlation processor to the pipeline
    /// <c>AddEvidgateTelemetry</c> already built.
    /// </summary>
    /// <param name="builder">The OpenTelemetry builder the host was given.</param>
    /// <returns>The same builder, so the host keeps chaining.</returns>
    public static OpenTelemetryBuilder AddEvidgateCorrelation(this OpenTelemetryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The logging pipeline's own options, not a second logging provider.
        builder.Services.Configure<OpenTelemetryLoggerOptions>(
            options => options.AddProcessor(new CorrelationLogRecordProcessor()));

        builder.WithTracing(tracing => tracing
            .AddSource("Npgsql")
            .AddHttpClientInstrumentation());

        return builder;
    }
}
