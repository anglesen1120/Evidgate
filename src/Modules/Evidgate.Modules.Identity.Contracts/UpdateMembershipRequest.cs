namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// Body of <c>PATCH /v1/orgs/{o}/memberships/{m}</c> (02.1.2 §5). The membership's
/// mutable state is its status: <c>removed</c> is the admin disable path (AC3), and
/// it is the only transition this route performs — activating a pending invite is
/// the invite-binding flow of 02.1.2-T2 (T75), never a client-set status. The row's
/// <c>version</c> travels in <c>If-Match</c>; a stale value is 412.
/// </summary>
/// <param name="Status">The target status; only <see cref="MembershipStatus.Removed"/> is accepted.</param>
public sealed record UpdateMembershipRequest(string Status);
