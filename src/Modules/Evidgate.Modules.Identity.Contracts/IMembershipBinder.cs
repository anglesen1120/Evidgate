using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// The invite-binding seam of 02.1.2-T2 (T75): it turns a pending membership of a
/// named address into an active one, for the user who has just authenticated. The
/// login flow of 02.3.1-T2 calls it once after a successful callback; nothing calls
/// it in this build (the wiring is 02.3.1-T2's).
/// <para>
/// An email alone never binds. The invite's single-use secret
/// (<see cref="BindPendingMembershipsRequest.InviteToken"/>) plus a provider-verified
/// matching address is the only path, unless the org has explicitly opted in to
/// verified-domain auto-bind (<c>organization.invite_autobind_verified_domain</c>),
/// where a provider-verified address under the account's own hosted domain is
/// enough.
/// </para>
/// <para>
/// Every "this does not bind" cause — unknown prefix, wrong secret, the invite names
/// another address, expired, cancelled, already used, a user who is already a member —
/// is answered with the same empty <see cref="BindPendingMembershipsResult"/>; the row
/// stays <c>pending</c>. The only failure value is a reached seat limit (429
/// <c>quota_exceeded</c>). The implementation never logs the token, the secret or the
/// address.
/// </para>
/// </summary>
public interface IMembershipBinder
{
    /// <summary>
    /// Binds every pending invite this request legitimately resolves, one tenant
    /// transaction per org, and commits each before returning.
    /// </summary>
    /// <param name="request">The authenticated user and what the provider reported.</param>
    /// <param name="cancellationToken">Cancellation; a cancelled call commits nothing further.</param>
    /// <returns>The memberships activated, or 429 <c>quota_exceeded</c> when an org is full.</returns>
    ValueTask<Result<BindPendingMembershipsResult>> BindAsync(
        BindPendingMembershipsRequest request,
        CancellationToken cancellationToken);
}
