using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Identity.Sessions;

/// <summary>
/// The fixed answers of session creation (02.3.1 §6, §8): a disabled account and a creation that cannot
/// run at all. Every message is written here, carries no user data and is the same whatever the cause, so
/// the login route is no oracle for the state of a deployment or of an account.
/// </summary>
internal static class SessionErrors
{
    /// <summary>
    /// 403 <c>account_disabled</c>: the account exists but <c>user.status</c> is <c>disabled</c> (02.3.1 §6),
    /// so no session is created and none is issued.
    /// </summary>
    public static Error AccountDisabled { get; } = new(
        ApiErrorCode.AccountDisabled,
        "this account is disabled; ask an administrator to restore it",
        StatusCodes.Status403Forbidden);

    /// <summary>
    /// 503 <c>unavailable</c>: the pepper is not provisioned, the user row is gone, or the write failed
    /// (database or timeout). <c>Retryable</c> means a fresh sign-in may succeed shortly — never that the same
    /// callback may be replayed: <c>LoginEndpoints.CallbackAsync</c> deletes the pending-login cookie and
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
