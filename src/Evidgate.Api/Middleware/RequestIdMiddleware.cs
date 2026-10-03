using System.Diagnostics;

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

    /// <summary>Cap on a client-supplied id — never echo unbounded values back.</summary>
    private const int MaxLength = 128;

    private readonly RequestDelegate _next;

    /// <summary>Creates the middleware.</summary>
    public RequestIdMiddleware(RequestDelegate next) => _next = next;

    /// <inheritdoc cref="RequestDelegate" />
    public async Task Invoke(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var requestId = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > MaxLength)
        {
            requestId = Activity.Current?.Id ?? context.TraceIdentifier;
        }

        context.TraceIdentifier = requestId;
        context.Items[ItemsKey] = requestId;
        context.Response.Headers[HeaderName] = requestId;
        Activity.Current?.SetTag("evidgate.request_id", requestId);
        // 02.7.1: audit rows carry the request id; baggage flows to the pipeline with the async context.
        Activity.Current?.AddBaggage(Evidgate.BuildingBlocks.Persistence.Audit.AuditTrail.RequestIdBaggage, requestId);

        await _next(context);
    }
}

/// <summary>Accessors for the request id stored by <see cref="RequestIdMiddleware"/>.</summary>
public static class RequestIdHttpContextExtensions
{
    /// <summary>The effective request id of the request (never null once middleware ran).</summary>
    public static string RequestId(this HttpContext context) =>
        context.Items[RequestIdMiddleware.ItemsKey] as string ?? context.TraceIdentifier;
}
