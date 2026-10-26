using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Evidgate.Modules.Identity.Sessions.Http;

/// <summary>
/// The request-path session authenticator of 02.3.2-T1 (D2/D3/D5): it reads the <c>__Host-evidgate_session</c>
/// cookie, hands the value to <see cref="ISessionAuthentication"/> — one statement, the HMAC lookup and the
/// throttled slide — and turns the row's state into the request's outcome. When the row says a privilege change
/// demands rotation, it proves the antiforgery pair of the session it presented and only then rotates, all inside
/// the same memoized call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Precedence (D2), and it is observable.</b> absent/blank cookie or an <c>Authorization</c> header → 401
/// <c>unauthenticated</c> without touching the database; unknown HMAC → 401 <c>unauthenticated</c>; revoked →
/// 401 <c>session_revoked</c>; past either deadline → 401 <c>session_expired</c>; a non-active
/// <c>user.status</c> → 403 <c>account_disabled</c>; a store that cannot answer (no pepper, database fault) → 503
/// <c>unavailable</c>. Revoked outranks expired so a logout is never reported as a timeout, and disabled is a
/// 403 because it is an authorization answer, not a credential one.
/// </para>
/// <para>
/// <b>Rotation is single-winner (D5b).</b> Two requests that present the same rotation-required cookie race for
/// one <c>session_rotate</c>; the store decides one winner, and this class never authenticates the loser on the
/// old row. The failure is mapped by its cause, never flattened: a session that is <c>not_live</c> (revoked,
/// expired, or the loser of the race) is 401 <c>session_revoked</c> because the old cookie is exactly what it is;
/// a disabled account is 403 <c>account_disabled</c>, an authorization answer the browser must not read as "sign in
/// again"; and an outage — or a code this build does not map — is 503 <c>unavailable</c>, which never signs a user
/// out of a session nobody judged. That closes the window a stolen pre-rotation cookie would otherwise keep open
/// for the length of the old row's idle deadline.
/// </para>
/// <para>
/// <b>The pair is proved before the rotation (R1/SEC-1).</b> A privilege-marked session is still the credential a
/// mutation presents, so the request must pass the antiforgery check of THAT session's pair before anything
/// writes: a forged cross-site mutation can neither burn the session (old row revoked, successor minted) nor be
/// handed a <c>Set-Cookie</c> — it answers 403 <c>csrf_failed</c> and the flag stays for the next legitimate
/// request. Only an in-scope mutating request pays this second look: a rotation-required read has no pair to
/// prove (it is a safe method) and rotates exactly as before, and the middleware's own check stays the single
/// check for every non-rotating request.
/// </para>
/// <para>
/// <b>Nothing is logged, ever (D9).</b> The cookie value, the session id, the client address and the user agent
/// never reach a log line, a metric tag or an audit row through this class; outcomes are counted by code only,
/// and the tracing of the request already carries method, route, status and duration.
/// </para>
/// </remarks>
/// <param name="authentication">The one statement that answers what the presented value is (D3).</param>
/// <param name="sessions">The rotation writer (D5b).</param>
/// <param name="antiforgery">The service that mints the refreshed pair on a rotation, and the one that proves the
/// presented pair before it.</param>
/// <param name="clock">The clock the rotated cookie's browser lifetime is measured from.</param>
internal sealed class SessionRequestAuthenticator(
    ISessionAuthentication authentication,
    ISessions sessions,
    SessionSecrets secrets,
    IAntiforgery antiforgery,
    TimeProvider clock) : ISessionRequestAuthenticator
{
    /// <summary>The request slot this request's outcome is memoized in (one answer per request, whatever calls it).</summary>
    private static readonly object OutcomeItemsKey = new();

    /// <inheritdoc />
    public ValueTask<SessionRequestOutcome> AuthenticateAsync(HttpContext http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (http.Items.TryGetValue(OutcomeItemsKey, out var memoized)
            && memoized is Task<SessionRequestOutcome> inFlight)
        {
            // The antiforgery enforcement, the tenant filter and an endpoint may all ask; the second caller awaits
            // the first one's statement instead of opening a second (and, on a rotation, a second rotation).
            return new ValueTask<SessionRequestOutcome>(inFlight);
        }

        var outcome = ResolveAsync(http, cancellationToken);
        http.Items[OutcomeItemsKey] = outcome;

        return new ValueTask<SessionRequestOutcome>(outcome);
    }

    private async Task<SessionRequestOutcome> ResolveAsync(HttpContext http, CancellationToken cancellationToken)
    {
        // T38: a bearer request is never authenticated by its cookie. Asked FIRST, before the cookie is even read,
        // so a browser that holds a session cannot turn a CI/CLI request into a cookie-authenticated one. Not
        // counted: no session authentication was attempted, so no outcome exists to count (D9).
        if (http.Request.Headers.ContainsKey(HeaderNames.Authorization))
        {
            return Refuse(SessionErrors.Unauthenticated);
        }

        var presented = http.Request.Cookies[SessionCookieNames.Session];
        if (string.IsNullOrWhiteSpace(presented))
        {
            return Refuse(SessionErrors.Unauthenticated);
        }

        var authenticated = await authentication.AuthenticateAsync(presented, cancellationToken).ConfigureAwait(false);

        if (!authenticated.IsSuccess)
        {
            // No pepper, or the database could not answer: the credential was not judged, and neither is it
            // accepted. 503 with Retry-After, never a 401 that would sign the user out of a healthy session.
            // The store already counted this outcome (S1's contract); counting it here would double it.
            return Refuse(authenticated.Error!);
        }

        var session = authenticated.Value;

        switch (session.Status)
        {
            case SessionAuthStatus.Active:
                break;
            case SessionAuthStatus.Expired:
                return Refuse(SessionErrors.SessionExpired);
            case SessionAuthStatus.Revoked:
                return Refuse(SessionErrors.SessionRevoked);
            case SessionAuthStatus.Disabled:
                return Refuse(SessionErrors.AccountDisabled);
            default:
                return Refuse(SessionErrors.Unauthenticated);
        }

        // The session the pair THIS request presented was minted under (D6). Published before any rotation and
        // never overwritten: the mutation that performs a rotation must still validate the pair the client sent
        // for the pre-rotation session, while the response's refreshed pair is minted for the new one.
        SessionAntiforgeryDataProvider.PublishPresented(http, session.SessionId);

        if (!session.RotationRequired)
        {
            // The store counted this authenticated outcome inside the one statement that judged it (S1). The proof
            // is lazy (P2): the one HMAC is paid only if a handler that must re-resolve this session asks for it.
            return SessionRequestOutcome.Live(session.UserId, session.SessionId, () => ProofOf(presented));
        }

        // R1/SEC-1: the presented pair is proved BEFORE the rotation. A rotation-required session is still the
        // credential, so an in-scope mutation must prove the pair minted for it — otherwise a cross-site forged
        // mutation would revoke the old row, mint a successor and answer with a Set-Cookie, and only then report
        // 403. On a failed pair nothing happens at all: no session_rotate, no cookie, the flag untouched.
        if (SessionXsrfPolicy.IsInScope(http)
            && !await SessionXsrfPolicy.ValidatesAsync(http, antiforgery, cancellationToken).ConfigureAwait(false))
        {
            // Counted HERE because this layer answers the refusal: the middleware returns on a refusal and counts
            // nothing. The store already counted `active` for this request's authenticate statement, so a refused
            // rotation shows up once as `active` and once as its refusal code (telemetry only, delta-review note).
            SessionTelemetry.AuthOutcome(ApiErrorCode.CsrfFailed);

            return Refuse(SessionErrors.CsrfFailed);
        }

        var rotated = await sessions
            .RotateAsync(new SessionRotation(session.SessionId), cancellationToken)
            .ConfigureAwait(false);

        if (!rotated.IsSuccess)
        {
            // Single-winner (D5b) and R2: the loser of the race — a session that is not live — is 401
            // session_revoked, because the presented cookie is exactly what it is. A disabled account is 403
            // account_disabled: an authorization answer, never a credential one. An outage (or a code this build
            // does not map) is 503: it never signs a user out of a session nobody judged. Never authenticate on
            // the old cookie.
            var failure = rotated.Error!;

            return Refuse(failure.Code switch
            {
                ApiErrorCode.AccountDisabled => SessionErrors.AccountDisabled,
                ApiErrorCode.SessionRevoked => SessionErrors.SessionRevoked,
                _ => SessionErrors.Unavailable,
            });
        }

        var ticket = rotated.Value;

        // The same writer the login completion uses, so a rotated cookie can never carry a different attribute
        // set (D1) — and the refreshed pair is bound to the NEW session id, so the predecessor's token dies with
        // the predecessor.
        SessionCookieWriter.Write(http, ticket, SessionCookieWriter.RemainingAbsolute(ticket), antiforgery, clock);

        // No Rotated() here: the store counts a rotation inside the call that performs it (S1's contract), and one
        // privilege change is one rotation however many layers observe it.
        //
        // The proof is of the NEW cookie: the presented value is dead the moment the old row was revoked, so a
        // handler that resolves the session server-side (link confirm, D8/SEC2) must be handed the successor's
        // hash, never a hash of what the browser sent (orchestrator ruling). Still lazy (P2): computed only if
        // such a handler asks.
        return SessionRequestOutcome.Live(session.UserId, ticket.SessionId, () => ProofOf(ticket.CookieValue));
    }

    /// <summary>
    /// The stored HMAC of a live cookie value under the CURRENT pepper: the same bytes the store compares, and the
    /// only thing a handler may carry when it must re-resolve this session server-side.
    /// </summary>
    /// <param name="cookieValue">The value the browser holds (presented, or the one just issued).</param>
    /// <remarks>
    /// <see cref="SessionSecrets.HashCurrent"/> is exactly what the <c>session.cookie_hmac</c> column stores —
    /// the same call the store makes (`SessionStore`), so no second derivation exists — and it is the value the row
    /// holds after the store's own re-hash in the statement that judged this request. A deployment with no pepper
    /// cannot have authenticated the cookie at all, so the throw inside it is unreachable from here.
    /// </remarks>
    private byte[] ProofOf(string cookieValue) =>
        secrets.HashCurrent(Encoding.ASCII.GetBytes(cookieValue)).Hash;

    /// <summary>The refusal outcome for <paramref name="error"/>: no caller, no session, no proof.</summary>
    /// <param name="error">The 00 §9.2 error the request answers.</param>
    private static SessionRequestOutcome Refuse(Error error) => SessionRequestOutcome.Refused(error);
}
