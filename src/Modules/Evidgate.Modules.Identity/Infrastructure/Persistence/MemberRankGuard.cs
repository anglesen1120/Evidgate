using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

internal static class MemberRankGuard
{
    internal static Error Refused => Error.Forbidden(
        "the caller's administrative rank must be at least the target's rank in this organization");

    /// <summary>
    /// Fail-closed entry point: a request without a resolved member has no rank and is refused.
    /// The SQL function remains the only definition of the rank rule.
    /// </summary>
    internal static async Task<bool> ActorOutranksOrEqualsAsync(
        ITenantDb db,
        Guid orgId,
        IRequestPrincipal? actor,
        Guid? targetUserId,
        CancellationToken cancellationToken)
    {
        return actor?.UserId is { } actorUserId
            && await ActorOutranksOrEqualsAsync(
                db, orgId, actorUserId, targetUserId, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<bool> ActorOutranksOrEqualsAsync(
        ITenantDb db,
        Guid orgId,
        Guid actorUserId,
        Guid? targetUserId,
        CancellationToken cancellationToken)
    {
        // An unbound invite has no user, and the function ranks it zero. The pipeline has
        // already required membership:write, so the comparison only orders administrators.
        return await db.ScalarAsync<bool?>(
            "select evidgate.session_admin_rank(@org_id, @actor) >= evidgate.session_admin_rank(@org_id, @target)",
            new { org_id = orgId, actor = actorUserId, target = targetUserId },
            cancellationToken).ConfigureAwait(false) ?? false;
    }
}
