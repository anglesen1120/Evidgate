using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Serialization;

namespace Evidgate.Api.Middleware;

/// <summary>
/// The one place that writes the 00 §9.2 error envelope for responses the
/// pipeline itself produces (an unhandled exception, a framework 404/405, an
/// oversized body). The endpoint-owned failures build the same envelope through
/// their own result, so both paths agree on shape and on the
/// <c>X-Request-Id</c> header (00 §9.1). 01.4.2-T4 replaces this with the
/// Result-driven endpoint filter of the Mediator pipeline.
/// </summary>
public static class ErrorEnvelopeWriter
{
    /// <summary>The 00 §9.2 code for a status the pipeline produced on its own.</summary>
    public static string CodeForStatus(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => ApiErrorCode.RequestInvalid,
        StatusCodes.Status404NotFound => ApiErrorCode.NotFound,
        StatusCodes.Status405MethodNotAllowed => ApiErrorCode.MethodNotAllowed,
        StatusCodes.Status413PayloadTooLarge => ApiErrorCode.PayloadTooLarge,
        StatusCodes.Status422UnprocessableEntity => ApiErrorCode.RequestInvalid,
        _ => ApiErrorCode.InternalError,
    };

    /// <summary>A secret-free message for a status the pipeline produced on its own.</summary>
    public static string MessageForStatus(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "the request is invalid",
        StatusCodes.Status404NotFound => "the requested resource does not exist",
        StatusCodes.Status405MethodNotAllowed => "the request method is not allowed for this route",
        StatusCodes.Status413PayloadTooLarge => "the request body is too large",
        StatusCodes.Status422UnprocessableEntity => "the request entity is not valid",
        _ => "internal error",
    };

    /// <summary>
    /// Writes the envelope with <paramref name="statusCode"/>. The response is not
    /// cleared: the <c>X-Request-Id</c> header set by the request-id middleware must
    /// survive (D7) — only the status and the body are replaced.
    /// </summary>
    public static Task WriteAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        IReadOnlyList<ErrorDetail>? details = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var response = context.Response;
        response.StatusCode = statusCode;
        response.ContentLength = null;
        response.ContentType = "application/json";

        var envelope = new ErrorEnvelope(new ErrorBody(
            Code: code,
            Message: message,
            RequestId: context.RequestId(),
            Retryable: false,
            Details: details ?? []));

        return response.WriteAsJsonAsync(
            envelope,
            EvidgateJsonContext.Default.ErrorEnvelope,
            "application/json",
            context.RequestAborted);
    }
}
