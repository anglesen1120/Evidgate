using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Testing;
using Identity.IntegrationTests.Sessions.Http;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Membership;

/// <summary>
/// The grant ceiling introduced with <c>role_rank</c>: permission codes alone cannot decide whether a caller may
/// hand a role out. A custom role holding every <c>tenant_admin</c> permission still ranks one, so its holder may
/// not grant or invite with the built-in owner role; an owner may. The same holder may grant its own rank-one role.
/// </summary>
/// <remarks>
/// The actor's role is built from the built-in role's permission rows, so the case exercises the exact boundary
/// between the permission check (<c>SelfEscalation</c>) and the rank check (<c>ActorMayGrantAsync</c>).
/// </remarks>
[Trait("Category", "Docker")]
public sealed class MemberRankGrantCeilingTests : TenantTest
{
    public MemberRankGrantCeilingTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>
    /// A rank-one actor with every tenant_admin permission cannot grant the built-in role or invite with it: both
    /// are 403 and neither writes a row, despite the permission check accepting the role.
    /// </summary>
    [Fact]
    public async Task Full_Permission_Custom_Admin_Cannot_Grant_Or_Invite_The_Built_In_Tenant_Admin()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var scene = await SeedCeilingSceneAsync(host, client);

        using var deniedGrant = await SendGrantAsync(
            client,
            scene.OrgId,
            scene.TargetMembership,
            scene.AdminRoleId,
            scene.Actor,
            "rank-ceiling-grant-denied");

        deniedGrant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(deniedGrant)).ShouldBe(ApiErrorCode.Forbidden);
        (await MemberRankTestScenario.HoldsRoleAsync(host, scene.TargetMembership, scene.AdminRoleId))
            .ShouldBeFalse("the refused grant writes nothing");

        using var deniedInvite = await SendInviteAsync(
            client,
            scene.OrgId,
            scene.Email,
            scene.AdminRoleId,
            scene.Actor,
            "rank-ceiling-invite-denied");

        deniedInvite.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(deniedInvite)).ShouldBe(ApiErrorCode.Forbidden);
        (await InviteCountAsync(host, scene.Email)).ShouldBe(0L, "the refused invite writes no row");
    }

    /// <summary>An owner passes the same ceiling: the grant is 200 and the invite is 201.</summary>
    [Fact]
    public async Task Owner_Grants_And_Invites_The_Built_In_Tenant_Admin()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var scene = await SeedCeilingSceneAsync(host, client);

        using var ownerGrant = await SendGrantAsync(
            client,
            scene.OrgId,
            scene.TargetMembership,
            scene.AdminRoleId,
            scene.Owner,
            "rank-ceiling-grant-owner");

        ownerGrant.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await MemberRankTestScenario.HoldsRoleAsync(host, scene.TargetMembership, scene.AdminRoleId))
            .ShouldBeTrue();

        using var ownerInvite = await SendInviteAsync(
            client,
            scene.OrgId,
            scene.Email,
            scene.AdminRoleId,
            scene.Owner,
            "rank-ceiling-invite-owner");

        ownerInvite.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await InviteCountAsync(host, scene.Email)).ShouldBe(1L);
    }

    /// <summary>The same rank-one actor may grant its own rank-one custom role: the ceiling is equality, not a ban.</summary>
    [Fact]
    public async Task Full_Permission_Custom_Admin_May_Grant_Its_Own_Rank_One_Role()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var scene = await SeedCeilingSceneAsync(host, client);

        using var granted = await SendGrantAsync(
            client,
            scene.OrgId,
            scene.TargetMembership,
            scene.ActorRoleId,
            scene.Actor,
            "rank-ceiling-rank-one-grant");

        granted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await MemberRankTestScenario.HoldsRoleAsync(host, scene.TargetMembership, scene.ActorRoleId))
            .ShouldBeTrue();
    }

    /// <summary>One org with an owner, a rank-one actor holding every owner permission, and a plain target member.</summary>
    private static async Task<CeilingScene> SeedCeilingSceneAsync(
        SessionTestHost host,
        HttpClient client)
    {
        var founder = await host.SignInMemberAsync(client);
        var owner = await host.SignInMemberInOrgAsync(client, founder.OrgId, "tenant_admin");
        var target = await host.SignInMemberInOrgAsync(client, founder.OrgId, roleCode: null);
        var targetMembership = await MemberRankTestScenario.MembershipIdAsync(
            host,
            founder.OrgId,
            target.UserId);
        var (actor, actorRoleId) = await MemberRankTestScenario.SignInFullPermissionAdminAsync(
            host,
            client,
            founder.OrgId);
        var adminRoleId = await MemberRankTestScenario.BuiltInRoleIdAsync(
            host.Fixture,
            founder.OrgId,
            "tenant_admin");

        return new CeilingScene(
            founder.OrgId,
            owner,
            actor,
            actorRoleId,
            targetMembership,
            adminRoleId,
            $"ceiling-{founder.OrgId:N}@example.test");
    }

    private static async Task<long> InviteCountAsync(SessionTestHost host, string email) =>
        (long)(await host.ScalarAsync(
            "select count(*) from public.membership where invited_email = @email",
            [("email", email)]))!;

    private static Task<HttpResponseMessage> SendGrantAsync(
        HttpClient client,
        Guid orgId,
        Guid membershipId,
        Guid roleId,
        SignedInMember caller,
        string key) =>
        MemberRankTestScenario.SendMutationAsync(
            client,
            HttpMethod.Post,
            $"/v1/orgs/{orgId}/memberships/{membershipId}/roles",
            caller,
            orgId,
            key,
            $$"""{"role_id":"{{roleId}}"}""",
            ifMatch: null);

    private static Task<HttpResponseMessage> SendInviteAsync(
        HttpClient client,
        Guid orgId,
        string email,
        Guid roleId,
        SignedInMember caller,
        string key) =>
        MemberRankTestScenario.SendMutationAsync(
            client,
            HttpMethod.Post,
            $"/v1/orgs/{orgId}/memberships",
            caller,
            orgId,
            key,
            $$"""{"email":"{{email}}","roles":["{{roleId}}"]}""",
            ifMatch: null);

    private sealed record CeilingScene(
        Guid OrgId,
        SignedInMember Owner,
        SignedInMember Actor,
        Guid ActorRoleId,
        Guid TargetMembership,
        Guid AdminRoleId,
        string Email);
}
