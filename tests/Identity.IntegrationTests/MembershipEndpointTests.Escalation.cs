using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Identity.Contracts;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// The self-escalation half of 02.1.2 §5 (00 §2): a caller may hand out only what it
/// already holds, whoever the target is — a grant to its own membership, to another
/// member or onto a pending invite, and the invite route's own role set. The rest of the
/// suite is in <c>MembershipEndpointTests.cs</c>.
/// </summary>
public sealed partial class MembershipEndpointTests
{
    /// <summary>
    /// 00 §2: a `tenant_admin` may not grant a role that carries a permission it does not
    /// hold, its own membership included. `release_approver` owns `policy:write` and
    /// `artifact:read_sensitive`, and `tenant_admin` has neither — so the request is 403
    /// `forbidden` and no `membership_role` row is written, because a refusal that leaves
    /// the escalation behind is worse than no refusal.
    /// </summary>
    [Fact]
    public async Task Tenant_Admin_Cannot_Grant_Release_Approver_To_Itself()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (adminMembershipId, adminUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var releaseApproverId = await RoleIdAsync("release_approver");

        // The module's real evaluator over the real membership: the subset rule compares the
        // caller's own resolution, so a fixed "grant everything" stand-in would decide the case
        // itself (this is the shape BuiltinRoleTests uses for its real-admin cases).
        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, Principal: RequestPrincipal.Member(adminUserId)));
        using var client = factory.CreateClient();

        using var response = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships/{adminMembershipId}/roles",
            $$"""{"role_id":"{{releaseApproverId}}"}""",
            "self-grant-1");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("forbidden");

        (await ScalarAsync<long>(
            "select count(*) from public.membership_role where membership_id = @id",
            ("id", adminMembershipId))).ShouldBe(1, "only the seeded tenant_admin grant exists");
    }

    /// <summary>
    /// The rule applies to every target, not only the caller's own membership: a
    /// `tenant_admin` without `policy:write` may not appoint another member as
    /// `release_approver`, or one admin could mint a second approver and defeat the
    /// two-person waiver rule. 403 `forbidden`, no grant written.
    /// </summary>
    [Fact]
    public async Task Tenant_Admin_Cannot_Grant_Release_Approver_To_Another_Member()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (_, adminUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var (memberId, _) = await SeedActiveMemberAsync(org, "member@example.com", "viewer");
        var releaseApproverId = await RoleIdAsync("release_approver");

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, Principal: RequestPrincipal.Member(adminUserId)));
        using var client = factory.CreateClient();

        using var response = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships/{memberId}/roles",
            $$"""{"role_id":"{{releaseApproverId}}"}""",
            "delegated-grant-1");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("forbidden");

        (await ScalarAsync<long>(
            "select count(*) from public.membership_role where membership_id = @id",
            ("id", memberId))).ShouldBe(1, "only the seeded viewer grant exists");
    }

    /// <summary>
    /// The invite half of the same rule: inviting another address with a role the caller
    /// does not fully hold is 403 `forbidden` and creates no membership.
    /// </summary>
    [Fact]
    public async Task Tenant_Admin_Cannot_Invite_Another_Address_As_Release_Approver()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (_, adminUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var releaseApproverId = await RoleIdAsync("release_approver");

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, Principal: RequestPrincipal.Member(adminUserId)));
        using var client = factory.CreateClient();

        using var response = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            $$"""{"email":"second@example.com","roles":["{{releaseApproverId}}"]}""",
            "other-invite-1");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("forbidden");

        (await CountAsync("select count(*) from public.membership"))
            .ShouldBe(1, "the refused invite wrote no membership");
    }

    /// <summary>
    /// Positive control: a caller that holds every permission of the role (a
    /// `tenant_admin` that is also a `release_approver`) may grant that role to another
    /// member and invite another address with it.
    /// </summary>
    [Fact]
    public async Task Caller_Holding_Every_Permission_Of_The_Role_May_Grant_And_Invite_It()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (_, callerUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin", "release_approver");
        var (memberId, _) = await SeedActiveMemberAsync(org, "member@example.com", "viewer");
        var releaseApproverId = await RoleIdAsync("release_approver");

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, Principal: RequestPrincipal.Member(callerUserId)));
        using var client = factory.CreateClient();

        using var grant = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships/{memberId}/roles",
            $$"""{"role_id":"{{releaseApproverId}}"}""",
            "holder-grant-1");

        grant.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonSerializer.Deserialize<MembershipView>(await grant.Content.ReadAsStringAsync(Token), Wire)!
            .Roles.Select(role => role.Code).Order(StringComparer.Ordinal)
            .ShouldBe(["release_approver", "viewer"]);

        using var invite = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            $$"""{"email":"second@example.com","roles":["{{releaseApproverId}}"]}""",
            "holder-invite-1");

        invite.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>
    /// The invite half: an invite addressed to the caller's own membership is refused like
    /// any other address when the role carries a permission the caller does not hold — the
    /// rule fires on the role, whoever the address belongs to. 403 `forbidden`, no
    /// membership created.
    /// </summary>
    [Fact]
    public async Task Inviting_The_Callers_Own_Address_To_A_Role_They_Do_Not_Hold_Is_403()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (_, adminUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var releaseApproverId = await RoleIdAsync("release_approver");

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, Principal: RequestPrincipal.Member(adminUserId)));
        using var client = factory.CreateClient();

        using var response = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            $$"""{"email":"admin@example.com","roles":["{{releaseApproverId}}"]}""",
            "self-invite-1");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("forbidden");

        (await CountAsync("select count(*) from public.membership"))
            .ShouldBe(1, "the refused invite wrote no membership");
    }

    /// <summary>
    /// The two-step form of the invite half: an invite to the caller's own address with no
    /// roles passes (it grants nothing), but a later grant onto that pending row is refused
    /// like a grant onto any other target — the caller does not hold the role's permissions
    /// — so it is the same 403 `forbidden` and no grant is written.
    /// </summary>
    [Fact]
    public async Task Granting_A_Role_To_A_Pending_Invite_For_The_Callers_Own_Address_Is_403()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (_, adminUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var releaseApproverId = await RoleIdAsync("release_approver");

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, Principal: RequestPrincipal.Member(adminUserId)));
        using var client = factory.CreateClient();

        using var invite = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            """{"email":"  Admin@Example.com ","roles":[]}""",
            "self-invite-empty-1");

        invite.StatusCode.ShouldBe(HttpStatusCode.Created);
        var pendingId = JsonSerializer.Deserialize<MembershipView>(await invite.Content.ReadAsStringAsync(Token), Wire)!.Id;

        using var grant = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships/{pendingId}/roles",
            $$"""{"role_id":"{{releaseApproverId}}"}""",
            "self-pending-grant-1");

        grant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await grant.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("forbidden");

        (await ScalarAsync<long>(
            "select count(*) from public.membership_role where membership_id = @id",
            ("id", pendingId))).ShouldBe(0, "the refused grant wrote nothing onto the pending row");
    }
}
