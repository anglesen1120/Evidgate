using Evidgate.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Identity.Sessions.Http;

/// <summary>
/// The one writer of the session and antiforgery cookies (02.3.2-T1, D1/D6). Both issuing paths — the login
/// completion and the rotation this slice performs on the request path — call these methods, so the two can
/// never drift into two attribute sets.
/// </summary>
/// <remarks>
/// <para>
/// <b>Attributes.</b> <c>__Host-evidgate_session</c> is <c>HttpOnly</c>, <c>Secure</c>, <c>SameSite=Lax</c>,
/// <c>Path=/</c>, with no <c>Domain</c> and a <c>Max-Age</c> (and the matching <c>Expires</c>, for clients that
/// predate <c>Max-Age</c>): <c>__Host-</c> demands the first three of those, and the lifetime is the session's
/// ABSOLUTE deadline, because the row — not the browser — is what slides (D1; a cookie that outlives its row
/// only costs one 401 <c>session_expired</c>, while a cookie that dies before an active row signs an active user
/// out).
/// </para>
/// <para>
/// <b>Value.</b> The value is the one-time <see cref="SessionTicket.CookieValue"/> the store minted and keeps
/// only as an HMAC. It is written here and nowhere else: never logged, never in a body, never in a metric, never
/// in an audit row (D9).
/// </para>
/// <para>
/// <b>Antiforgery pair.</b> Every issuance of a session cookie also issues the pair (D6): ASP.NET Core's
/// <c>__Host-evidgate_af</c> cookie-token plus the script-readable <c>XSRF-TOKEN</c> request token, both bound to
/// the NEW session id. The id is published into <see cref="HttpContext.Items"/> first, because
/// <see cref="SessionAntiforgeryDataProvider"/> is what puts it inside the token — so the token a rotated
/// session receives validates against the rotated session and never against its predecessor.
/// </para>
/// </remarks>
internal static class SessionCookieWriter
{
    /// <summary>
    /// Writes the session cookie for <paramref name="ticket"/> and, in the same response, the antiforgery pair
    /// bound to the session it names.
    /// </summary>
    /// <param name="http">The request whose response carries the cookies.</param>
    /// <param name="ticket">The session the store just created or rotated.</param>
    /// <param name="absoluteExpiresAt">
    /// The browser-visible deadline: the session's absolute expiry. A fresh login knows it exactly
    /// (<c>now + SessionOptions.AbsoluteLifetime</c>); see <see cref="RemainingAbsolute"/> for why a rotation
    /// uses the ticket's own deadline instead.
    /// </param>
    /// <param name="antiforgery">The antiforgery service that mints the pair.</param>
    /// <param name="clock">The clock the browser lifetime is measured from (the same one the store wrote the row with).</param>
    internal static void Write(
        HttpContext http,
        SessionTicket ticket,
        DateTimeOffset absoluteExpiresAt,
        IAntiforgery antiforgery,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(antiforgery);
        ArgumentNullException.ThrowIfNull(clock);

        WriteSessionCookie(http, ticket.CookieValue, absoluteExpiresAt, clock);

        // Before GetAndStoreTokens: the additional-data provider reads the issuance slot when it mints the token,
        // so the pair is bound to the session it is being issued for — the new id after a rotation, never the
        // predecessor the request authenticated as (which validation still compares against).
        SessionAntiforgeryDataProvider.PublishIssuance(http, ticket.SessionId);
        SessionAntiforgeryDataProvider.Issue(http, antiforgery);
    }

    /// <summary>
    /// The deadline to write for a rotated session: the row's own absolute deadline, which a rotation INHERITS from
    /// its predecessor (D5) and which <see cref="SessionTicket.AbsoluteExpiresAt"/> now carries.
    /// </summary>
    /// <param name="ticket">The rotated session's ticket.</param>
    /// <remarks>
    /// Until S1 widened the ticket this path had to write the sliding deadline
    /// (<c>least(now + idle, absolute_expires_at)</c>) as the conservative stand-in; with the absolute deadline on
    /// the ticket the browser keeps the cookie exactly as long as the row can possibly live (addendum (b)). The
    /// fallback stays for a ticket built without it (<c>default</c>): writing the sliding deadline then is still
    /// never LATER than the real absolute deadline, so a rotation can never extend a session past the deadline its
    /// predecessor already had.
    /// </remarks>
    internal static DateTimeOffset RemainingAbsolute(SessionTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return ticket.AbsoluteExpiresAt == default ? ticket.ExpiresAt : ticket.AbsoluteExpiresAt;
    }

    /// <summary>
    /// Expires the session cookie and the antiforgery pair on this response (02.3.2-T2: logout, and
    /// <c>revoke-all</c> with <c>keep_current: false</c>).
    /// </summary>
    /// <param name="http">The request whose response carries the expiries.</param>
    /// <remarks>
    /// <para>
    /// <b>Same attributes as the issue.</b> A browser only drops a cookie whose name, path, transport and site
    /// rules match the one it stored — and the <c>__Host-</c> prefix makes that a hard rule — so every
    /// attribute here is the one <see cref="WriteSessionCookie"/> and <see cref="SessionAntiforgeryDataProvider"/>
    /// issued: <c>Secure</c>, <c>Path=/</c>, no <c>Domain</c>, the same <c>SameSite</c> per cookie, and only the
    /// two script-readable/HttpOnly flags the pair differs by.
    /// </para>
    /// <para>
    /// <b>Empty value, zero lifetime.</b> The cookie is written with an empty value and <c>Max-Age=0</c> (plus
    /// the matching <c>Expires</c> for older clients), which is the documented sign-out shape; no secret is
    /// carried out of the response. Called ONLY after the row was revoked, so a failed write leaves the
    /// credential the browser holds exactly as it was.
    /// </para>
    /// </remarks>
    internal static void Expire(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        ExpireCookie(http, SessionCookieNames.Session, httpOnly: true, SameSiteMode.Lax);
        ExpireCookie(http, SessionCookieNames.Antiforgery, httpOnly: true, SameSiteMode.Strict);
        ExpireCookie(http, SessionCookieNames.Xsrf, httpOnly: false, SameSiteMode.Lax);
    }

    /// <summary>
    /// The <c>__Host-evidgate_session</c> cookie, with exactly the attribute set the <c>__Host-</c> prefix
    /// requires and the lifetime of the row behind it.
    /// </summary>
    /// <param name="http">The request whose response carries the cookie.</param>
    /// <param name="cookieValue">The opaque, one-time value the store minted.</param>
    /// <param name="expiresAt">When the browser must drop it: the row's absolute (or, on rotation, sliding) deadline.</param>
    /// <param name="clock">The clock the remaining lifetime is measured from.</param>
    private static void WriteSessionCookie(
        HttpContext http,
        string cookieValue,
        DateTimeOffset expiresAt,
        TimeProvider clock)
    {
        var remaining = expiresAt - clock.GetUtcNow();

        http.Response.Cookies.Append(
            SessionCookieNames.Session,
            cookieValue,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = SessionCookieNames.HostCookiePath,
                IsEssential = true,
                // Max-Age is what the contract documents; Expires keeps older clients in step. A deadline already
                // in the past is written as zero rather than as a negative lifetime.
                MaxAge = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
                Expires = expiresAt,
            });
    }

    /// <summary>
    /// Writes one cookie as an expiry: empty value, <c>Max-Age=0</c> and an <c>Expires</c> in the past, with the
    /// attributes the issuing writer used (see <see cref="Expire"/>).
    /// </summary>
    /// <param name="http">The request whose response carries the cookie.</param>
    /// <param name="name">The cookie's name.</param>
    /// <param name="httpOnly">Whether the issued cookie was <c>HttpOnly</c>.</param>
    /// <param name="sameSite">The <c>SameSite</c> mode the issued cookie carried.</param>
    private static void ExpireCookie(HttpContext http, string name, bool httpOnly, SameSiteMode sameSite) =>
        http.Response.Cookies.Append(
            name,
            string.Empty,
            new CookieOptions
            {
                HttpOnly = httpOnly,
                Secure = true,
                SameSite = sameSite,
                Path = SessionCookieNames.HostCookiePath,
                IsEssential = true,
                MaxAge = TimeSpan.Zero,
                Expires = DateTimeOffset.UnixEpoch,
            });
}
