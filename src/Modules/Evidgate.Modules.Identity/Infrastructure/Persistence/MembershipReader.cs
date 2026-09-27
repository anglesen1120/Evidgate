using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Identity.Contracts;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// Assembles <see cref="MembershipView"/>s from the Dapper read SQL
/// (02.1.2-T1.2): one statement for the membership rows, one bounded statement for
/// the roles of exactly those rows — never a query per membership (02.1.2 §8 P3).
/// </summary>
/// <remarks>
/// The commands use it too, after <c>SaveChangesAsync</c>: EF and Dapper share the
/// scope's connection and transaction (00 §5.5 C3), so a read here sees the
/// uncommitted membership the same command just wrote.
/// </remarks>
internal static class MembershipReader
{
    /// <summary>One membership with its roles, or null when the tenant cannot see it.</summary>
    internal static async Task<MembershipView?> ReadAsync(
        ITenantDb db,
        Guid orgId,
        Guid membershipId,
        CancellationToken cancellationToken)
    {
        var row = await db.QuerySingleOrDefaultAsync<MembershipRow>(
            MembershipReadSql.MembershipById,
            new { org_id = orgId, membership_id = membershipId },
            cancellationToken).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var views = await ToViewsAsync(db, orgId, [row], cancellationToken).ConfigureAwait(false);
        return views[0];
    }

    /// <summary>The rows of one page, each with its roles, in the page's own order.</summary>
    internal static async Task<IReadOnlyList<MembershipView>> ToViewsAsync(
        ITenantDb db,
        Guid orgId,
        IReadOnlyList<MembershipRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(row => row.Id).ToArray();

        var roleRows = await db.QueryAsync<MembershipRoleRow>(
            MembershipReadSql.RolesOfMemberships,
            new { org_id = orgId, membership_ids = ids },
            cancellationToken).ConfigureAwait(false);

        var rolesByMembership = roleRows
            .GroupBy(row => row.MembershipId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<RoleView>)[.. group.Select(ToRoleView)]);

        return
        [
            .. rows.Select(row => new MembershipView(
                row.Id,
                row.OrgId,
                row.UserId,
                row.InvitedEmail,
                row.Status,
                rolesByMembership.TryGetValue(row.Id, out var roles) ? roles : [],
                row.CreatedAt,
                row.UpdatedAt,
                row.Version)),
        ];
    }

    private static RoleView ToRoleView(MembershipRoleRow row) =>
        new(row.Id, row.OrgId, row.Code, row.Name, row.IsBuiltin, row.CreatedAt, row.Version);
}
