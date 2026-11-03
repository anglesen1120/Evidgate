using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// 02.1.2-T1.3: the exported identity directory against a real tenant scope —
/// <c>ListMemberRolesAsync</c> and <c>ListPendingInvitesAsync</c>
/// (<see cref="IIdentityDirectory"/>) and <c>FindAsync</c> (<see cref="IMembership"/>).
/// Both services read through the ambient <c>ITenantDb</c>, so the assertions also pin
/// that their answers are bounded by row level security rather than by their SQL.
/// </summary>
[Trait("Category", "Docker")]
public sealed class MembershipDirectoryTests : CatalogueTestBase
{
    public MembershipDirectoryTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// An active member's roles are exactly the granted ones, a pending invite resolves
    /// by its normalised address, and the membership lookup returns the row with its
    /// role ids.
    /// </summary>
    [Fact]
    public async Task Directory_Reports_Member_Roles_Pending_Invites_And_The_Membership()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (membershipId, userId) = await SeedActiveMemberAsync(org, "member@example.com", "tenant_admin", "viewer");
        var pendingId = await SeedPendingInviteAsync(org, "invitee@example.com");
        // Read the role ids before opening the read-only scope: one scope per async flow
        // (00 §5.4.2), so a second BeginAsync inside it throws.
        var adminRoleId = await RoleIdAsync("tenant_admin");
        var viewerRoleId = await RoleIdAsync("viewer");

        await using var scope = await Fixture.BeginTenantScopeAsync(org, readOnlyTransaction: true, cancellationToken: Token);
        var directory = new MembershipDirectory(scope.Db);

        var roles = await directory.ListMemberRolesAsync(org, userId, Token);
        roles.Select(role => role.Code).Order(StringComparer.Ordinal).ShouldBe(["tenant_admin", "viewer"]);
        roles.ShouldAllBe(role => role.IsBuiltin);
        roles.Single(role => role.Code == "tenant_admin").RoleId.ShouldBe(adminRoleId);

        var invites = await directory.ListPendingInvitesAsync(org, "invitee@example.com", Token);
        invites.Count.ShouldBe(1);
        invites[0].MembershipId.ShouldBe(pendingId);
        invites[0].OrgId.ShouldBe(org);
        invites[0].Status.ShouldBe(MembershipStatus.Pending);
        invites[0].InviteExpiresAt.ShouldBeNull("02.1.2-T2 mints the invite token and its expiry");

        var membership = await directory.FindAsync(org, userId, Token);
        membership.ShouldNotBeNull();
        membership.MembershipId.ShouldBe(membershipId);
        membership.Status.ShouldBe(MembershipStatus.Active);
        membership.RoleIds.Order().ShouldBe(new[] { adminRoleId, viewerRoleId }.Order());

        (await directory.ListMemberRolesAsync(org, Guid.CreateVersion7(), Token))
            .ShouldBeEmpty("an unknown user has no member roles");
    }

    /// <summary>
    /// RLS, not the SQL, is the boundary: with the scope opened as org A the directory
    /// cannot see org B's membership, roles or invite.
    /// </summary>
    [Fact]
    public async Task Directory_Cannot_See_Another_Orgs_Membership()
    {
        var orgA = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var orgB = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (_, userIdOfB) = await SeedActiveMemberAsync(orgB, "b@example.com", "tenant_admin");
        await SeedPendingInviteAsync(orgB, "invitee@example.com");

        await using var scope = await Fixture.BeginTenantScopeAsync(orgA, readOnlyTransaction: true, cancellationToken: Token);
        var directory = new MembershipDirectory(scope.Db);

        (await directory.FindAsync(orgB, userIdOfB, Token))
            .ShouldBeNull("the lookup names org B's own row, so only the scope's RLS can hide it");
        (await directory.ListMemberRolesAsync(orgB, userIdOfB, Token))
            .ShouldBeEmpty("org B's rows are invisible inside org A's scope even when the caller names org B");
        (await directory.ListPendingInvitesAsync(orgB, "invitee@example.com", Token)).ShouldBeEmpty();
    }

    /// <summary>Seeds one <c>active</c> membership with the named built-in roles, as the owner.</summary>
    private async Task<(Guid MembershipId, Guid UserId)> SeedActiveMemberAsync(Guid orgId, string email, params string[] roleCodes)
    {
        var membershipId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        // 0044's `membership_user_fk` (membership.user_id -> public."user") makes the bound
        // membership name a real account, so the account is seeded before the membership.
        await Fixture.SeedAsync(
            """insert into public."user" (id, email) values (@id, @email)""",
            [("id", userId), ("email", email)],
            Token);

        await Fixture.SeedAsync(
            "insert into public.membership (id, org_id, user_id, invited_email, status) values (@id, @org_id, @user_id, @email, 'active')",
            [("id", membershipId), ("org_id", orgId), ("user_id", userId), ("email", email)],
            Token);

        foreach (var code in roleCodes)
        {
            await Fixture.SeedAsync(
                """
                insert into public.membership_role (id, org_id, membership_id, role_id)
                select @id, @org_id, @membership_id, role.id from public.role
                 where role.org_id is null and role.code = @code
                """,
                [
                    ("id", Guid.CreateVersion7()),
                    ("org_id", orgId),
                    ("membership_id", membershipId),
                    ("code", code),
                ],
                Token);
        }

        return (membershipId, userId);
    }

    /// <summary>Seeds one pending invite and returns its membership id, as the owner.</summary>
    private async Task<Guid> SeedPendingInviteAsync(Guid orgId, string email)
    {
        var membershipId = Guid.CreateVersion7();

        await Fixture.SeedAsync(
            "insert into public.membership (id, org_id, invited_email, status) values (@id, @org_id, @email, 'pending')",
            [("id", membershipId), ("org_id", orgId), ("email", email)],
            Token);

        return membershipId;
    }

    /// <summary>The id of one built-in role of 0010's seed, read through the owner seam.</summary>
    private async Task<Guid> RoleIdAsync(string code)
    {
        await using var scope = await Fixture.BeginTenantScopeAsync(Guid.CreateVersion7(), readOnlyTransaction: true, cancellationToken: Token);
        return await scope.Db.ScalarAsync<Guid>(
            "select id from public.role where org_id is null and code = @code",
            new { code },
            Token);
    }
}
