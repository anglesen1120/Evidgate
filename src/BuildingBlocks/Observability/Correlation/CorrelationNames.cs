using System.Diagnostics.CodeAnalysis;

namespace Evidgate.BuildingBlocks.Observability.Correlation;

/// <summary>
/// The one correlation vocabulary of the two entry points (10.3.1-T1.2, 00 §9.1,
/// NFR-10-1): the attribute names a span or a log record may carry, the header the edge
/// accepts, and the shape an accepted value must have.
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

    /// <summary>
    /// The request id to tag a span or a log record with: <paramref name="value"/> when it is
    /// an acceptable id, otherwise <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Every read of a correlation id repeats the check the edge applied — a parent's span tag
    /// and a baggage item are both input, and baggage is client-controlled on the request path
    /// (a caller can send <c>baggage: evidgate.request_id=…</c>). An id copied without this
    /// check would reach a span tag, a log attribute and, from there, every child span.
    /// </remarks>
    /// <param name="value">A correlation id read from a tag or a baggage item, or null.</param>
    /// <returns>The id to use, or null when the value is not a request id.</returns>
    public static string? SafeRequestId(string? value) => IsSafeRequestId(value) ? value : null;

    /// <summary>
    /// The run id to tag a span or a log record with, canonicalised to the <c>D</c> GUID form:
    /// <paramref name="value"/> when it is a GUID, otherwise <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Same reason as <see cref="SafeRequestId"/>: a run id arrives as a route value, a span tag
    /// or a baggage item, and only a GUID is a run id. Canonicalising means a value copied from a
    /// tag and one read from baggage compare equal downstream.
    /// </remarks>
    /// <param name="value">A correlation id read from a tag or a baggage item, or null.</param>
    /// <returns>The GUID in <c>D</c> form, or null when the value is not a GUID.</returns>
    public static string? SafeRunId(string? value) => CanonicalGuid(value);

    /// <summary>
    /// The job id to tag a span or a log record with, canonicalised to the <c>D</c> GUID form: the
    /// claimed task row's <c>task.id</c>, or <c>null</c> when the value is not a GUID.
    /// </summary>
    /// <param name="value">A correlation id read from a tag, or null.</param>
    /// <returns>The GUID in <c>D</c> form, or null when the value is not a GUID.</returns>
    public static string? SafeJobId(string? value) => CanonicalGuid(value);

    /// <summary>
    /// The text of a correlation value read from a span tag: a <see cref="string"/> as it is, a
    /// <see cref="Guid"/> in <c>D</c> form, anything else <c>null</c>.
    /// </summary>
    /// <remarks>
    /// A module tags the run with the run's <see cref="Guid"/> object rather than its text
    /// (<c>CreateRun</c>, <c>PlanExecutions</c>), while the edge and the task carrier tag text. Reading
    /// only <c>as string</c> would silently drop the module-tagged value and leave every child span of
    /// a run without its <c>run_id</c>.
    /// </remarks>
    /// <param name="value">A tag value of any type, or null.</param>
    /// <returns>The correlation text, or null when the value is not a string or a GUID.</returns>
    public static string? CorrelationText(object? value) => value switch
    {
        string text => text,
        Guid id => id.ToString("D"),
        _ => null,
    };

    private static string? CanonicalGuid(string? value) =>
        Guid.TryParse(value, out var id) ? id.ToString("D") : null;
}
