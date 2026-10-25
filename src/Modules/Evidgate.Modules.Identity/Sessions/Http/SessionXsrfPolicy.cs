using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;

namespace Evidgate.Modules.Identity.Sessions.Http;

/// <summary>
/// The antiforgery RULE of 02.3.2-T1 (D6/AC5), independent of the pipeline that applies it: which requests must
/// prove they came from the same page, which are deliberately outside the rule, and what "prove" means.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who must prove it.</b> A request is in scope exactly when its authority came from the session cookie: it
/// carries <c>__Host-evidgate_session</c>, no <c>Authorization</c> header (TL T38: a bearer request is never
/// cookie-authenticated), and a mutating method (<c>POST</c>/<c>PUT</c>/<c>PATCH</c>/<c>DELETE</c>).
/// <c>GET</c>/<c>HEAD</c>/<c>OPTIONS</c>/<c>TRACE</c> are safe methods and outside the rule by definition — an
/// antiforgery failure on a read would be a bug, not a protection.
/// </para>
/// <para>
/// <b>What proves nothing.</b> A request with no session cookie is not weakened by skipping the check: the routes
/// that need a caller answer 401 on their own, and the ones that need none (login, invite staging) have no cookie
/// authority to forge. That is also why <see cref="ExemptRoutePatterns"/> is one entry long.
/// </para>
/// <para>
/// <b>Why a route pattern and not a path.</b> The exemption is matched against the endpoint's route pattern, so it
/// cannot drift onto a sibling route by a prefix or a trailing slash, and a route that stops being served fails
/// the route-table test instead of silently keeping its exemption.
/// </para>
/// </remarks>
internal static class SessionXsrfPolicy
{
    /// <summary>
    /// The one documented exemption (02.3.2 §10 T1.2 Notes): invite staging SETS a credential cookie and
    /// authenticates with none, so there is no session for a token to be bound to and no ambient authority a
    /// forged request could borrow. Its real defence is the body contract, not a header check: the route refuses
    /// anything that is not <c>application/json</c> (422 <c>request_invalid</c>,
    /// <see cref="Endpoints.InviteTokenEndpoints"/>' own remarks), which is what stops the one encoding a
    /// cross-site form post can send without a CORS preflight, and a cross-site JSON fetch needs a preflight the
    /// app's CORS policy refuses. Kept in sync with the served-route test's expected set.
    /// </summary>
    internal static readonly string[] ExemptRoutePatterns = ["/v1/auth/invite-token"];

    /// <summary>
    /// Whether <paramref name="http"/> is a request whose authority is its session cookie and which therefore must
    /// pass the antiforgery pair.
    /// </summary>
    /// <param name="http">The request.</param>
    internal static bool IsInScope(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        return IsMutating(http.Request.Method)
            && !http.Request.Headers.ContainsKey(HeaderNames.Authorization)
            && !string.IsNullOrWhiteSpace(http.Request.Cookies[SessionCookieNames.Session])
            && !IsExempt(http);
    }

    /// <summary>
    /// Whether <paramref name="http"/>'s request token validates against the antiforgery pair bound to the session
    /// it authenticated as. A missing header, a mismatched token, a token minted for another session and an
    /// expired token all answer false and are one refusal to the client (no oracle).
    /// </summary>
    /// <param name="http">The request.</param>
    /// <param name="antiforgery">The antiforgery service.</param>
    /// <param name="cancellationToken">Request abort.</param>
    internal static async ValueTask<bool> ValidatesAsync(
        HttpContext http,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(antiforgery);
        cancellationToken.ThrowIfCancellationRequested();

        // The header is what this contract documents; a body form field is never read on a JSON API, and the
        // framework's own fallback does not touch the body for a non-form content type.
        return await antiforgery.IsRequestValidAsync(http).ConfigureAwait(false);
    }

    /// <summary>Whether the endpoint is annotated <see cref="XsrfExemptMetadata"/> or is a documented exemption.</summary>
    /// <param name="http">The request.</param>
    internal static bool IsExempt(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (http.GetEndpoint()?.Metadata.GetMetadata<XsrfExemptMetadata>() is not null)
        {
            return true;
        }

        // Route templates are compared without their leading slash: a group prefix produces "v1/auth/invite-token"
        // where the documented exemption is written "/v1/auth/invite-token".
        return http.GetEndpoint() is RouteEndpoint endpoint
            && ExemptRoutePatterns.Any(
                pattern => string.Equals(
                    pattern.TrimStart('/'),
                    (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/'),
                    StringComparison.Ordinal));
    }

    /// <summary>The methods that change state and therefore need the pair (00 §9.1, RFC 9110 safe methods).</summary>
    /// <param name="method">The request method.</param>
    internal static bool IsMutating(string method) =>
        HttpMethods.IsPost(method)
        || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method)
        || HttpMethods.IsDelete(method);
}
