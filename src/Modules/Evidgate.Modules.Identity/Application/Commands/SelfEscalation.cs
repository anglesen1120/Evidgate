using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Authorization;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// The self-escalation block of W7-SEC-02 (00 §2: "`tenant_admin` | Membership,
/// integrations, quota | Secret values are never readable: no `artifact:read_sensitive`
/// and no `billing:*`"). The role strings are data, so nothing stops a
/// <c>tenant_admin</c> — which holds <c>membership:write</c> and, with the flag on,
/// <c>role:write</c> — from granting itself <c>release_approver</c> (policy:write,
/// artifact:read_sensitive) or writing a custom role that widens its own access. The
/// rule these helpers implement is the direct reading of that row: <b>a caller may hand
/// out only what the caller already holds</b>.
/// </summary>
/// <remarks>
/// <para>
/// The caller's set comes from <see cref="ICallerPermissionSet"/> — the same object and
/// the same join the C2 gate evaluates — so the answer here and the answer on the next
/// request cannot diverge. The set is resolved once per check.
/// </para>
/// <para>
/// [DECISION] Every route that hands out permissions applies the rule to every target:
/// GrantRole and InviteMember whoever the member or address is, CreateRole and UpdateRole
/// every submitted code. A second address the caller controls would otherwise carry a
/// role the caller could not hold, which defeats the two-person waiver rule; admin
/// delegation is not part of V1.
/// </para>
/// </remarks>
internal static class SelfEscalation
{
    /// <summary>
    /// The 403 of the rule. It reuses the 00 §9.2 code the 403 response already
    /// documents on all four routes (<c>forbidden</c>, 00 §9.2 "the principal does not
    /// hold the operation's permission"), so no response shape changes and no new code is
    /// invented — the request is refused as a permission the caller does not have, which
    /// is exactly what it is.
    /// </summary>
    internal static Error Refused => Error.Forbidden(
        "the request would grant the caller a permission the caller does not hold (00 §2: a role never widens the caller's own access)");

    /// <summary>
    /// The codes of <paramref name="codes"/> the caller does not hold, ordinal order —
    /// empty when the caller holds all of them (or when there is nothing to grant).
    /// Fail-closed: without a resolved caller nothing is held.
    /// </summary>
    internal static async ValueTask<IReadOnlyList<string>> MissingAsync(
        ICallerPermissionSet permissions,
        Guid orgId,
        IRequestPrincipal? caller,
        IEnumerable<string> codes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(codes);

        var distinct = codes.Distinct(StringComparer.Ordinal).ToList();

        if (distinct.Count == 0)
        {
            return [];
        }

        if (caller is null)
        {
            // The C2 stage never reaches a handler without a principal; if it ever did,
            // the caller holds nothing and every grant is refused rather than allowed.
            return distinct;
        }

        var access = await permissions.ResolveAsync(orgId, caller, cancellationToken).ConfigureAwait(false);

        return [.. distinct.Where(code => !access.Codes.Contains(code))];
    }

    /// <summary>
    /// Every code the given roles grant, read through the write model under RLS: the
    /// built-in grants (<c>org_id is null</c>) plus the caller's own org rows. The role
    /// being handed over is inspected before it is written.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> RoleCodesAsync(
        IdentityDbContext context,
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (roleIds.Count == 0)
        {
            return [];
        }

        var ids = roleIds.ToArray();

        return await context.RolePermissions
            .Where(grant => ids.Contains(grant.RoleId))
            .Select(grant => grant.PermissionCode)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
