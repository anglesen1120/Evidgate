using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Invites;
using Evidgate.Modules.Identity.Login;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Identity.Endpoints;

/// <summary>
/// <c>POST /v1/auth/invite-token</c> (02.3.1-T2, Q1): stages the invite token a landing page received in its
/// link, so the federated login that follows can bind the pending membership to the verified email.
/// </summary>
/// <remarks>
/// <para>
/// PRE-TENANT, like the login routes it sits beside (00 §5.4.8): the browser has neither a session nor a
/// tenant at this point, so the route takes no tenant, principal or permission, and it is mapped onto the same
/// group as <c>start</c>/<c>callback</c> so it carries the same single envelope filter.
/// </para>
/// <para>
/// The token is a bearer secret. It arrives once, in a request BODY — <c>{"invite_token": "evgi_…"}</c>, the
/// <c>InviteTokenRequest</c> of <c>contracts/openapi.yaml</c> — never a URL, a query string or a
/// <c>Location</c>, is validated with <see cref="InviteTokenFormat.TryParse"/>, and leaves the server only
/// inside the encrypted <see cref="InviteTokenCookie"/>: the answer is a bodiless 204 and the value is never
/// logged, counted, echoed, stored on the session row or copied into a <c>returnUrl</c>. A malformed token is
/// 422 <c>request_invalid</c> with <c>details[].path</c> = <c>invite_token</c> and no cookie written, so a
/// client learns nothing about whether a well-formed token exists; <c>path</c> is <c>body</c> only when the
/// body itself is wrong (not JSON, not a JSON object, or not <c>application/json</c>). The route is throttled
/// per client address from the CI exchange limiter's budget under its own key prefix, aimed at the same cost
/// as a <c>start</c>: one client cannot fill the cookie jar (or the staging path) for everyone else.
/// </para>
/// <para>
/// The body is bounded before it is parsed: a declared or streamed length over <see cref="MaxBodyBytes"/> is
/// 413 <c>payload_too_large</c>, so an anonymous caller cannot make the host buffer a large document for a
/// token that could never be longer than <see cref="InviteTokenFormat.MaxLength"/> characters. The body must
/// also be <c>application/json</c> (422 <c>request_invalid</c> otherwise): that is what stops a cross-site
/// <c>enctype=text/plain</c> form post — the one encoding that needs no CORS preflight — from staging an
/// attacker's token in a victim's cookie jar, because a browser cannot label a form post with that type.
/// </para>
/// </remarks>
internal static class InviteTokenEndpoints
{
    /// <summary>Largest body accepted: the token's own contract (80 characters) plus envelope slack.</summary>
    internal const int MaxBodyBytes = 4096;

    /// <summary>
    /// The client-address limiter key prefix of this route: the CI exchange limiter's key
    /// (<see cref="ExchangeRateLimiter.ClientKey"/>) under a route-specific prefix, so a flood here cannot spend
    /// <c>start</c>'s or <c>callback</c>'s window.
    /// </summary>
    private const string LimiterPrefix = "login-invite:";

    /// <summary>This route's answer is never cacheable: it sets a credential-bearing cookie.</summary>
    private const string NoStore = "no-store";

    /// <summary>
    /// The wire member name of the token in the request body (<c>contracts/openapi.yaml</c>,
    /// <c>InviteTokenRequest</c>). It is also the <c>details[].path</c> of every refusal that names the field.
    /// </summary>
    private const string TokenField = "invite_token";

    /// <summary>Maps the route onto the login group and returns it.</summary>
    /// <param name="group">The <c>/v1/auth</c> group <see cref="LoginEndpoints"/> built.</param>
    /// <returns>The same group, so one composition applies to every route on it.</returns>
    internal static RouteGroupBuilder Map(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        // Same reason as the protocol routes: with a declared non-200 status ASP.NET infers a 200 response from
        // the delegate's `Result` return type, which would put the wrapper's schema in the served document. The
        // bodiless `.Produces(200)` replaces that inference; the 204 is the real answer.
        group.MapPost("/invite-token", StageAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Stage an invite token in a short-lived __Host- cookie for the next federated login (02.3.1-T2)");

        return group;
    }

    /// <summary>
    /// <c>stage</c>: a 429 once the client's window is spent, a 413 for an oversized body, a 422 for a body
    /// that is not JSON or a token that is not shaped like one, otherwise 204 plus the staged-invite cookie.
    /// </summary>
    private static async Task<Result> StageAsync(
        HttpContext context,
        InviteTokenCookie cookie,
        ExchangeRateLimiter limiter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cookie);
        ArgumentNullException.ThrowIfNull(limiter);

        context.Response.Headers.CacheControl = NoStore;

        // First, before the body is touched: an anonymous caller must not spend another client's capacity by
        // posting tokens, and the cookie is a credential carrier worth throttling.
        if (Throttle(context, limiter) is { } throttled)
        {
            return Result.Failure(throttled);
        }

        var (body, error) = await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return Result.Failure(error);
        }

        // Shape only: whether the token exists, has expired or belongs to another org is not this route's
        // business (the binder resolves it per org at login), and answering it here would turn the route into
        // an oracle for invite prefixes.
        if (!InviteTokenFormat.TryParse(body, out _, out _))
        {
            return Result.Failure(Error.Invalid(
                "the invite token is not valid",
                [new ErrorDetail("invite_token", "invalid")]));
        }

        cookie.Write(context, body!);

        return Result.Success(StatusCodes.Status204NoContent);
    }

    /// <summary>Spends one unit of the client's window, the same cost as a <c>start</c>; the 429 carries <c>Retry-After</c>.</summary>
    /// <param name="context">The request whose response carries the header.</param>
    /// <param name="limiter">The shared CI exchange limiter.</param>
    private static Error? Throttle(HttpContext context, ExchangeRateLimiter limiter)
    {
        var key = LimiterPrefix + ExchangeRateLimiter.ClientKey(context.Connection.RemoteIpAddress);

        if (limiter.TryAcquire(key, out var retryAfter))
        {
            return null;
        }

        context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

        return new Error(
            ApiErrorCode.RateLimited,
            "too many invite attempts; retry later",
            StatusCodes.Status429TooManyRequests,
            Retryable: true);
    }

    /// <summary>
    /// Reads the body under <see cref="MaxBodyBytes"/> and returns <c>invite_token</c>, or the error the route
    /// answers (413 or 422). Nothing read here is logged.
    /// </summary>
    /// <remarks>
    /// The body is parsed as a <see cref="JsonDocument"/> rather than deserialized into a record, because the
    /// contract fixes WHICH field a refusal names: a non-string <c>invite_token</c> is
    /// <c>details[].path = invite_token</c>, while a body that is not JSON, or not a JSON object, is the whole
    /// body's fault and reports <c>body</c> (contracts/openapi.yaml, <c>POST /auth/invite-token</c>).
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="cancellationToken">Request abort.</param>
    private static async Task<(string? Token, Error? Error)> ReadBodyAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.ContentLength > MaxBodyBytes)
        {
            return (null, TooLarge());
        }

        // Media type first, because it is a security boundary and not a formality: a cross-site form post can
        // set a victim's cookie (SameSite governs what the browser SENDS, not what a response may Set-Cookie),
        // and `enctype=text/plain` is the one encoding that lets an attacker put JSON-looking text into the
        // body without triggering a CORS preflight. Requiring `application/json` means such a form cannot
        // stage a token: the browser cannot label a form post with that type.
        if (!context.Request.HasJsonContentType())
        {
            return (null, Invalid("the request body must be application/json"));
        }

        using var limited = new MemoryStream();
        var buffer = new byte[1024];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await limited.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (limited.Length > MaxBodyBytes)
            {
                return (null, TooLarge());
            }
        }

        if (limited.Length == 0)
        {
            return (null, Invalid("the request body is required"));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(limited.ToArray());
        }
        catch (JsonException)
        {
            return (null, Invalid("the request body is not valid JSON"));
        }

        using (document)
        {
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? ReadToken(document.RootElement)
                : (null, Invalid("the request body must be a JSON object"));
        }
    }

    /// <summary>
    /// <c>invite_token</c> out of a JSON object body, or the refusal a value of the wrong type earns.
    /// </summary>
    /// <param name="body">The request body, already known to be a JSON object.</param>
    /// <returns>
    /// The token — null when the member is absent or explicitly <c>null</c>, which the caller's shape check
    /// then refuses as a field error — or the 422 whose <c>details[].path</c> names <c>invite_token</c> when
    /// the member carries anything but a string. Either verdict is about SHAPE only: nothing here can reveal
    /// whether a token exists.
    /// </returns>
    private static (string? Token, Error? Error) ReadToken(JsonElement body)
    {
        string? token = null;

        // The name is matched case-insensitively, as the serializer this replaced did, and the last
        // occurrence of a repeated member wins, as it would there too.
        foreach (var property in body.EnumerateObject())
        {
            if (!string.Equals(property.Name, TokenField, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                // A number, a boolean, an object or an array is not a token: the contract names the field,
                // so the client is told which member to fix and nothing about the token itself.
                return (null, Error.Invalid(
                    "the invite token must be a string",
                    [new ErrorDetail(TokenField, "invalid")]));
            }

            token = property.Value.GetString();
        }

        return (token, null);
    }

    private static Error TooLarge() => new(
        ApiErrorCode.PayloadTooLarge,
        "the request body is too large",
        StatusCodes.Status413PayloadTooLarge);

    private static Error Invalid(string message) => Error.Invalid(message, [new ErrorDetail("body", "invalid")]);
}
