using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;

namespace Evidgate.Api.Middleware;

/// <summary>
/// Catches what the pipeline throws and writes the 00 §9.2 error envelope —
/// never <c>ProblemDetails</c> (00 §5.5 C8). This is the skeleton's slice error
/// mapping; 01.4.2-T4 replaces it with the Result-driven endpoint filter in the
/// Mediator pipeline.
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
