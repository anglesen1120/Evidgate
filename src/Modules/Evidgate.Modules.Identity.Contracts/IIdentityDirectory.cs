namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// Read side of the identity directory (02.1.2-T1.3): the membership facts other
/// modules consume without going through the Mediator pipeline. The implementation
/// runs inside the caller's <c>ITenantScope</c>, so every answer is scoped by
/// <c>org_id</c> row level security — an org never sees another org's members, and a
/// missing tenant context yields nothing (00 §5.4.7). The pre-tenant half of invite
/// binding (secret → org) is not here: it is
/// <c>evidgate_resolve.invite_by_secret</c> (02.6.1-T3), and the login opens the
/// tenant scope before calling this interface.
/// </summary>
public interface IIdentityDirectory
{
    /// <summary>
    /// The roles an <paramref name="orgId"/> membership holds for
    /// <paramref name="userId"/>, or an empty list when the user has no active
    /// membership there. Consumed by the login flow (02.3.1-T2).
    /// </summary>
    ValueTask<IReadOnlyList<MemberRole>> ListMemberRolesAsync(
        Guid orgId,
        Guid userId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The invites of <paramref name="orgId"/> addressed to
    /// <paramref name="invitedEmail"/> (normalised by the caller). Consumed by the
    /// login flow's invite check (02.3.1-T2), after the secret resolved the org.
    /// </summary>
    ValueTask<IReadOnlyList<PendingInvite>> ListPendingInvitesAsync(
        Guid orgId,
        string invitedEmail,
        CancellationToken cancellationToken);
}
