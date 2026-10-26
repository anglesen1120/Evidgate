using Evidgate.Modules.Identity.Sessions.Http;

namespace Evidgate.Modules.Identity.Login.Completion;

/// <summary>
/// The login route's view of the session cookie name (02.3.1 §5, 00 §8.9/§9.1), kept as one constant so a rename
/// has to fail one test rather than silently pass two.
/// </summary>
/// <remarks>
/// The WRITER moved to <see cref="SessionCookieWriter"/> in 02.3.2-T1 (D1): the login completion and the
/// privilege-change rotation must write one attribute set, so there is exactly one <c>Cookies.Append</c> for
/// <c>__Host-evidgate_session</c> in the module. What remains here is the name the protocol code reads and the
/// suites assert against.
/// </remarks>
internal static class SessionCookie
{
    /// <summary>The cookie's name, with the <c>__Host-</c> prefix that pins host, path and transport.</summary>
    internal const string Name = SessionCookieNames.Session;
}
