using System.Diagnostics;
using Evidgate.BuildingBlocks.Observability.Correlation;

namespace Evidgate.Api.Middleware;

/// <summary>
/// The edge half of 10.3.1-T1.1/T1.2: it puts the correlation vocabulary of
/// <see cref="CorrelationNames"/> on the request's span once the edge knows the values.
/// </summary>
/// <remarks>
/// Called by <see cref="RequestIdMiddleware"/> — never by a module — so every route,
/// including one added later, is covered without an annotation. Every method is a
/// no-op when nothing listens to tracing (01.1.2-T1.2), and none of them can throw
/// into the request path: a correlation field is observability, not behaviour
/// (00 §11: telemetry must never fail a request).
/// <para>
/// Both ids are validated where they are written: a request id only when it is a
/// bounded printable token, a run id only from the routed <c>{r}</c> parsed as a GUID.
/// The run id therefore has exactly one source, the route — never the client's
/// <c>baggage</c> header, which ASP.NET Core copies into the request activity as-is.
/// </para>
/// </remarks>
public static class RequestCorrelation
{
    /// <summary>
    /// Tags the current span with the request id, the trace id and — when the route
    /// names a run — the run id. The middleware calls it before and after the
    /// pipeline, because the route is only resolved by the time the request reaches
    /// an endpoint. Idempotent, so the second call simply confirms the first.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="requestId">The effective request id of the request.</param>
    public static void TagRequest(HttpContext context, string? requestId)
    {
        ArgumentNullException.ThrowIfNull(context);

        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        if (CorrelationNames.IsSafeRequestId(requestId))
        {
            activity.SetTag(CorrelationNames.RequestId, requestId);
        }

        if (activity.TraceId != default)
        {
            activity.SetTag(CorrelationNames.TraceId, activity.TraceId.ToHexString());
        }

        if (TryGetRunId(context) is { } runId)
        {
            activity.SetTag(CorrelationNames.RunId, runId);
            activity.AddBaggage(CorrelationNames.RunIdBaggage, runId);
        }
    }

    /// <summary>
    /// The run id of a <c>/v1/runs/{r}/…</c> request, or null for any other route.
    /// Only a parseable UUID is accepted, so a client cannot push free text into the
    /// attribute (the route constraint already rejects one; this repeats the rule
    /// where the tag is written).
    /// </summary>
    /// <param name="context">The request.</param>
    public static string? TryGetRunId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.RouteValues.TryGetValue(CorrelationNames.RunRouteKey, out var value)
            || value is not string text
            || !Guid.TryParse(text, out var runId))
        {
            return null;
        }

        return runId.ToString("D");
    }
}
