using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Identity.Invites;

/// <summary>
/// The fixed answers of the invite routes (02.1.2-T2 §5): minting that cannot run, an invite that is
/// no longer pending, and a full org. Every message is written here and carries no tenant data, no
/// address and no secret.
/// </summary>
/// <remarks>
/// The 429 reuses Tenancy's envelope shape (<c>error.details[].path = max_seats</c>, reason
/// <c>reached</c>) without referencing that module — the identity module may not depend on it
/// (02 §5.5, architecture tests). Invite creation and the login binder's bind both build it through
/// <see cref="QuotaExceeded"/>, so an invite and a bind of the same full org are byte-for-byte the
/// same refusal.
/// </remarks>
internal static class InviteErrors
{
    /// <summary>
    /// 503 <c>unavailable</c>: no invite pepper is provisioned, so no token can be minted or hashed
    /// (<see cref="InviteSecrets.CanHash"/> false). Retryable because a deployment may provision it at
    /// any time; the same answer the CI key material and the session pepper give.
    /// </summary>
    public static Error NotConfigured { get; } = new(
        ApiErrorCode.Unavailable,
        "invite minting is not configured on this deployment",
        StatusCodes.Status503ServiceUnavailable,
        Retryable: true);

    /// <summary>
    /// 503 <c>unavailable</c>: three freshly generated prefixes all collided with a stored invite.
    /// Astronomically unlikely (62^12 prefixes); answered instead of a 500 so a client may retry.
    /// </summary>
    public static Error MintingUnavailable { get; } = new(
        ApiErrorCode.Unavailable,
        "a fresh invite token could not be minted right now; retry shortly",
        StatusCodes.Status503ServiceUnavailable,
        Retryable: true);

    /// <summary>
    /// 409 <c>invite_not_pending</c>: the membership is not <c>pending</c>, so it has no invite to
    /// resend or cancel (02.1.2 §6). An unknown or other-tenant id is 404 before this is ever built.
    /// </summary>
    public static Error NotPending { get; } = Error.Conflict(
        ApiErrorCode.InviteNotPending,
        "the membership has no pending invite: it is already bound, cancelled or expired");

    /// <summary>The 429 of a full org: <c>max_seats</c> seats are already taken (02.1.2-T2.3).</summary>
    /// <param name="limit">The org's <c>max_seats</c>; never null when this is built from a refusal.</param>
    public static Error QuotaExceeded(int? limit) => new(
        ApiErrorCode.QuotaExceeded,
        limit is { } value
            ? string.Create(CultureInfo.InvariantCulture, $"the organization's {SeatQuota.LimitName} limit ({value}) is reached")
            : $"the organization's {SeatQuota.LimitName} limit is reached",
        StatusCodes.Status429TooManyRequests,
        Retryable: false,
        Details: [new ErrorDetail(SeatQuota.LimitName, "reached")]);
}
