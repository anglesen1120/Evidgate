using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// The last-admin rule of 02.1.2 AC4: an org must keep at least one <b>active</b>
/// membership holding the built-in <c>tenant_admin</c> role, so removing the last
/// one — or revoking that role from it — is 409 <c>last_admin</c>.
/// <para>
/// It is a query plus a rule, so it lives beside the module's SQL rather than in
/// <c>Domain/</c>: the 00 §5.5 rule keeps <c>Domain</c> clear of any
/// <c>Infrastructure</c> namespace. 02.1.2-T3.1 extracts it into the story's shared
/// guard; the three callers today are <c>RemoveMemberCommand</c>,
/// <c>UpdateMembershipCommand</c> and <c>RevokeRoleCommand</c>.
/// </para>
/// <para>
/// The count runs as <c>SELECT … FOR UPDATE</c> over the org's active
/// <c>tenant_admin</c> rows (02.1.2 §8): two concurrent removals of different admins
/// serialise on those rows, and the second transaction re-evaluates the row the
/// first one changed, so it sees the reduced set and refuses too. A plain count
/// would let both through.
/// </para>
/// </summary>
internal static class LastAdminGuard
{
    /// <summary>The built-in role code the guard protects (00 §2, 0010).</summary>
    internal const string TenantAdminRoleCode = "tenant_admin";

    /// <summary>
    /// True when the org's only active <c>tenant_admin</c> membership is
    /// <paramref name="membershipId"/> — i.e. removing that membership, or revoking
    /// the role from it, would leave the org without an admin.
    /// </summary>
    internal static async Task<bool> WouldOrphanOrgAsync(
        ITenantDb db,
        Guid orgId,
        Guid membershipId,
        CancellationToken cancellationToken)
    {
        var admins = await db.QueryAsync<Guid>(
            MembershipReadSql.ActiveMembershipsHoldingRole,
            new { org_id = orgId, role_code = TenantAdminRoleCode },
            cancellationToken).ConfigureAwait(false);

        return admins.Count == 1 && admins[0] == membershipId;
    }
}
