using System.Diagnostics.CodeAnalysis;

namespace Evidgate.BuildingBlocks.Observability.Correlation;

/// <summary>
/// The one correlation vocabulary of the two entry points (10.3.1-T1.2, 00 §9.1,
/// NFR-10-1): the attribute names a span, a log record and a metric may carry, the
/// header the edge accepts, and the shape an accepted value must have.
/// </summary>
/// <remarks>
/// <para>
/// <b>Spans and logs, never metrics.</b> <c>request_id</c>, <c>run_id</c>, <c>job_id</c>
/// and <c>trace_id</c> are unbounded per-request values: a metric label carrying one
/// would create a series per request (00 §5.7 P2, 10.3.1 §8 "metric label allowlist").
/// They belong on spans and log records, which are sampled and stored per event; the
/// counters of <c>EvidgateTelemetry</c> keep their closed label vocabularies.
/// </para>
/// <para>
/// The task-side half of the same vocabulary (the payload carrier and the consumer
/// activity) lives in <c>Evidgate.BuildingBlocks.Persistence.Tasks.TaskCorrelation</c>
/// and repeats these names by value — the Persistence assembly deliberately does not
/// reference this one, the same reason <c>OutboxDispatchMetrics</c> repeats the meter
/// name. A rename here must update that constant too.
/// </para>
/// </remarks>
public static class CorrelationNames
{
    /// <summary>Span tag and log attribute carrying the 00 §9.1 request id.</summary>
    public const string RequestId = "evidgate.request_id";

    /// <summary>Span tag and log attribute carrying the run of the request, when the route names one.</summary>
    public const string RunId = "evidgate.run_id";

    /// <summary>Span tag and log attribute carrying the claimed task row (<c>task.id</c>) of a job.</summary>
    public const string JobId = "evidgate.job_id";

    /// <summary>Span tag and log attribute carrying the W3C trace id, so a log search can join on it.</summary>
    public const string TraceId = "evidgate.trace_id";

    /// <summary>Baggage item the request-id middleware sets so the audit trail (02.7.1) and the task carrier can read it.</summary>
    public const string RequestIdBaggage = "evidgate.request_id";

    /// <summary>Baggage item a producer may set so background work inherits the run it belongs to.</summary>
    public const string RunIdBaggage = "evidgate.run_id";

    /// <summary>Request header a client may supply (00 §9.1).</summary>
    public const string RequestIdHeader = "X-Request-Id";

    /// <summary>Route value a <c>/v1/runs/{r}</c> request carries; its value is the run id.</summary>
    public const string RunRouteKey = "r";

    /// <summary>Cap on an accepted client id: the pre-existing <c>RequestIdMiddleware</c> bound.</summary>
    public const int MaxRequestIdLength = 128;

    /// <summary>
    /// Whether an inbound <c>X-Request-Id</c> may be echoed, tagged and exported as-is.
    /// </summary>
    /// <remarks>
    /// The value reaches a response header, a span tag, a log attribute and the audit
    /// row, so only a bounded printable-ASCII token is accepted: an over-long value
    /// would blow up every downstream field, and a control character (CR/LF, NUL, ESC,
    /// a lone surrogate) is a header-injection and log-forging vector. Anything else is
    /// replaced by a minted id at the edge, never echoed.
    /// </remarks>
    /// <param name="value">The inbound header value, or null.</param>
    /// <returns>
    /// True only for a bounded printable-ASCII token, so a caller that branches on it can
    /// use <paramref name="value"/> without a null check.
    /// </returns>
    public static bool IsSafeRequestId([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxRequestIdLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            // Visible ASCII only (0x21–0x7E): no spaces, no control characters, no
            // non-ASCII that could smuggle a separator into a log line.
            if (character is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }
}
