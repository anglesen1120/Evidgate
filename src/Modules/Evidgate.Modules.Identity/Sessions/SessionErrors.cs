using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Identity.Sessions;

/// <summary>
/// The fixed answers of the session lifecycle (02.3.2 §5, §8, 00 §9.2): a cookie that is not a live session, a
/// session whose account was disabled, a failed antiforgery check, and the two failures of a write that cannot
/// run at all. Every message is written here, carries no user data and is the same whatever the cause, so no
/// route becomes an oracle for the state of an account or of a deployment.
/// </summary>
/// <remarks>
/// A session's state is a VALUE, not a failure of the lookup: <c>ISessionAuthentication</c> answers
/// <c>Result.Success</c> with <c>SessionAuthResult.Status</c>, and the caller renders the matching member of
/// this class. The 401 family is what the browser acts on (drop the cookie, sign in again); the 403 family is
/// what the person acts on.
/// </remarks>
internal static class SessionErrors
{
    /// <summary>
    /// 401 <c>unauthenticated</c>: no live session carries the presented value — it was never issued, or the
    /// cookie is missing. One code for both, so a bogus value and an absent cookie are indistinguishable.
    /// </summary>
    public static Error Unauthenticated { get; } = new(
        ApiErrorCode.Unauthenticated,
        "sign in to continue",
        StatusCodes.Status401Unauthorized);

    /// <summary>
    /// 401 <c>session_expired</c> (02.3.2 AC2, AC3): the row is past its sliding or its absolute deadline, so
    /// activity can no longer revive it. Not retryable — the browser signs in again.
    /// </summary>
    public static Error SessionExpired { get; } = new(
        ApiErrorCode.SessionExpired,
        "this session has expired; sign in again",
        StatusCodes.Status401Unauthorized);

    /// <summary>
    /// 401 <c>session_revoked</c> (02.3.2 AC4, AC8): the row was revoked — logout, revoke, a rotation or a
    /// member removal — and a replay of the old cookie stays refused. Not retryable.
    /// </summary>
    public static Error SessionRevoked { get; } = new(
        ApiErrorCode.SessionRevoked,
        "this session is no longer valid; sign in again",
        StatusCodes.Status401Unauthorized);

    /// <summary>
    /// 403 <c>account_disabled</c>: the session may be live, but <c>user.status</c> is <c>disabled</c> (02.3.2
    /// D7, closing the hand-over row of 02.3.1-T2), so every route — the link routes included — answers 403
    /// instead of pretending the caller is anonymous. Not retryable: only an administrator re-enables the
    /// account.
    /// </summary>
    public static Error AccountDisabled { get; } = new(
        ApiErrorCode.AccountDisabled,
        "this account is disabled; ask an administrator to restore it",
        StatusCodes.Status403Forbidden);

    /// <summary>
    /// 403 <c>csrf_failed</c> (02.3.2 AC5, T38): a cookie-authenticated mutation carried no matching
    /// <c>X-XSRF-TOKEN</c>. Not retryable with the same request; the client re-reads the pair and retries,
    /// which is a new request.
    /// </summary>
    public static Error CsrfFailed { get; } = new(
        ApiErrorCode.CsrfFailed,
        "the request's antiforgery token is missing or does not match; reload the page and try again",
        StatusCodes.Status403Forbidden);

    /// <summary>
    /// 503 <c>unavailable</c>: the pepper is not provisioned, the user row is gone, or the write failed
    /// (database or timeout). <c>Retryable</c> means a fresh attempt may succeed shortly — never that the same
    /// login callback may be replayed: <c>LoginEndpoints.CallbackAsync</c> deletes the pending-login cookie and
    /// consumes the one-time <c>state</c> before any completion runs, so a replay of that callback is 401
    /// <c>token_invalid</c> and the browser restarts the login from <c>start</c>. What the session key does
    /// guarantee is that a later login cannot alias an existing session: the row id is minted per attempt and
    /// the cookie hash is unique (0042).
    /// </summary>
    public static Error Unavailable { get; } = new(
        ApiErrorCode.Unavailable,
        "the sign-in cannot be completed right now; retry shortly",
        StatusCodes.Status503ServiceUnavailable,
        Retryable: true);
}
