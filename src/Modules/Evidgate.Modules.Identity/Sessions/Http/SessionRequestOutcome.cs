using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Identity.Sessions.Http;

/// <summary>
/// What one request's <c>__Host-evidgate_session</c> cookie amounts to (02.3.2-T1, D2): the typed refusal when
/// it is not usable, or the live session's ids when it is.
/// </summary>
/// <param name="Refusal">
/// The 00 §9.2 error the request answers, or null when the cookie authenticated the request. A non-null
/// refusal is final: the caller renders it (401 <c>session_expired</c> / <c>session_revoked</c> /
/// <c>unauthenticated</c>, 403 <c>account_disabled</c>, 503 <c>unavailable</c>) and runs no handler. It is never
/// a generic 401, because the three states are distinguishable to the browser and the contract says so.
/// </param>
/// <param name="UserId">
/// The <c>user</c> row the live session belongs to — the ONE source of "who is acting" for a cookie caller,
/// never a route value or a body field. Null exactly when <paramref name="Refusal"/> is non-null.
/// </param>
/// <param name="SessionId">
/// The live <c>session</c> row, and the id the antiforgery pair is bound to. After a privilege-change
/// rotation this is the NEW row (the old one is revoked in the same call), so a caller that reads it may write
/// an audit row against the session the request really continues on.
/// </param>
/// <param name="SessionProof">
/// The HMAC-SHA256 (current pepper, the same bytes the <c>session.cookie_hmac</c> column stores) of the LIVE
/// session's cookie value: of the presented value when the request did not rotate, of the freshly issued value
/// when a lazy rotation happened (orchestrator ruling, 02.3.2-T1). It is what a handler that must re-resolve the
/// session server-side (link confirm, D8/SEC2) hands to the resolver instead of re-reading the request cookie —
/// which is dead after a rotation. A HASH, never the cookie: it is never logged, never put in a metric label and
/// never echoed. Null when the request has no live session (a refusal, or a bearer/absent cookie).
/// </param>
internal sealed record SessionRequestOutcome(
    Error? Refusal,
    Guid? UserId,
    Guid? SessionId,
    byte[]? SessionProof = null);

/// <summary>
/// The HTTP face of session authentication (02.3.2-T1, D2/D5/D6): one call answers "who is this cookie, and
/// what should this request do about it", including the lazy rotation a privilege change demands.
/// </summary>
/// <remarks>
/// <para>
/// <b>One database round trip, memoized per request.</b> The implementation caches its outcome in
/// <see cref="HttpContext.Items"/>, so the middleware that enforces the antiforgery pair, the tenant filter that
/// publishes the principal and an endpoint that needs the acting user all read one answer: no second statement
/// and, above all, no second rotation for one request (P1/P3, AC2's throttled write).
/// </para>
/// <para>
/// <b>It abstains for a bearer caller.</b> A request that carries an <c>Authorization</c> header is never
/// authenticated by its cookie (TL T38): the CI/CLI principal is the credential, and a browser that happens to
/// hold a session must not turn a bearer request into a cookie-authenticated one.
/// </para>
/// </remarks>
internal interface ISessionRequestAuthenticator
{
    /// <summary>Resolves the request's session cookie, rotating it first when it demands rotation.</summary>
    /// <param name="http">The request (cookie in, typed refusal or ids out, rotated cookies on the response).</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>The typed refusal, or the live session's user and session ids.</returns>
    ValueTask<SessionRequestOutcome> AuthenticateAsync(HttpContext http, CancellationToken cancellationToken);
}
