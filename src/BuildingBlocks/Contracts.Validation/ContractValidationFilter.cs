using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.BuildingBlocks.Contracts.Validation;

/// <summary>
/// 00 §5.5 edge rule "JSON Schema runs before binding" for ingest payloads,
/// mounted per route (01.3.2-T2.1). Minimal-API endpoint filters run after route
/// binding but before the handler, so the route binds its body as a
/// <see cref="JsonDocument"/> or <see cref="JsonElement"/> parameter and this
/// filter validates that parse tree — no DTO materializes, and no business code
/// runs, for a body the schema would reject.
/// </summary>
/// <remarks>
/// The <c>x-max-bytes</c> transport cap (00 §9.3) is answered 413
/// <c>payload_too_large</c> against the declared content length (a chunked body
/// falls back to the bound document's raw-text byte count); schema violations
/// are 422 <c>request_invalid</c> with the mapped
/// <see cref="ContractValidationError"/> rows as <c>details[]</c>. The filter is
/// opt-in per route via <see cref="ContractValidationEndpointBuilderExtensions.ValidateContractBody{TBuilder}"/>
/// and is never registered globally — Api wiring is a later task.
/// </remarks>
public sealed class ContractValidationFilter : IEndpointFilter
{
    private readonly string _schemaKey;

    /// <summary>Creates the filter for the named contract schema (e.g. <c>event-envelope.v1</c>).</summary>
    /// <param name="schemaKey">A key of the registered <see cref="ContractSchemaSet"/>.</param>
    public ContractValidationFilter(string schemaKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaKey);
        _schemaKey = schemaKey;
    }

    /// <inheritdoc />
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var httpContext = context.HttpContext;
        var schemas = httpContext.RequestServices.GetRequiredService<ContractSchemaSet>();

        var body = FindBody(context)
            ?? throw new InvalidOperationException(
                $"ValidateContractBody('{_schemaKey}') needs a route bound to JsonDocument or JsonElement — " +
                "the filter validates the parsed body before the handler materializes DTOs.");

        var maxBytes = schemas.MaxBytes(_schemaKey);
        if (maxBytes is { } cap)
        {
            // Prefer the transport's declared length; a chunked body (no
            // Content-Length) measures the bound document's re-encoded bytes,
            // which for JSON text equals the delivered byte count.
            var bodyBytes = httpContext.Request.ContentLength
                ?? Encoding.UTF8.GetByteCount(body.GetRawText());
            if (bodyBytes > cap)
            {
                return Failure(
                    httpContext,
                    StatusCodes.Status413PayloadTooLarge,
                    ApiErrorCode.PayloadTooLarge,
                    "the request body is too large",
                    [ContractValidationError.BodyTooLarge]);
            }
        }

        var result = schemas.Validate(_schemaKey, body);
        return result.IsValid
            ? next(context)
            : Failure(
                httpContext,
                StatusCodes.Status422UnprocessableEntity,
                ApiErrorCode.RequestInvalid,
                "the request body does not match its schema",
                result.Errors);
    }

    private static JsonElement? FindBody(EndpointFilterInvocationContext context)
    {
        foreach (var argument in context.Arguments)
        {
            switch (argument)
            {
                case JsonDocument document:
                    return document.RootElement;
                case JsonElement element:
                    return element;
            }
        }

        return null;
    }

    private static ValueTask<object?> Failure(
        HttpContext httpContext,
        int statusCode,
        string code,
        string message,
        IReadOnlyList<ContractValidationError> errors)
    {
        var details = new List<ErrorDetail>(errors.Count);
        foreach (var error in errors)
        {
            details.Add(new ErrorDetail(error.Path, error.Reason, error.Index));
        }

        return ValueTask.FromResult<object?>(
            new ContractValidationErrorResult(statusCode, code, message, details));
    }

    /// <summary>
    /// The 00 §9.2 envelope for a validation failure. The request id is
    /// <see cref="HttpContext.TraceIdentifier"/>, which
    /// <c>RequestIdMiddleware</c> assigns the effective <c>X-Request-Id</c> to
    /// (00 §9.1).
    /// </summary>
    private sealed class ContractValidationErrorResult(
        int statusCode,
        string code,
        string message,
        IReadOnlyList<ErrorDetail> details)
        : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = "application/json";

            var envelope = new ErrorEnvelope(new ErrorBody(
                Code: code,
                Message: message,
                RequestId: httpContext.TraceIdentifier,
                Retryable: false,
                Details: details));

            return httpContext.Response.WriteAsJsonAsync(
                envelope,
                EvidgateJsonContext.Default.ErrorEnvelope,
                "application/json",
                httpContext.RequestAborted);
        }
    }
}
