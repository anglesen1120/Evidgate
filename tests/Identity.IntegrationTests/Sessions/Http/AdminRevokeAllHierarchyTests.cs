using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// The hierarchy of <c>POST /v1/auth/users/{u}/sessions/revoke-all</c>: a caller may end all sessions of a target
/// only when the target's rank in the ACTING org is not above the caller's, and only a <c>tenant_admin</c> (the
/// owner of the org) outranks a custom administrator that holds <c>membership:write</c>.
/// </summary>
/// <remarks>
/// A refusal is the route's one 404 and changes nothing: no session row, no audit row. The rank is read from the
/// acting org's grants only, so an owner elsewhere is no owner here.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AdminRevokeAllHierarchyTests : TenantTest
{
    /// <summary>Own database: the mutation case replaces a function of the schema.</summary>
    public AdminRevokeAllHierarchyTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>A custom administrator ends a plain member's sessions: the lower rank is allowed.</summary>
    [Fact]
    public async Task A_Custom_Admin_Ends_A_Members_Sessions()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var target = await host.SignInMemberAsync(client);
        var admin = await SignInCustomAdminAsync(host, client, target.OrgId);

        await AssertRevokedAsync(host, client, admin, target);
    }

    /// <summary>A custom administrator may not end a <c>tenant_admin</c>'s sessions: 404 and nothing changes.</summary>
    [Fact]
    public async Task A_Custom_Admin_Cannot_End_An_Owners_Sessions()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var owner = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");
        var admin = await SignInCustomAdminAsync(host, client, member.OrgId);

        await AssertRefusedAsync(host, client, admin, owner);
    }

    /// <summary>Equal rank is allowed: one custom administrator ends another's sessions.</summary>
    [Fact]
    public async Task A_Custom_Admin_Ends_Another_Custom_Admins_Sessions()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var admin = await SignInCustomAdminAsync(host, client, member.OrgId);
        var peer = await SignInCustomAdminAsync(host, client, member.OrgId);

        await AssertRevokedAsync(host, client, admin, peer);
    }

    /// <summary>An owner ends an owner's sessions, and a custom administrator's.</summary>
    [Fact]
    public async Task An_Owner_Ends_An_Owners_And_An_Admins_Sessions()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var owner = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");
        var otherOwner = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");
        var admin = await SignInCustomAdminAsync(host, client, member.OrgId);

        await AssertRevokedAsync(host, client, owner, otherOwner);
        await AssertRevokedAsync(host, client, owner, admin);
    }

    /// <summary>An owner addressing itself on the administrative path is refused; the self-service routes exist.</summary>
    [Fact]
    public async Task An_Owner_Addressing_Itself_Is_Refused()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var owner = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");

        await AssertRefusedAsync(host, client, owner, owner);
    }

    /// <summary>
    /// The rank is the acting org's: a target that is an owner in org A and a plain member in org B is refused
    /// under A's header and allowed under B's, for a custom administrator of both.
    /// </summary>
    [Fact]
    public async Task The_Rank_Is_Read_From_The_Acting_Org_Only()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var inA = await host.SignInMemberAsync(client);
        var orgB = await host.SeedOrganizationAsync();
        var target = await host.SignInMemberInOrgAsync(client, inA.OrgId, "tenant_admin");
        await host.SeedMembershipAsync(orgB, target.UserId);

        var adminA = await SignInCustomAdminAsync(host, client, inA.OrgId);
        var adminB = await SignInCustomAdminAsync(host, client, orgB);

        await AssertRefusedAsync(host, client, adminA, target, inA.OrgId);
        await AssertRevokedAsync(host, client, adminB, target, orgB);
    }

    /// <summary>
    /// A custom role of the org that merely carries the code <c>tenant_admin</c> is not the built-in owner role:
    /// it ranks as a custom administrator, so it can neither end a real owner's sessions nor be mistaken for one.
    /// </summary>
    [Fact]
    public async Task A_Custom_Role_Named_Tenant_Admin_Does_Not_Reach_The_Owner_Rank()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var owner = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");
        var impostor = await SignInCustomAdminAsync(host, client, member.OrgId, roleCode: "tenant_admin");

        await AssertRefusedAsync(host, client, impostor, owner);
    }

    /// <summary>
    /// A <c>membership_role</c> row that points at ANOTHER org's custom administrator role is refused by the
    /// database itself, and a custom administrator of the acting org still ends the member's sessions: the rank
    /// query scopes roles to the acting org, and the key makes the foreign row unwritable in the first place.
    /// </summary>
    [Fact]
    public async Task A_Foreign_Orgs_Custom_Role_Cannot_Be_Granted()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var foreignOrg = await host.SeedOrganizationAsync();
        var foreignAdmin = await SignInCustomAdminAsync(host, client, foreignOrg);
        var roleId = (Guid)(await host.ScalarAsync(
            "select mr.role_id from public.membership_role mr join public.membership m on m.id = mr.membership_id "
            + "where m.org_id = @org and m.user_id = @user",
            [("org", foreignOrg), ("user", foreignAdmin.UserId)])).ShouldNotBeNull();

        var caller = await host.SignInMemberInOrgAsync(client, member.OrgId, roleCode: null);
        var refusal = await Should.ThrowAsync<PostgresException>(async () =>
            await host.ArrangeAsync(
                "insert into public.membership_role (id, org_id, membership_id, role_id) "
                + "select @id, @org, m.id, @role from public.membership m where m.org_id = @org and m.user_id = @user",
                ("id", Guid.CreateVersion7()),
                ("org", member.OrgId),
                ("role", roleId),
                ("user", caller.UserId)));

        refusal.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        refusal.ConstraintName.ShouldBe("membership_role_role_guard");

        var localAdmin = await SignInCustomAdminAsync(host, client, member.OrgId);
        await AssertRevokedAsync(host, client, localAdmin, member);
    }

    /// <summary>
    /// Mutation proof: with the rank function neutralised (every user ranks the same) the very call the first
    /// refusal case asserts succeeds, so the refusal is the rank check's and nothing else's.
    /// </summary>
    [Fact]
    public async Task Without_The_Rank_Check_The_Owner_Refusal_Disappears()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var owner = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");
        var admin = await SignInCustomAdminAsync(host, client, member.OrgId);

        await AssertRefusedAsync(host, client, admin, owner);

        await host.ArrangeAsync(
            "create or replace function evidgate.session_admin_rank(p_org_id uuid, p_user_id uuid) "
            + "returns integer language sql set search_path = '' as $$ select 0 $$");

        await AssertRevokedAsync(host, client, admin, owner);
    }

    private static async Task<SignedInMember> SignInCustomAdminAsync(
        SessionTestHost host,
        HttpClient client,
        Guid orgId,
        string? roleCode = null)
    {
        var signedIn = await host.SignInMemberInOrgAsync(client, orgId, roleCode: null);
        var roleId = Guid.CreateVersion7();

        await host.ArrangeAsync(
            "insert into public.role (id, org_id, code, name, is_builtin, kind) "
            + "values (@id, @org, @code, 'Member administrator', false, 'member')",
            ("id", roleId),
            ("org", orgId),
            ("code", roleCode ?? "member_admin_" + roleId.ToString("N")));
        await host.ArrangeAsync(
            "insert into public.role_permission (id, org_id, role_id, permission_code) "
            + "values (@id, @org, @role, 'membership:write')",
            ("id", Guid.CreateVersion7()),
            ("org", orgId),
            ("role", roleId));
        await host.ArrangeAsync(
            "insert into public.membership_role (id, org_id, membership_id, role_id) "
            + "select @id, @org, m.id, @role from public.membership m where m.org_id = @org and m.user_id = @user",
            ("id", Guid.CreateVersion7()),
            ("org", orgId),
            ("role", roleId),
            ("user", signedIn.UserId));

        return signedIn;
    }

    private static async Task AssertRevokedAsync(
        SessionTestHost host,
        HttpClient client,
        SignedInMember caller,
        SignedInMember target,
        Guid? orgId = null)
    {
        var org = orgId ?? target.OrgId;
        var rowsBefore = await AdminRevokeAllTestData.AdminRowCountAsync(host, org);

        using var response = await AdminRevokeAllTestData.RevokeAllAsync(client, caller, target.UserId, org);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, "the target does not outrank the caller");

        (await AdminRevokeAllTestData.LiveCountAsync(host, target.UserId)).ShouldBe(0L);
        (await AdminRevokeAllTestData.AdminRowCountAsync(host, org)).ShouldBe(
            rowsBefore + 1,
            "one audit row lands in the acting org's chain");
    }

    private static async Task AssertRefusedAsync(
        SessionTestHost host,
        HttpClient client,
        SignedInMember caller,
        SignedInMember target,
        Guid? orgId = null)
    {
        var org = orgId ?? target.OrgId;
        var liveBefore = await AdminRevokeAllTestData.LiveCountAsync(host, target.UserId);
        var rowsBefore = await AdminRevokeAllTestData.AdminRowCountAsync(host, org);

        using var response = await AdminRevokeAllTestData.RevokeAllAsync(client, caller, target.UserId, org);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the target outranks the caller, or is the caller");
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.NotFound);

        (await AdminRevokeAllTestData.LiveCountAsync(host, target.UserId)).ShouldBe(liveBefore);
        (await AdminRevokeAllTestData.AdminRowCountAsync(host, org)).ShouldBe(rowsBefore);
    }
}
