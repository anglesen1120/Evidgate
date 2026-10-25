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
/// (<c>EvidgateTelemetry.RecordLoginAttempt</c>) is a provider sign-in, and a link that borrows it would
/// inflate the sign-in success rate. The callback counts the attempt it completed, as it does for a sign-in.
/// </para>
/// </remarks>
internal static class LinkErrors
{
    /// <summary>
    /// 401 <c>unauthenticated</c>: no <c>__Host-evidgate_session</c> cookie, or one that is unknown,
    /// expired, revoked or belongs to an account disabled after it was minted (the resolver's
    /// <c>user.status = 'active'</c> filter makes the last two indistinguishable, and both answer this).
    /// </summary>
    public static Error Unauthenticated { get; } = new(
        ApiErrorCode.Unauthenticated,
        "sign in to link another sign-in provider",
        StatusCodes.Status401Unauthorized);

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
    /// 403 <c>account_disabled</c>: the account the link would attach the identity to is <c>disabled</c>
    /// (02.3.1 §6). Reachable only where the database reports the status itself
    /// (<c>login_link_identity</c>'s <c>disabled</c> outcome, i.e. the account was disabled between the
    /// session check and the confirm); the routes' own session lookup cannot tell a disabled account from
    /// an absent session and answers 401 there.
    /// </summary>
    public static Error AccountDisabled { get; } = new(
        ApiErrorCode.AccountDisabled,
        "this account is disabled; contact an administrator",
        StatusCodes.Status403Forbidden);

    /// <summary>
    /// 503 <c>unavailable</c>, retryable: a database fault, an audit write that could not commit, or a
    /// deployment without a session pepper. <see cref="Unavailable(HttpContext, LoginOptions)"/> is what the
    /// routes answer with, so <c>Retry-After</c> is always set.
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
