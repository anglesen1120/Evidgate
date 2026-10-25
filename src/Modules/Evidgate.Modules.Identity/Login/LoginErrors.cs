using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Identity.Login;

/// <summary>
/// The fixed answers of the login routes (02.3.1 §5, AC3, AC8): an invalid callback, an unusable provider, an
/// unknown route value, and the completion's own refusals (no membership, a disabled account). Every message
/// is written here and none is taken from the provider or the database: no error query value, code, token,
/// subject, email or SQL text can reach a response body.
/// </summary>
/// <remarks>
/// The two helpers are also the single place a failed attempt is counted
/// (<see cref="EvidgateTelemetry.RecordLoginAttempt"/>), so the route code cannot forget one outcome or
/// count the same attempt twice with different codes.
/// </remarks>
internal static class LoginErrors
{
    /// <summary>
    /// 503 <c>unavailable</c>, retryable, with <c>Retry-After</c>: the provider is unconfigured, a call to
    /// it timed out or failed, or the deployment has no <see cref="LoginOptions.PublicBaseUrl"/> (AC8). The
    /// same answer serves every one of those causes, so the route is no oracle for the deployment's
    /// configuration.
    /// </summary>
    /// <remarks>
    /// The completion reuses this value for its own infrastructure faults (a database failure while the
    /// session is written, 02.3.1-T1.4), so a caller sees one 503 shape and one fixed message on the whole
    /// path — and never a database, SQL or host name in the body.
    /// </remarks>
    public static Error ProviderUnavailable { get; } = new(
        ApiErrorCode.Unavailable,
        "the identity provider is not available; retry shortly",
        StatusCodes.Status503ServiceUnavailable,
        Retryable: true);

    /// <summary>
    /// 401 <c>token_invalid</c>: the pending-login cookie is missing, expired or tampered with, the
    /// <c>state</c> does not match or was already used, the callback named another provider, the provider
    /// reported an error, the code is missing or oversized, or the provider rejected the code.
    /// </summary>
    public static Error TokenInvalid { get; } = new(
        ApiErrorCode.TokenInvalid,
        "the sign-in could not be verified; start it again",
        StatusCodes.Status401Unauthorized);

    /// <summary>404 <c>not_found</c>: no provider is registered under the route value (a lower-case exact match).</summary>
    public static Error UnknownProvider { get; } = new(
        ApiErrorCode.NotFound,
        "no login provider is registered under that name",
        StatusCodes.Status404NotFound);

    /// <summary>
    /// 403 <c>no_membership</c>: the identity is verified but the account holds no membership in an active
    /// organization (AC3, 02 §4.3). No session row and no session cookie are created; the user row the
    /// login's upsert wrote stays, so a later invite binds to it (02.3.1-T2).
    /// </summary>
    public static Error NoMembership { get; } = new(
        ApiErrorCode.NoMembership,
        "this account belongs to no organization that admits it; ask an administrator for an invitation",
        StatusCodes.Status403Forbidden);

    /// <summary>
    /// 403 <c>account_disabled</c>: <c>user.status</c> is <c>disabled</c> (02.3.1 §6), or the session store
    /// refused the account for the same reason. Not retryable — an administrator has to re-enable it.
    /// </summary>
    public static Error AccountDisabled { get; } = new(
        ApiErrorCode.AccountDisabled,
        "this account is disabled; contact an administrator",
        StatusCodes.Status403Forbidden);

    /// <summary>The 401 for one callback, so every invalid path answers with the same code and message.</summary>
    /// <param name="provider">The provider the outcome is counted under (a registered name, never a raw route value).</param>
    public static Result Invalid(string provider)
    {
        EvidgateTelemetry.RecordLoginAttempt(provider, ApiErrorCode.TokenInvalid);

        return Result.Failure(TokenInvalid);
    }

    /// <summary>The 503 for one route, with the deployment's <c>Retry-After</c> set on the response.</summary>
    /// <param name="context">The response that carries the header.</param>
    /// <param name="provider">The provider the outcome is counted under.</param>
    /// <param name="options">The bound login options (<see cref="LoginOptions.RetryAfterSeconds"/>).</param>
    public static Result Unavailable(HttpContext context, string provider, LoginOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        context.Response.Headers.RetryAfter = options.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        EvidgateTelemetry.RecordLoginAttempt(provider, ApiErrorCode.Unavailable);

        return Result.Failure(ProviderUnavailable);
    }
}
