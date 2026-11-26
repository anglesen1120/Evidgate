using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Application.Commands;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Identity.Endpoints;

/// <summary>
/// Exchanges an ingest token for a short-lived project-scoped producer token. Rate limiting and prefix
/// resolution precede the tenant scope; the scoped handler verifies the secret and lifecycle.
/// </summary>
public static class CiTokenEndpoints
{
    private const int MaxBodyBytes = 4096;

    private static readonly JsonSerializerOptions BodyOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
        };

    /// <summary>Maps the route and returns the group (only the result filter applies: there is no tenant yet).</summary>
    /// <param name="routes">The route builder.</param>
    public static RouteGroupBuilder MapCiTokenEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("identity");

        group.MapPost("/ci/token", ExchangeAsync)
            .Produces<CiTokenResponse>(StatusCodes.Status200OK)
            .WithSummary("Exchange a project ingest token for a ci_producer token");

        return group;
    }

    private static async Task<Result<CiTokenResponse>> ExchangeAsync(
        HttpContext context,
        IMediator mediator,
        CiProducerTokens tokens,
        CiSecrets secrets,
        ICiResolver resolver,
        ExchangeRateLimiter limiter,
        IRequestTenantContextSetter tenantSetter,
        IRequestPrincipalSetter principalSetter,
        CancellationToken cancellationToken)
    {
        if (!tokens.Enabled || !secrets.CanHash)
        {
            return Result.Failure<CiTokenResponse>(CiErrors.NotConfigured);
        }

        if (await ThrottleAsync(context, limiter, "ip:" + ClientIp.LimiterKey(context), cancellationToken).ConfigureAwait(false)
            is { } throttled)
        {
            return Result.Failure<CiTokenResponse>(throttled);
        }

        if (context.Request.Headers.ContainsKey("Authorization"))
        {
            return Result.Failure<CiTokenResponse>(Invalid("send only an ingest_token in the body"));
        }

        var (body, bodyError) = await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        if (bodyError is not null)
        {
            return Result.Failure<CiTokenResponse>(bodyError);
        }

        if (string.IsNullOrEmpty(body?.IngestToken))
        {
            return Result.Failure<CiTokenResponse>(Invalid("an ingest_token is required"));
        }

        return await ExchangeIngestAsync(context, body.IngestToken, mediator, secrets, resolver, limiter, tenantSetter, principalSetter, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Result<CiTokenResponse>> ExchangeIngestAsync(
        HttpContext context,
        string presented,
        IMediator mediator,
        CiSecrets secrets,
        ICiResolver resolver,
        ExchangeRateLimiter limiter,
        IRequestTenantContextSetter tenantSetter,
        IRequestPrincipalSetter principalSetter,
        CancellationToken cancellationToken)
    {
        // A malformed string never reaches the database: nothing to look up.
        if (!IngestTokenFormat.TryParse(presented, out var prefix, out var secret))
        {
            return Result.Failure<CiTokenResponse>(Unauthorized(ApiErrorCode.TokenUnknown, "no such token"));
        }

        // Keyed by caller address too: the prefix is not secret, so a bucket on it alone would let anyone lock the real job out.
        if (await ThrottleAsync(context, limiter, "tok:" + ClientIp.LimiterKey(context) + ":" + prefix, cancellationToken).ConfigureAwait(false)
            is { } throttled)
        {
            return Result.Failure<CiTokenResponse>(throttled);
        }

        var resolved = await resolver.ResolveIngestTokenAsync(prefix, cancellationToken).ConfigureAwait(false);

        if (resolved is null)
        {
            return Result.Failure<CiTokenResponse>(Unauthorized(ApiErrorCode.TokenUnknown, "no such token"));
        }

        using var tenant = tenantSetter.Publish(resolved.OrgId, resolved.ProjectId);
        using var principal = principalSetter.Publish(
            RequestPrincipal.Service(resolved.ProjectId, [], resolved.IngestTokenId.ToString("D")));

        return await mediator.Send(new ExchangeIngestTokenCommand(resolved.IngestTokenId, secret), cancellationToken)
            .ConfigureAwait(false);
    }

    private static ValueTask<Error?> ThrottleAsync(
        HttpContext context, ExchangeRateLimiter limiter, string key, CancellationToken cancellationToken) =>
        RateLimitGate.AcquireAsync(
            context, limiter, RateSurface.CiExchange, key, cost: 1, "too many token exchanges; retry later", cancellationToken);

    private static async Task<(CiTokenRequest? Body, Error? Error)> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength > MaxBodyBytes)
        {
            return (null, new Error(ApiErrorCode.PayloadTooLarge, "the request body is too large", StatusCodes.Status413PayloadTooLarge));
        }

        try
        {
            using var limited = new MemoryStream();
            var buffer = new byte[1024];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await limited.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (limited.Length > MaxBodyBytes)
                {
                    return (null, new Error(ApiErrorCode.PayloadTooLarge, "the request body is too large", StatusCodes.Status413PayloadTooLarge));
                }
            }

            if (limited.Length == 0)
            {
                return (null, null);
            }

            var body = JsonSerializer.Deserialize<CiTokenRequest>(limited.GetBuffer().AsSpan(0, (int)limited.Length), BodyOptions);

            return (body, null);
        }
        catch (JsonException)
        {
            return (null, Error.BadRequest("expected a JSON object containing only ingest_token", [new ErrorDetail("body", "invalid")]));
        }
    }

    private static Error Invalid(string message) => Unauthorized(ApiErrorCode.TokenInvalid, message);

    private static Error Unauthorized(string code, string message) => new(code, message, StatusCodes.Status401Unauthorized);
}
