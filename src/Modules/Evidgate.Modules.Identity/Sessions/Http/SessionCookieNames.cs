namespace Evidgate.Modules.Identity.Sessions.Http;

/// <summary>
/// The wire names of the session credential and its antiforgery pair (02.3.2-T1, D1/D6, 00 §9.1). They live in
/// one place because three writers and one reader must agree byte for byte: the login completion and the
/// rotation path set them, the enforcement middleware reads them, and the browser refuses a <c>__Host-</c>
/// cookie whose name, path or transport disagrees with the prefix's rule.
/// </summary>
/// <remarks>
/// <para>
/// <b>Session.</b> <see cref="Session"/> is the opaque reference to a server-side <c>session</c> row
/// (<c>HttpOnly</c>, so no script can read it), and the <c>__Host-</c> prefix makes the browser itself reject
/// the cookie unless it carries <c>Secure</c>, <c>Path=/</c> and no <c>Domain</c> — a deployment that gets one
/// of the three wrong sees a login loop in a real browser, not a silently travelling credential.
/// </para>
/// <para>
/// <b>Antiforgery.</b> <see cref="Antiforgery"/> is ASP.NET Core's own cookie-token (<c>HttpOnly</c>: nothing
/// in the page reads it) and <see cref="Xsrf"/> is the half the page script must copy into
/// <see cref="XsrfHeader"/> — so the request token is deliberately NOT <c>HttpOnly</c> and deliberately NOT
/// <c>__Host-</c> (Angular's <c>withXsrfConfiguration</c> defaults are exactly these two names). The pair is
/// bound to the session id through <c>IAntiforgeryAdditionalDataProvider</c>, so a token issued for one
/// session never validates another.
/// </para>
/// </remarks>
internal static class SessionCookieNames
{
    /// <summary>The session cookie (02.3.1 §5/00 §8.9): the opaque reference to the <c>session</c> row.</summary>
    internal const string Session = "__Host-evidgate_session";

    /// <summary>The request-token cookie a page script reads and echoes in <see cref="XsrfHeader"/>.</summary>
    internal const string Xsrf = "XSRF-TOKEN";

    /// <summary>ASP.NET Core's antiforgery cookie, paired with <see cref="Xsrf"/>.</summary>
    internal const string Antiforgery = "__Host-evidgate_af";

    /// <summary>The header a cookie-authenticated mutation must carry (00 §9.1, T38).</summary>
    internal const string XsrfHeader = "X-XSRF-TOKEN";

    /// <summary>
    /// The only path a <c>__Host-</c> cookie may carry, and therefore the path of both the session and the
    /// antiforgery cookie (the <c>__Host-</c> prefix refuses anything else).
    /// </summary>
    internal const string HostCookiePath = "/";
}
