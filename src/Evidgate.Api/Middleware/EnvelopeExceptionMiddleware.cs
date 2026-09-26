using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;

namespace Evidgate.Api.Middleware;

/// <summary>
/// Catches what the pipeline throws and writes the 00 §9.2 error envelope —
/// never <c>ProblemDetails</c> (00 §5.5 C8). Since 01.4.2-T4 this is the
/// exception half of the mapping only: a <c>Result</c> is rendered by
/// <see cref="Endpoints.ResultEndpointFilter"/>, while bugs, infrastructure
/// failures and the framework's own 404/405/413 keep landing here — plus the 401
/// of a pipeline-managed message dispatched without a resolved tenant.
/// </summary>
public sealed partial class EnvelopeExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<EnvelopeExceptionMiddleware> _logger;

    /// <summary>Creates the middleware.</summary>
    public EnvelopeExceptionMiddleware(RequestDelegate next, ILogger<EnvelopeExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <inheritdoc cref="RequestDelegate" />
    public async Task Invoke(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context);
        }
        catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
        {
            // D6: preserve the framework's status (e.g. 413 for an oversized body)
            // instead of flattening every bad request to 400.
            var status = exception.StatusCode;
            await ErrorEnvelopeWriter.WriteAsync(
                context,
                status,
                ErrorEnvelopeWriter.CodeForStatus(status),
                ErrorEnvelopeWriter.MessageForStatus(status));
        }
        catch (JsonException) when (!context.Response.HasStarted)
        {
            await ErrorEnvelopeWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                ApiErrorCode.RequestInvalid,
                "the request body is not valid JSON");
        }

        // 01.4.2-T4: a pipeline-managed message dispatched without a resolved
        // request tenant fails closed — 401, never a silently picked org.
        catch (TenantContextMissingException) when (!context.Response.HasStarted)
        {
            await ErrorEnvelopeWriter.WriteAsync(
                context,
                StatusCodes.Status401Unauthorized,
                ApiErrorCode.Unauthenticated,
                "the request is not authenticated");
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            LogUnhandled(exception);
            await ErrorEnvelopeWriter.WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                ApiErrorCode.InternalError,
                "internal error");
        }
    }

    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Warning,
        Message = "Unhandled request failure mapped to the 9.2 envelope")]
    private partial void LogUnhandled(Exception exception);
}
