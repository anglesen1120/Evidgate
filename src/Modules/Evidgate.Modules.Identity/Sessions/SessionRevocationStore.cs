using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Identity.Contracts;

namespace Evidgate.Modules.Identity.Sessions;

/// <summary>
/// The only implementations of <see cref="ISessionRevocation"/> and <see cref="ISessionPrivilegeChange"/>
/// (02.3.2-T1.1, AC4/AC7): both run the <c>SECURITY DEFINER</c> functions of migration 0045 on the CALLER's
/// tenant scope — connection and transaction — so a membership removal or a role change and the sessions it
/// affects commit or roll back together.
/// </summary>
/// <remarks>
/// <para>
/// The tenant scope is the rule, not a convenience: 02.1.2-T3 removes a member inside one transaction, and an
/// asynchronous revocation (an outbox message) would leave a window in which the removed member's next request
/// is still served. <c>ITenantDb</c> binds every statement to that scope's transaction, so this class never
/// opens a connection of its own.
/// </para>
/// <para>
/// The <c>session</c> table grants the app role no privilege at all (0041), and the definer is owned by
/// <c>evidgate_owner</c>; <c>evidgate_app</c> holds EXECUTE only. A missing scope therefore throws the
/// framework's <see cref="TenantScopeMissingException"/> instead of silently revoking nothing.
/// </para>
/// </remarks>
/// <param name="db">The Dapper seam bound to the ambient tenant scope.</param>
/// <param name="clock">The clock the two statements' <c>p_now</c> comes from.</param>
internal sealed class SessionRevocationStore(ITenantDb db, TimeProvider clock) : ISessionRevocation, ISessionPrivilegeChange
{
    /// <inheritdoc />
    public async ValueTask<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var revoked = await db.ScalarAsync<int>(
            "select revoked_count from evidgate_resolve.session_revoke_all_for_user(@user_id, @p_now)",
            new { user_id = userId, p_now = clock.GetUtcNow().ToUniversalTime() },
            cancellationToken).ConfigureAwait(false);

        // A write count, like the attempt counters: a later rollback of the caller's transaction is not
        // subtracted (the database decided the number, telemetry reports what was attempted).
        SessionTelemetry.Revoked(revoked);

        return revoked;
    }

    /// <inheritdoc />
    public async ValueTask<int> RequireRotationForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var marked = await db.ScalarAsync<int>(
            "select marked_count from evidgate_resolve.session_require_rotation(@user_id, @p_now)",
            new { user_id = userId, p_now = clock.GetUtcNow().ToUniversalTime() },
            cancellationToken).ConfigureAwait(false);

        return marked;
    }
}
