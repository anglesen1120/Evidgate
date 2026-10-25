using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Login.Linking;

/// <summary>
/// The fixed answers of the linking routes (02.3.1-T2.2, Q4): no session, no pending link, a refused
/// origin, an identity that belongs to an account still in use, a disabled account and an infrastructure
/// fault. Every message is written here and none is taken from the provider, the cookie or the database, so
/// nothing about a subject, an address, a token or a row can reach a response body.
/// </summary>
/// <remarks>
/// <para>
/// The codes are the 00 §9.2 catalogue's existing values; no code is invented for this story. The one
/// judgement call is the 409: <see cref="ApiErrorCode.BindingConflict"/> is the catalogue's "another party
/// already holds this binding" conflict, and an identity that belongs to another account which still has a
/// membership or a live session is exactly that — the contract surface of 02.3.1-T2.2 documents the mapping.
/// </para>
/// <para>
/// No outcome here is counted as a login attempt: the metric's vocabulary
/// (<c>EvidgateTelemetry.RecordLoginAttempt</c>) is a provider sign-in and measures the sign-in success
/// rate, and a link that borrowed it would inflate that rate. This slice adds no counter of its own, and the
/// shared callback records nothing for the two things a link can decide: a link it completed and a link its
/// seam refused (a 409 <c>binding_conflict</c>, a 401 <c>unauthenticated</c>) never reach the sign-in
/// outcome, neither as <c>completed</c> nor as the refusal code.
/// </para>
/// <para>
/// <b>Residual, by design of the shared route.</b> Refusals the callback raises itself — a state mismatch, a
/// replayed or foreign state cookie, the provider's own <c>error</c>, a rejected exchange, a provider
/// outage, and the 503 of an uncomposed link seam — are answered by the login errors of the shared route,
/// which do count a <c>token_invalid</c>/<c>unavailable</c> outcome. A state cookie that cannot be
/// unprotected has no purpose at all, so those refusals cannot be attributed to a sign-in or to a link in
/// the first place; the metric therefore counts them as it always has, and only a completed link attempt is
/// reliably absent from it.
/// </para>
/// <para>
/// The 503 helper is <see cref="UnavailableWithRetryAfter(HttpContext, LoginOptions)"/>: a 503 always carries
/// <c>Retry-After</c> (00 §9.2), and the value comes from <c>EVIDGATE__LOGIN__RETRYAFTERSECONDS</c>.
/// </para>
/// </remarks>
internal static class LinkErrors
{
    /// <summary>
    /// 404 <c>not_found</c>: nothing is pending for this session — no confirm cookie, an expired one, or one
    /// this browser never received. The route also deletes a presented value it cannot use.
    /// </summary>
    public static Error NothingPending { get; } = new(
        ApiErrorCode.NotFound,
        "no provider link is pending for this session",
        StatusCodes.Status404NotFound);

    /// <summary>
    /// 401 <c>token_invalid</c>: the confirm route was reached with no usable staged link — a missing,
    /// expired or foreign <c>__Host-evidgate_link</c> cookie, or a confirm cookie that belongs to another
    /// user. The link has to be started again, and nothing was written.
    /// </summary>
    public static Error LinkNotPending { get; } = new(
        ApiErrorCode.TokenInvalid,
        "the provider link could not be confirmed; start it again",
        StatusCodes.Status401Unauthorized);

    /// <summary>
    /// 403 <c>forbidden</c>: a request that named an <c>Origin</c> other than this deployment's public
    /// origin. Defence in depth on top of <c>SameSite=Lax</c>, not the primary CSRF control.
    /// </summary>
    public static Error ForeignOrigin { get; } = new(
        ApiErrorCode.Forbidden,
        "the request did not come from this application's origin",
        StatusCodes.Status403Forbidden);

    /// <summary>
    /// 409 <c>binding_conflict</c>: that <c>(provider, subject)</c> is already bound to ANOTHER account that
    /// still holds a membership or a live session, so it is never taken from it (AC6: no automatic merge,
    /// ever — the other person has to link their own account instead).
    /// </summary>
    public static Error IdentityInUse { get; } = new(
        ApiErrorCode.BindingConflict,
        "that provider identity already belongs to another account that is still in use",
        StatusCodes.Status409Conflict);

    /// <summary>
    /// 503 <c>unavailable</c>, retryable: a database fault, an audit write that could not commit, or a
    /// deployment without a session pepper. <see cref="UnavailableWithRetryAfter(HttpContext, LoginOptions)"/> is
    /// what the routes answer with, so <c>Retry-After</c> is always set.
    /// </summary>
    public static Error ServiceUnavailable { get; } = new(
        ApiErrorCode.Unavailable,
        "the identity store is not available; retry shortly",
        StatusCodes.Status503ServiceUnavailable,
        Retryable: true);

    /// <summary>The 503 for one route, with the deployment's <c>Retry-After</c> set on the response.</summary>
    /// <param name="context">The response that carries the header.</param>
    /// <param name="options">The bound login options (<see cref="LoginOptions.RetryAfterSeconds"/>).</param>
    public static Error UnavailableWithRetryAfter(HttpContext context, LoginOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        context.Response.Headers.RetryAfter = options.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        return ServiceUnavailable;
    }
}
