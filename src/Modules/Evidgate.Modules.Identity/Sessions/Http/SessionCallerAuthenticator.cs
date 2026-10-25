using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Evidgate.Modules.Identity.Sessions.Http;

/// <summary>
/// The <see cref="ICallerAuthenticator"/> of a browser session (02.3.2-T1, D2): a live session becomes a
/// <see cref="PrincipalKind.Member"/> principal whose user id is the one the <c>session</c> row names, never a
/// value taken from the route or the body. It is also the host's <see cref="ICallerRefusalAuthenticator"/>, so a
/// revoked, expired or unknown cookie answers its own 401 instead of the pipeline's generic one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two questions, two gates.</b> Authenticating answers "is there a caller?" and must abstain whenever the
/// session is not usable: another authenticator (a <c>ci_producer</c> bearer, 02.4.x) may still authenticate the
/// request, and returning null here is what lets the filter try it. Refusing answers "is the credential the
/// client PRESENTED acceptable?" and therefore stays silent when no session cookie was presented at all — a
/// bearer request, or an anonymous one — because the absence of this credential says nothing about the others.
/// </para>
/// <para>
/// <b>One statement.</b> Both gates go through the memoized <see cref="ISessionRequestAuthenticator"/>, so the
/// refusal pass, the authentication pass, the antiforgery middleware and an endpoint all share one database round
/// trip per request (P1/P3) — and one rotation, when the session demands it.
/// </para>
/// </remarks>
/// <param name="authenticator">The request-path session authenticator this adapter projects.</param>
/// <param name="accessor">The current request, from which the cookie is read.</param>
internal sealed class SessionCallerAuthenticator(
    ISessionRequestAuthenticator authenticator,
    IHttpContextAccessor accessor) : ICallerAuthenticator, ICallerRefusalAuthenticator
{
    /// <inheritdoc />
    public async ValueTask<AuthenticatedCaller?> AuthenticateAsync(
        ICallerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (accessor.HttpContext is not { } http)
        {
            return null;
        }

        var outcome = await authenticator.AuthenticateAsync(http, cancellationToken).ConfigureAwait(false);

        return outcome.Refusal is null && outcome.UserId is { } userId
            ? new AuthenticatedCaller(RequestPrincipal.Member(userId))
            : null;
    }

    /// <inheritdoc />
    public async ValueTask<Error?> RefuseAsync(ICallerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (accessor.HttpContext is not { } http || !PresentsSessionCookie(http))
        {
            // No session credential on this request: nothing to refuse, and another authenticator may still own it.
            return null;
        }

        var outcome = await authenticator.AuthenticateAsync(http, cancellationToken).ConfigureAwait(false);

        return outcome.Refusal;
    }

    /// <summary>
    /// Whether the request carries the session credential at all. A bearer request does not, whatever cookie the
    /// browser happens to hold (TL T38), so it is never refused by this seam — and never authenticated by it.
    /// </summary>
    /// <param name="http">The request.</param>
    private static bool PresentsSessionCookie(HttpContext http) =>
        !http.Request.Headers.ContainsKey(HeaderNames.Authorization)
        && !string.IsNullOrWhiteSpace(http.Request.Cookies[SessionCookieNames.Session]);
}
