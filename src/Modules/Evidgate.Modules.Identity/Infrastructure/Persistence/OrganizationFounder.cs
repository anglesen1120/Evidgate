using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// <see cref="IOrganizationFounder"/> over the membership write model (02.1.1 AC8,
/// T98). Runs inside the caller's tenant transaction: RLS scopes every row to the new
/// org, and the built-in roles (<c>org_id IS NULL</c>) are visible to every tenant.
/// </summary>
internal sealed class OrganizationFounder(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    ITenantScopeAccessor scopes)
    : IOrganizationFounder
{
    /// <summary>The built-in roles the creator receives together (T98).</summary>
    internal static readonly string[] FounderRoleCodes =
        [LastAdminGuard.TenantAdminRoleCode, "release_approver"];

    /// <inheritdoc />
    public async ValueTask<Guid> GrantFounderAsync(
        Guid orgId,
        Guid userId,
        string verifiedEmail,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedEmail);

        if (scopes.Require().OrgId != orgId)
        {
            // A founder row in another tenant would be a cross-org write; RLS would
            // refuse it too, but the contract is stated here, not left to a 42501.
            throw new InvalidOperationException(
                "GrantFounderAsync must run in the new organization's own tenant scope");
        }

        await using var context = contexts.Create();

        // Defence in depth (02.1.1 security review): a founder exists only for an org
        // that has no member yet. Today the org INSERT runs first and fails on an
        // existing id, but a later refactor must not turn this into "become
        // tenant_admin of any org whose id the caller can make the tenant".
        if (await context.Memberships.AnyAsync(membership => membership.OrgId == orgId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "GrantFounderAsync refused: the organization already has members");
        }

        var roles = await context.Roles
            .Where(role => role.IsBuiltin && FounderRoleCodes.Contains(role.Code))
            .Select(role => role.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (roles.Count != FounderRoleCodes.Length)
        {
            throw new InvalidOperationException(
                "the built-in tenant_admin/release_approver roles are missing from the catalogue (migration 0010)");
        }

        var membership = new Membership
        {
            Id = Guid.CreateVersion7(),
            OrgId = orgId,
            UserId = userId,
            InvitedEmail = InviteEmail.Normalize(verifiedEmail),
            Status = MembershipStatus.Active,
            InvitedBy = userId,
        };

        await context.Memberships.AddAsync(membership, cancellationToken).ConfigureAwait(false);

        foreach (var roleId in roles)
        {
            await context.MembershipRoles.AddAsync(
                new MembershipRole
                {
                    Id = Guid.CreateVersion7(),
                    OrgId = orgId,
                    MembershipId = membership.Id,
                    RoleId = roleId,
                    GrantedBy = userId,
                },
                cancellationToken).ConfigureAwait(false);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return membership.Id;
    }
}
