using System.Net;
using System.Text.Json;
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
/// <c>POST /v1/ci/token</c> (02.4.1-T3, 02.4.2): exchanges a GitHub Actions OIDC token (bearer) or an ingest token
/// (body) for a short-lived <c>ci_producer</c> token. This is a PRE-TENANT path (00 §5.4.8, TL T1): the caller has no
/// session, the endpoint verifies the credential, resolves the tenant through <c>evidgate_resolve</c>, and only then
/// publishes tenant and principal and lets the pipeline open the scope. Order of work (AC8): rate limit → shape →
/// signature/lookup (outside any transaction, P6) → scoped command.
/// </summary>
public static class CiTokenEndpoints
{
    private const int MaxBodyBytes = 4096;

    private static readonly JsonSerializerOptions BodyOptions =
        new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private const string Bearer = "Bearer ";

    /// <summary>Maps the route and returns the group (only the result filter applies: there is no tenant yet).</summary>
    /// <param name="routes">The route builder.</param>
    public static RouteGroupBuilder MapCiTokenEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("identity");

        group.MapPost("/ci/token", ExchangeAsync)
            .Produces<CiTokenResponse>(StatusCodes.Status200OK)
            .WithSummary("Exchange a GitHub OIDC token or an ingest token for a ci_producer token (02.4)");

        return group;
    }

    private static async Task<Result<CiTokenResponse>> ExchangeAsync(
        HttpContext context,
        IMediator mediator,
        CiProducerTokens tokens,
        CiSecrets secrets,
        GitHubOidcValidator oidc,
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

        if (Throttle(context, limiter, "ip:" + ClientIp.LimiterKey(context)) is { } throttled)
        {
            return Result.Failure<CiTokenResponse>(throttled);
        }

        var (body, bodyError) = await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        if (bodyError is not null)
        {
            return Result.Failure<CiTokenResponse>(bodyError);
        }

        if (!string.IsNullOrEmpty(body?.IngestToken))
        {
            return await ExchangeIngestAsync(context, body.IngestToken, mediator, secrets, resolver, limiter, tenantSetter, principalSetter, cancellationToken)
                .ConfigureAwait(false);
        }

        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<CiTokenResponse>(Invalid("send a GitHub OIDC token as a bearer, or an ingest_token in the body"));
        }

        return await ExchangeGitHubAsync(
            context, header[Bearer.Length..].Trim(), body?.BindingChallenge, mediator, secrets, oidc, resolver, limiter, tenantSetter, principalSetter, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Result<CiTokenResponse>> ExchangeGitHubAsync(
        HttpContext context,
        string jwt,
        string? challenge,
        IMediator mediator,
        CiSecrets secrets,
        GitHubOidcValidator oidc,
        ICiResolver resolver,
        ExchangeRateLimiter limiter,
        IRequestTenantContextSetter tenantSetter,
        IRequestPrincipalSetter principalSetter,
        CancellationToken cancellationToken)
    {
        // Signature and claims first, entirely outside any transaction: the JWKS fetch can be slow (P6).
        var validated = await oidc.ValidateAsync(jwt, cancellationToken).ConfigureAwait(false);
        if (validated.Claims is not { } claims)
        {
            return Result.Failure<CiTokenResponse>(validated.Failure == OidcFailure.KeysUnavailable
                ? new Error(ApiErrorCode.Unavailable, "the token issuer's keys are unavailable; retry", StatusCodes.Status503ServiceUnavailable, Retryable: true)
                : Invalid("the OIDC token is not valid"));
        }

        if (Throttle(context, limiter, $"repo:{claims.Run.RepositoryId}") is { } throttled)
        {
            return Result.Failure<CiTokenResponse>(throttled);
        }

        var hashes = string.IsNullOrEmpty(challenge) ? null : secrets.HashUnderEveryPepper(CiSecrets.Bytes(challenge));
        var resolved = await resolver.ResolveBindingAsync("github_actions", claims.Run.RepositoryId, hashes, cancellationToken)
            .ConfigureAwait(false);

        switch (resolved)
        {
            case null:
                return Result.Failure<CiTokenResponse>(Error.Denied(ApiErrorCode.BindingMismatch, "no binding admits this repository"));
            case { Status: "pending_unproven" }:
                return Result.Failure<CiTokenResponse>(Error.Denied(ApiErrorCode.BindingPending, "the binding has not proven control of the repository yet"));
            case { Status: "disabled" }:
                return Result.Failure<CiTokenResponse>(Error.Denied(ApiErrorCode.BindingDisabled, "the binding for this repository is disabled"));
            case { OrgId: null } or { ProjectId: null } or { SubjectId: null }:
                return Result.Failure<CiTokenResponse>(Error.Denied(ApiErrorCode.BindingMismatch, "no binding admits this repository"));
        }

        using var tenant = tenantSetter.Publish(resolved.OrgId!.Value, resolved.ProjectId);
        using var principal = principalSetter.Publish(
            RequestPrincipal.Service(resolved.ProjectId, CiProducerPermissions.Set, claims.Jti));

        return await mediator.Send(new ExchangeGitHubTokenCommand(resolved.SubjectId!.Value, claims, challenge), cancellationToken)
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
        if (Throttle(context, limiter, "tok:" + ClientIp.LimiterKey(context) + ":" + prefix) is { } throttled)
        {
            return Result.Failure<CiTokenResponse>(throttled);
        }

        var resolved = await resolver.ResolveIngestTokenAsync(prefix, cancellationToken).ConfigureAwait(false);

        switch (resolved)
        {
            case null:
                return Result.Failure<CiTokenResponse>(Unauthorized(ApiErrorCode.TokenUnknown, "no such token"));
            case { OrgId: null } or { ProjectId: null } or { SubjectId: null }:
                return Result.Failure<CiTokenResponse>(Unauthorized(ApiErrorCode.TokenUnknown, "no such token"));
        }

        using var tenant = tenantSetter.Publish(resolved.OrgId!.Value, resolved.ProjectId);
        using var principal = principalSetter.Publish(
            RequestPrincipal.Service(resolved.ProjectId, CiProducerPermissions.Set, resolved.SubjectId!.Value.ToString("D")));

        return await mediator.Send(new ExchangeIngestTokenCommand(resolved.SubjectId!.Value, secret), cancellationToken)
            .ConfigureAwait(false);
    }

    private static Error? Throttle(HttpContext context, ExchangeRateLimiter limiter, string key)
    {
        if (limiter.TryAcquire(key, out var retryAfter))
        {
            return null;
        }

        context.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return new Error(ApiErrorCode.RateLimited, "too many token exchanges; retry later", StatusCodes.Status429TooManyRequests, Retryable: true);
    }

    private static async Task<(CiTokenRequest? Body, Error? Error)> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is null or 0 && !context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            return (null, null);
        }

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

            var body = JsonSerializer.Deserialize<CiTokenRequest>(limited.ToArray(), BodyOptions);

            return (body, null);
        }
        catch (JsonException)
        {
            return (null, Error.Invalid("the request body is not valid JSON", [new ErrorDetail("body", "invalid")]));
        }
    }

    private static Error Invalid(string message) => Unauthorized(ApiErrorCode.TokenInvalid, message);

    private static Error Unauthorized(string code, string message) => new(code, message, StatusCodes.Status401Unauthorized);
}
