using Evidgate.Api.Middleware;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.BuildingBlocks.SharedKernel.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;

namespace Evidgate.Api.Endpoints;

/// <summary>
/// 00 §5.5 C8: the one place a pipeline <see cref="Result"/> becomes an HTTP
/// response. Mounted on a route with <c>AddEndpointFilter</c>, it turns a
/// <see cref="Result{T}"/> success into the body's JSON with the result's own
/// status, and a failure into the 00 §9.2 error envelope — never
/// <c>ProblemDetails</c>. A route whose delegate returns anything else passes
/// through untouched, so this filter is safe on a route that mixes the two.
/// </summary>
public sealed class ResultEndpointFilter : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var outcome = await next(context).ConfigureAwait(false);

        return outcome is Result result ? result.ToHttpResult(context.HttpContext) : outcome;
    }
}

/// <summary>The Result → 00 §9.2 envelope mapping (00 §5.5 C8).</summary>
public static class ResultHttpExtensions
{
    /// <summary>The <c>Retry-After</c> of a 429/503 whose route set none (seconds, 00 §9.2).</summary>
    public const string DefaultRetryAfterSeconds = "60";

    /// <summary>
    /// Renders <paramref name="result"/> as the endpoint's HTTP result. Success
    /// carries the result's own status (a command that creates work answers
    /// 201/202) and its boxed body; a failure is the 00 §9.2 envelope with the
    /// request id, status and error the command declared.
    /// </summary>
    public static IResult ToHttpResult(this Result result, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        if (result.IsSuccess)
        {
            // A route that streams (e.g. the audit export) carries its own IResult.
            if (result.SuccessValue is IResult custom)
            {
                return custom;
            }

            // Results.Json resolves the app's configured JsonOptions, which put the
            // source-generated contexts first — the snake_case wire stays.
            return result.SuccessValue is { } body
                ? Results.Json(body, contentType: "application/json", statusCode: result.Status)
                : Results.StatusCode(result.Status);
        }

        var error = result.Error;

        // 00 §9.2: a 429 (rate_limited / quota_exceeded) and a 503 always carry
        // Retry-After. A route that knows a better value sets the header first.
        if (error!.Status is StatusCodes.Status429TooManyRequests or StatusCodes.Status503ServiceUnavailable
            && !context.Response.Headers.ContainsKey(HeaderNames.RetryAfter))
        {
            context.Response.Headers.RetryAfter = DefaultRetryAfterSeconds;
        }

        return TypedResults.Json(
            new ErrorEnvelope(new ErrorBody(
                Code: error.Code,
                Message: error.Message,
                RequestId: context.RequestId(),
                Retryable: error.Retryable,
                Details: error.Details ?? [])),
            EvidgateJsonContext.Default.ErrorEnvelope,
            contentType: "application/json",
            statusCode: error.Status);
    }
}
