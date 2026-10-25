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
    /// 503 <c>unavailable</c>, retryable: the pepper is not provisioned, the user row is gone, or the write
    /// failed (database or timeout). Retrying the same callback is safe — the session key is the row id and
    /// the unique cookie hash, so a retry cannot alias another session.
    /// </summary>
    public static Error Unavailable { get; } = new(
        ApiErrorCode.Unavailable,
        "the sign-in cannot be completed right now; retry shortly",
        StatusCodes.Status503ServiceUnavailable,
        Retryable: true);
}
