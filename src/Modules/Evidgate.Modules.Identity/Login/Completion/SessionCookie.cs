using Evidgate.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Identity.Login.Completion;

/// <summary>
/// The session cookie a completed login writes (02.3.1 §5, 00 §8.9/§9.1): <c>__Host-evidgate_session</c>
/// with <c>HttpOnly</c>, <c>Secure</c>, <c>SameSite=Lax</c>, <c>Path=/</c> and no <c>Domain</c> — the three
/// attributes the browser's <c>__Host-</c> prefix demands, so a cookie set under another host or over plain
/// HTTP is refused by the browser instead of silently travelling.
/// </summary>
/// <remarks>
/// <para>
/// The value is the one-time <see cref="SessionTicket.CookieValue"/> the session store generated and stores
/// only as an HMAC: it is written here and nowhere else — never logged, never in a body, never in a metric.
/// </para>
/// <para>
/// <c>Expires</c> carries the ticket's sliding deadline, so the browser drops the cookie when the server-side
/// row would already refuse it. Nothing about rotation or revocation lives here: 02.3.2-T1 owns the session
/// lifecycle and re-issues the same cookie shape.
/// </para>
/// </remarks>
internal static class SessionCookie
{
    /// <summary>The cookie's name, with the <c>__Host-</c> prefix that pins host, path and transport.</summary>
    internal const string Name = "__Host-evidgate_session";

    /// <summary>The only path a <c>__Host-</c> cookie may carry.</summary>
    private const string CookiePath = "/";

    /// <summary>Writes <paramref name="ticket"/>'s cookie onto the callback's response.</summary>
    /// <param name="context">The callback's request, whose response carries the cookie.</param>
    /// <param name="ticket">The session the store just created.</param>
    public static void Write(HttpContext context, SessionTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ticket);

        context.Response.Cookies.Append(
            Name,
            ticket.CookieValue,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = CookiePath,
                IsEssential = true,
                Expires = ticket.ExpiresAt,
            });
    }
}
