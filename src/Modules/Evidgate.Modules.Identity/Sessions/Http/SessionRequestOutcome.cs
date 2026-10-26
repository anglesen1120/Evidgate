using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Identity.Sessions.Http;

/// <summary>
/// What one request's <c>__Host-evidgate_session</c> cookie amounts to (02.3.2-T1, D2): the typed refusal when
/// it is not usable, or the live session's ids when it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>The proof is lazy (P2).</b> Only the link-confirm handler (D8/SEC2) ever needs the HMAC of the live cookie,
/// yet a session proof costs one HMAC per authenticated request if it is computed eagerly — on every read the
/// platform serves. So the value the proof is derived from is held PRIVATELY here and hashed only when
/// <see cref="GetSessionProof"/> asks, at most once per request however many callers ask.
/// </para>
/// <para>
/// <b>No raw cookie leaves this type.</b> The cookie value never has a public reader, getter or property: the
/// only observable derived value is the hash, which is what the store compares and what a handler may carry.
/// </para>
/// </remarks>
internal sealed class SessionRequestOutcome
{
    private readonly Lazy<byte[]>? _proof;

    private SessionRequestOutcome(Error? refusal, Guid? userId, Guid? sessionId, Func<byte[]>? proof)
    {
        Refusal = refusal;
        UserId = userId;
        SessionId = sessionId;

        // ExecutionAndPublication: one hash per request even if two callers race, and no lock on the common path
        // (the proof is normally never asked for at all).
        _proof = proof is null ? null : new Lazy<byte[]>(proof, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// The 00 §9.2 error the request answers, or null when the cookie authenticated the request. A non-null
    /// refusal is final: the caller renders it (401 <c>session_expired</c> / <c>session_revoked</c> /
    /// <c>unauthenticated</c>, 403 <c>account_disabled</c> / <c>csrf_failed</c>, 503 <c>unavailable</c>) and runs
    /// no handler. It is never a generic 401, because the three states are distinguishable to the browser and the
    /// contract says so.
    /// </summary>
    public Error? Refusal { get; }

    /// <summary>
    /// The <c>user</c> row the live session belongs to — the ONE source of "who is acting" for a cookie caller,
    /// never a route value or a body field. Null exactly when <see cref="Refusal"/> is non-null.
    /// </summary>
    public Guid? UserId { get; }

    /// <summary>
    /// The live <c>session</c> row, and the id the antiforgery pair is bound to. After a privilege-change
    /// rotation this is the NEW row (the old one is revoked in the same call), so a caller that reads it may write
    /// an audit row against the session the request really continues on.
    /// </summary>
    public Guid? SessionId { get; }

    /// <summary>The refusal outcome for <paramref name="error"/>: no caller, no session, no proof.</summary>
    /// <param name="error">The 00 §9.2 error the request answers.</param>
    public static SessionRequestOutcome Refused(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new SessionRequestOutcome(error, userId: null, sessionId: null, proof: null);
    }

    /// <summary>The live outcome of an authenticated request.</summary>
    /// <param name="userId">The <c>user</c> row the session belongs to.</param>
    /// <param name="sessionId">The live <c>session</c> row (the NEW one when the request rotated).</param>
    /// <param name="proof">
    /// How to derive <see cref="GetSessionProof"/> from the LIVE cookie value — the presented value when the
    /// request did not rotate, the freshly issued one when it did. Invoked at most once, and only when asked.
    /// </param>
    public static SessionRequestOutcome Live(Guid userId, Guid sessionId, Func<byte[]>? proof = null) =>
        new(null, userId, sessionId, proof);

    /// <summary>
    /// The HMAC-SHA256 (current pepper, the same bytes the <c>session.cookie_hmac</c> column stores) of the LIVE
    /// session's cookie value: of the presented value when the request did not rotate, of the freshly issued value
    /// when a lazy rotation happened. It is what a handler that must re-resolve the session server-side (link
    /// confirm, D8/SEC2) hands to the resolver instead of re-reading the request cookie — which is dead after a
    /// rotation. Computed on the first call and memoized, so the request path pays for it only when it is needed.
    /// </summary>
    /// <remarks>
    /// A HASH, never the cookie: it is never logged, never put in a metric label and never echoed. Null when the
    /// request has no live session (a refusal, or a bearer/absent cookie).
    /// </remarks>
    /// <returns>The stored HMAC of the live cookie value, or null.</returns>
    public byte[]? GetSessionProof() => Refusal is null ? _proof?.Value : null;
}

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
