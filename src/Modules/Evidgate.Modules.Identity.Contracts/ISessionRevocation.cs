namespace Evidgate.Modules.Identity.Contracts;

/// <summary>
/// The session-revocation seam of 02.1.2 §3 / 02.3.2-T1: removing or disabling a
/// member must end their live sessions in the <em>same</em> transaction as the
/// membership write, or the next request could still be served from a session the
/// change should have killed (AC3). C7's outbox is deliberately not used: an
/// asynchronous revocation would be a window, not a guarantee.
/// <para>
/// The implementation (the <c>session</c> table of 02.3.2-T1) does not exist yet —
/// there is no <c>user</c>, <c>user_identity</c> or <c>session</c> table in this
/// build (02.3.x). This interface is the contract 02.3.2-T1 implements and
/// 02.1.2-T3.2 calls; until then no code calls it, and the removal path records the
/// deferral in the feature plan §3.
/// </para>
/// </summary>
public interface ISessionRevocation
{
    /// <summary>
    /// Revokes every live session of <paramref name="userId"/>. Called on the
    /// caller's tenant scope's connection and transaction, so a rollback of the
    /// membership change also rolls the revocation back.
    /// </summary>
    /// <returns>The number of sessions revoked.</returns>
    ValueTask<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken);
}
