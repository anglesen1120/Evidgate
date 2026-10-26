namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// The session-revocation seam of 02.1.2 §3 / 02.3.2-T1: removing or disabling a
/// member must end their live sessions in the <em>same</em> transaction as the
/// membership write, or the next request could still be served from a session the
/// change should have killed (AC3, AC4). C7's outbox is deliberately not used: an
/// asynchronous revocation would be a window, not a guarantee.
/// <para>
/// <b>Implemented and registered.</b> <c>SessionRevocationStore</c> (module
/// <c>Evidgate.Modules.Identity.Sessions</c>) runs the <c>SECURITY DEFINER</c>
/// <c>evidgate_resolve.session_revoke_all_for_user</c> function of migration 0045 on
/// the ambient tenant scope's connection and transaction, which is what makes 02.1.2-T3's
/// removal atomic with the revocation; the <c>session</c> table itself grants the app
/// role no privilege at all (0041), so the definer is the only writer.
/// </para>
/// </summary>
public interface ISessionRevocation
{
    /// <summary>
    /// Revokes every live session of <paramref name="userId"/>. Called on the
    /// caller's tenant scope's connection and transaction, so a rollback of the
    /// membership change also rolls the revocation back.
    /// </summary>
    /// <param name="userId">The user whose sessions must end.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>The number of sessions revoked.</returns>
    ValueTask<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken);
}
