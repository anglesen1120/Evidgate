using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

internal static class MemberRankGuard
{
    /// <summary>
    /// 503 <c>unavailable</c>: another writer held a membership row past the lock timeout, or the two
    /// transactions deadlocked; the request changed nothing and may be retried.
    /// </summary>
    internal static Error Contention => new(
        ApiErrorCode.Unavailable,
        "the membership is being changed by another request; retry shortly",
        StatusCodes.Status503ServiceUnavailable,
        Retryable: true);

    internal static Error Refused => Error.Forbidden(
        "the caller's administrative rank must be at least the target's rank in this organization");

    /// <summary>
    /// Fail-closed entry point for the early, unlocked refusal: a request without a resolved member has
    /// no rank and is refused. The SQL functions remain the only definition of the rank rule, and the
    /// target is the membership row, whatever its status (a removed or pending row keeps its grants).
    /// </summary>
    internal static Task<bool> ActorOutranksOrEqualsAsync(
        ITenantDb db,
        Guid orgId,
        IRequestPrincipal? actor,
        Guid targetMembershipId,
        CancellationToken cancellationToken) =>
        CompareAsync(db, orgId, actor, targetMembershipId, locked: false, cancellationToken);

    /// <summary>
    /// The authoritative comparison, made atomic: the target and the actor's membership rows are locked
    /// (one statement, ordered by id, so two callers cannot lock them in opposite orders) and the ranks are
    /// read afterwards. A grant insert takes a key-share lock on its membership row through the foreign key
    /// and every command that lowers a rank takes this lock, so none can land between this read and the write
    /// that follows in the same transaction. Null means the actor may proceed; a lock timeout or a deadlock is
    /// the retryable <see cref="Contention"/>, never a 500.
    /// </summary>
    internal static async Task<Error?> LockedRefusalAsync(
        ITenantDb db,
        Guid orgId,
        IRequestPrincipal? actor,
        Guid targetMembershipId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CompareAsync(db, orgId, actor, targetMembershipId, locked: true, cancellationToken)
                .ConfigureAwait(false)
                ? null
                : Refused;
        }
        catch (PostgresException exception)
            when (exception.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected)
        {
            return Contention;
        }
    }

    private static async Task<bool> CompareAsync(
        ITenantDb db,
        Guid orgId,
        IRequestPrincipal? actor,
        Guid targetMembershipId,
        bool locked,
        CancellationToken cancellationToken)
    {
        if (actor?.UserId is not { } actorUserId)
        {
            return false;
        }

        if (locked)
        {
            _ = await db.ScalarAsync<long>(
                """
                select count(*) from (
                  select m.id
                    from public.membership m
                   where m.org_id = @org_id
                     and (m.id = @target or (m.user_id = @actor and m.status = 'active'))
                   order by m.id
                     for update) locked
                """,
                new { org_id = orgId, target = targetMembershipId, actor = actorUserId },
                cancellationToken).ConfigureAwait(false);
        }

        // A target the tenant cannot see ranks 0; the pipeline has already required membership:write, so
        // the comparison only orders administrators.
        return await db.ScalarAsync<bool?>(
            "select evidgate.session_admin_rank(@org_id, @actor) >= evidgate.membership_rank(@org_id, @target)",
            new { org_id = orgId, actor = actorUserId, target = targetMembershipId },
            cancellationToken).ConfigureAwait(false) ?? false;
    }

    /// <summary>
    /// The grant ceiling of <c>GrantRole</c> and <c>InviteMember</c>: the caller's rank must be at least the
    /// rank each role confers, so only an owner can hand out the built-in <c>tenant_admin</c> role. Permission
    /// codes alone cannot say it: a custom role holding every admin permission is still rank 1.
    /// </summary>
    internal static async Task<bool> ActorMayGrantAsync(
        ITenantDb db,
        Guid orgId,
        IRequestPrincipal? actor,
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        if (actor?.UserId is not { } actorUserId)
        {
            return false;
        }

        if (roleIds.Count == 0)
        {
            return true;
        }

        return await db.ScalarAsync<bool?>(
            """
            select evidgate.session_admin_rank(@org_id, @actor)
                   >= coalesce(max(evidgate.role_rank(@org_id, r.id)), 0)
              from unnest(@roles) as r(id)
            """,
            new { org_id = orgId, actor = actorUserId, roles = roleIds.ToArray() },
            cancellationToken).ConfigureAwait(false) ?? false;
    }
}
