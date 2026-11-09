using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Modules.Identity.Sessions.Http;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Identity.Endpoints;

internal static partial class SessionEndpoints
{
    /// <summary>
    /// Largest body accepted by <c>revoke-all</c>: one optional boolean plus envelope slack. A larger body is a
    /// client error (413), never a reason to buffer something unbounded (SEC3).
    /// </summary>
    internal const int MaxBodyBytes = 1024;

    private static readonly JsonSerializerOptions BodyOptions =
        new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>
    /// <c>POST /v1/auth/sessions/revoke-all</c>: 204 (idempotent) after revoking every live session of the
    /// caller, keeping the current one unless <c>keep_current</c> is false; with <c>false</c> the response also
    /// expires the session and antiforgery cookies, exactly like <c>logout</c>.
    /// </summary>
    private static async Task<Result> RevokeAllAsync(
        HttpContext context,
        ISessionRequestAuthenticator authenticator,
        ISessionRoutesStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(store);

        context.Response.Headers.CacheControl = NoStore;

        var (proof, _, refusal) = await SessionProofAsync(context, authenticator, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return Result.Failure(refusal);
        }

        var (keepCurrent, bodyError) = await ReadKeepCurrentAsync(context, cancellationToken).ConfigureAwait(false);
        if (bodyError is not null)
        {
            return Result.Failure(bodyError);
        }

        var (status, _) = await store
            .RevokeOthersAsync(proof, keepCurrent, cancellationToken)
            .ConfigureAwait(false);

        switch (status)
        {
            case SessionRouteStatus.Done:
                // The current row is gone with the rest, so its cookie names nothing: expire the pair only
                // after the write so a failed revocation never signs a live session out of its own credential.
                if (!keepCurrent)
                {
                    SessionCookieWriter.Expire(context);
                }

                return Result.Success(StatusCodes.Status204NoContent);

            case SessionRouteStatus.Unauthenticated:
                return Result.Failure(SessionErrors.Unauthenticated);

            // No `NotFound` branch: the definer behind this route has no addressed row to miss, it answers
            // `unauthenticated` when the proof names nothing live. A code this build does not map — an outage, or a
            // function/migration mismatch — is 503, never an invented answer about a session.
            default:
                return Result.Failure(SessionErrors.Unavailable);
        }
    }

    /// <summary>
    /// <c>POST /v1/auth/logout</c> (AC8): 204 after revoking the CURRENT row, and the response expires the
    /// session cookie and the antiforgery pair. A replay of the old value afterwards answers 401
    /// <c>session_revoked</c>, because revocation is a row state and not a key rotation (D9).
    /// </summary>
    private static async Task<Result> LogoutAsync(
        HttpContext context,
        ISessionRequestAuthenticator authenticator,
        ISessionRoutesStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(store);

        context.Response.Headers.CacheControl = NoStore;

        var (proof, _, refusal) = await SessionProofAsync(context, authenticator, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return Result.Failure(refusal);
        }

        var status = await store.LogoutAsync(proof, cancellationToken).ConfigureAwait(false);

        switch (status)
        {
            case SessionRouteStatus.Done:
                SessionCookieWriter.Expire(context);

                return Result.Success(StatusCodes.Status204NoContent);

            case SessionRouteStatus.Unauthenticated:
                return Result.Failure(SessionErrors.Unauthenticated);

            // No `NotFound` branch: `session_revoke_current` addresses no row by id and answers `unauthenticated`
            // when the proof names nothing live, so any other code is an outage or a migration mismatch — 503.
            default:
                return Result.Failure(SessionErrors.Unavailable);
        }
    }

    /// <summary>
    /// The optional body of <c>revoke-all</c>: <c>{"keep_current": false}</c>, or no body at all, which keeps
    /// the current session (the safe default). Bounded read, JSON content type required, and an unparseable or
    /// oversized body is refused before the store is called.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>The flag to pass to the store, or the error to answer.</returns>
    private static async Task<(bool KeepCurrent, Error? Error)> ReadKeepCurrentAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is 0)
        {
            return (KeepCurrent: true, null);
        }

        if (context.Request.ContentLength > MaxBodyBytes)
        {
            return (KeepCurrent: true, TooLarge());
        }

        using var limited = new MemoryStream();
        var buffer = new byte[512];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await limited.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (limited.Length > MaxBodyBytes)
            {
                return (KeepCurrent: true, TooLarge());
            }
        }

        if (limited.Length == 0)
        {
            return (KeepCurrent: true, null);
        }

        // The media type is a security boundary here too (the invite staging route's reasoning): a cross-site
        // form post cannot be labelled `application/json`, so no browser form reaches this body.
        if (!context.Request.HasJsonContentType())
        {
            return (KeepCurrent: true, Invalid("the request body must be application/json"));
        }

        RevokeAllSessionsRequest? body;
        try
        {
            body = JsonSerializer.Deserialize<RevokeAllSessionsRequest>(limited.ToArray(), BodyOptions);
        }
        catch (JsonException)
        {
            return (KeepCurrent: true, Invalid("the request body is not valid JSON"));
        }

        // Absent member = the documented default: keep the current session. Only an explicit `false` kills it.
        return (KeepCurrent: body?.KeepCurrent ?? true, null);
    }

    private static Error TooLarge() => new(
        ApiErrorCode.PayloadTooLarge,
        "the request body is too large",
        StatusCodes.Status413PayloadTooLarge);

    private static Error Invalid(string message) => Error.Invalid(message, [new ErrorDetail("body", "invalid")]);
}

/// <summary>The body of <c>POST /v1/auth/sessions/revoke-all</c>: optional, defaulting to keeping the current session.</summary>
/// <param name="KeepCurrent">True to keep the session that made the request; false revokes it too.</param>
internal sealed record RevokeAllSessionsRequest(bool? KeepCurrent);
