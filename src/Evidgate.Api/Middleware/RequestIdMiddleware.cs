using System.Diagnostics;
using Evidgate.BuildingBlocks.Observability.Correlation;

namespace Evidgate.Api.Middleware;

/// <summary>
/// 00 §9.1/§5.5 edge middleware: honours an inbound <c>X-Request-Id</c> (and the
/// W3C <c>traceparent</c>, which ASP.NET Core's distributed context propagator
/// already accepts), otherwise mints one, echoes it back in the response header
/// and tags the request span so a trace carries it.
/// </summary>
/// <remarks>
/// Deliberately logs nothing: the request carries an <c>Authorization</c> header,
/// and a bearer token must never reach a production log where redaction is the
/// only guard. The redaction contract covers that shape in a focused unit test
/// against <c>SecretRedactor</c> instead (AC4).
/// </remarks>
public sealed class RequestIdMiddleware
{
    /// <summary>Items key for the effective request id (used by the error envelope).</summary>
    public const string ItemsKey = "evidgate.request_id";

    /// <summary>Request header name of the request id (00 §9.1).</summary>
    public const string HeaderName = "X-Request-Id";

    private readonly RequestDelegate _next;

    /// <summary>Creates the middleware.</summary>
    public RequestIdMiddleware(RequestDelegate next) => _next = next;

    /// <inheritdoc cref="RequestDelegate" />
    public async Task Invoke(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var inbound = context.Request.Headers[HeaderName].FirstOrDefault();
        // 10.3.1-T1.2: an inbound id is honoured only when it is a bounded printable
        // token (`CorrelationNames.IsSafeRequestId`). It is echoed in a response header,
        // written to a span tag, exported as a log attribute and stored on the audit
        // row, so an over-long value or one carrying CR/LF would forge a header or a log
        // line. Anything else is replaced by a minted id, never echoed raw.
        var requestId = CorrelationNames.IsSafeRequestId(inbound)
            ? inbound
            : Activity.Current?.Id ?? context.TraceIdentifier;

        context.TraceIdentifier = requestId;
        context.Items[ItemsKey] = requestId;
        context.Response.Headers[HeaderName] = requestId;
        Activity.Current?.SetTag("evidgate.request_id", requestId);
        // 02.7.1: audit rows carry the request id; baggage flows to the pipeline with the async context.
        Activity.Current?.AddBaggage(Evidgate.BuildingBlocks.Persistence.Audit.AuditTrail.RequestIdBaggage, requestId);
        // 10.3.1-T1.2: the same four correlation ids on the span, plus the run id when
        // the route names one (routing has already resolved the endpoint's parameters).
        RequestCorrelation.TagRequest(context, requestId);

        await _next(context);

        // The route may only be resolved by now on a host that maps endpoints late, so
        // the run id is tagged again; the call is idempotent and cannot throw.
        RequestCorrelation.TagRequest(context, requestId);
    }
}

/// <summary>Accessors for the request id stored by <see cref="RequestIdMiddleware"/>.</summary>
public static class RequestIdHttpContextExtensions
{
    /// <summary>The effective request id of the request (never null once middleware ran).</summary>
    public static string RequestId(this HttpContext context) =>
        context.Items[RequestIdMiddleware.ItemsKey] as string ?? context.TraceIdentifier;
}
