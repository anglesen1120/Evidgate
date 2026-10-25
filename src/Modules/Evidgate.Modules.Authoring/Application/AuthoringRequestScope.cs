using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Authoring.Application;

/// <summary>
/// The two decisions beyond the project gate that every authoring handler of 13.1.1 §5 makes before it reads or
/// writes a row: which active membership the caller writes as (AC5's <c>author_membership_id</c>), and how a stale
/// <c>If-Match</c> is answered (AC19).
/// </summary>
/// <remarks>
/// The project gate itself is <see cref="AuthoringRules.Project"/> (one helper for all 26 routes, so the 403 and the
/// 404 read identically everywhere); this type only adds the two answers that are specific to a write.
/// </remarks>
internal static class AuthoringRequestScope
{
    /// <summary>
    /// The caller's active membership in the open scope's organization, or null for a principal that has none (a
    /// service credential). The version writer records it as <c>author_membership_id</c> (AC5) and accepts a null
    /// author for exactly those principals.
    /// </summary>
    /// <param name="db">The tenant-bound database of the open command scope.</param>
    /// <param name="orgId">The organization of the open scope.</param>
    /// <param name="principals">The request principal.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The membership id, or null.</returns>
    internal static async ValueTask<Guid?> ActorMembershipAsync(
        ITenantDb db,
        Guid orgId,
        IRequestPrincipalAccessor principals,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(principals);

        if (principals.Current?.UserId is not { } userId)
        {
            return null;
        }

        return await db.ScalarAsync<Guid?>(
            "select id from membership where org_id = @org_id and user_id = @user_id and status = 'active' limit 1",
            new { org_id = orgId, user_id = userId },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 412 <c>precondition_failed</c> for a stale <c>If-Match</c> (AC19): the same code, status and shape the
    /// catalog's <c>CatalogErrors.Stale</c> answers, so one client rule covers both modules. The current version
    /// travels in <c>error.details</c> so a retry needs no extra GET. Both detail paths are rooted JSON pointers —
    /// <c>/If-Match</c> for the header the caller sent and <c>/version</c> for the member it must re-read — so every
    /// authoring version <c>412</c> carries one wire shape (hand-over (c) of 13.1.1, OI-13-130; the bare <c>version</c>
    /// name this helper used to answer, and the one <c>ManualCaseCommands.Stale</c> duplicated, were the divergence).
    /// The reorder's 412 is not one of these: its <c>If-Match</c> is the group's order hash, not an entity version
    /// (see the slice's hand-over for that pointer).
    /// </summary>
    /// <param name="what">The entity kind, e.g. <c>checklist item</c>.</param>
    /// <param name="currentVersion">The row's version now.</param>
    /// <returns>The refusal the handler returns unchanged.</returns>
    internal static Error Stale(string what, int currentVersion) => new(
        ApiErrorCode.PreconditionFailed,
        $"the {what} changed since it was read (current version {currentVersion})",
        412,
        Retryable: false,
        Details:
        [
            new ErrorDetail("/If-Match", "stale"),
            new ErrorDetail("/version", currentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ]);
}
