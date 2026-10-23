using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Identity.Contracts;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// The module's implementation of the exported identity directory
/// (02.1.2-T1.3): <see cref="IIdentityDirectory"/> for the login flow's views of a
/// member and <see cref="IMembership"/> for the membership lookup. Both run on the
/// caller's ambient <see cref="ITenantDb"/>, i.e. inside whatever
/// <c>ITenantScope</c> the caller opened, so RLS — not this class — decides what is
/// visible (00 §5.4.7, AC7).
/// </summary>
internal sealed class MembershipDirectory(ITenantDb db) : IIdentityDirectory, IMembership
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MemberRole>> ListMemberRolesAsync(
        Guid orgId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var roles = await db.QueryAsync<RoleView>(
            MembershipReadSql.ActiveMemberRoles,
            new { org_id = orgId, user_id = userId },
            cancellationToken).ConfigureAwait(false);

        return [.. roles.Select(role => new MemberRole(role.Id, role.Code, role.Name, role.IsBuiltin))];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<PendingInvite>> ListPendingInvitesAsync(
        Guid orgId,
        string invitedEmail,
        CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<PendingInviteRow>(
            MembershipReadSql.PendingInvitesForEmail,
            new { org_id = orgId, invited_email = invitedEmail },
            cancellationToken).ConfigureAwait(false);

        return
        [
            .. rows.Select(row => new PendingInvite(
                row.Id,
                row.OrgId,
                row.InvitedEmail,
                row.Status,
                row.InviteExpiresAt)),
        ];
    }

    /// <inheritdoc />
    public async ValueTask<MembershipSummary?> FindAsync(
        Guid orgId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var row = await db.QuerySingleOrDefaultAsync<MembershipSummaryRow>(
            MembershipReadSql.MembershipForUser,
            new { org_id = orgId, user_id = userId },
            cancellationToken).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var roleIds = await db.QueryAsync<Guid>(
            MembershipReadSql.RoleIdsOfMembership,
            new { org_id = orgId, membership_id = row.Id },
            cancellationToken).ConfigureAwait(false);

        return new MembershipSummary(row.Id, row.OrgId, row.UserId, row.Status, roleIds);
    }
}

/// <summary>One pending invite row of <see cref="MembershipReadSql.PendingInvitesForEmail"/>.</summary>
/// <param name="Id">The membership id.</param>
/// <param name="OrgId">The owning org.</param>
/// <param name="InvitedEmail">The normalised invite address.</param>
/// <param name="Status">Always <c>pending</c> for these rows.</param>
/// <param name="InviteExpiresAt">The invite's expiry, or null until 02.1.2-T2 mints the token.</param>
internal sealed record PendingInviteRow(
    Guid Id,
    Guid OrgId,
    string InvitedEmail,
    string Status,
    DateTimeOffset? InviteExpiresAt);

/// <summary>One membership row of <see cref="MembershipReadSql.MembershipForUser"/>.</summary>
/// <param name="Id">The membership id.</param>
/// <param name="OrgId">The owning org.</param>
/// <param name="UserId">The bound user (the row was looked up by it).</param>
/// <param name="Status">One of <c>MembershipStatus</c>.</param>
internal sealed record MembershipSummaryRow(Guid Id, Guid OrgId, Guid? UserId, string Status);
