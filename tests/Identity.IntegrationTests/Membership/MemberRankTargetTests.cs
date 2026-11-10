using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Testing;
using Identity.IntegrationTests.Sessions.Http;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Membership;

/// <summary>
/// The target half of the member rank rule introduced with <c>membership_rank</c>: the target is the
/// membership row, so a removed owner's row and a pending invite keep the rank their grants give them.
/// Allowing a removed or pending owner to be edited by a lower-rank administrator would let that
/// administrator undo the owner's grants after the fact.
/// </summary>
/// <remarks>
/// Every call runs through the real routes, so the permission pipeline, the rank statements and RLS are the
/// deployed ones. Each refusal case repeats the same request as an owner, so only the caller's rank changes.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class MemberRankTargetTests : TenantTest
{
    public MemberRankTargetTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>A lower-rank administrator cannot revoke a removed owner's <c>tenant_admin</c> grant; an owner can.</summary>
    [Fact]
    public async Task Lower_Rank_Admin_Cannot_Revoke_A_Removed_Owners_Admin_Role_But_An_Owner_Can()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var scene = await SeedRemovedOwnerAsync(host, client);

        using var denied = await SendRevokeAsync(client, scene, scene.Actor, "rank-target-removed-revoke");

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(denied)).ShouldBe(ApiErrorCode.Forbidden);
        (await MemberRankTestScenario.HoldsRoleAsync(host, scene.MembershipId, scene.AdminRoleId)).ShouldBeTrue(
            "the refused revoke leaves the removed owner's grant in place");

        using var allowed = await SendRevokeAsync(client, scene, scene.Owner, "rank-target-removed-owner-revoke");

        allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await MemberRankTestScenario.HoldsRoleAsync(host, scene.MembershipId, scene.AdminRoleId)).ShouldBeFalse(
            "an owner may act on an owner's dead row");
    }

    /// <summary>A lower-rank administrator cannot disable a removed owner's row; an owner's retry is the no-op 200.</summary>
    [Fact]
    public async Task Lower_Rank_Admin_Cannot_Disable_A_Removed_Owner_But_An_Owner_Retry_Is_200()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var scene = await SeedRemovedOwnerAsync(host, client);

        using var denied = await SendDisableAsync(client, scene, scene.Actor, "rank-target-removed-patch");

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(denied)).ShouldBe(ApiErrorCode.Forbidden);
        (await MemberRankTestScenario.MembershipStatusAsync(host, scene.MembershipId)).ShouldBe("removed");
        (await MemberRankTestScenario.HoldsRoleAsync(host, scene.MembershipId, scene.AdminRoleId)).ShouldBeTrue();

        using var allowed = await SendDisableAsync(client, scene, scene.Owner, "rank-target-removed-owner-patch");

        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await MemberRankTestScenario.MembershipStatusAsync(host, scene.MembershipId)).ShouldBe("removed");
    }

    /// <summary>A lower-rank administrator cannot remove a removed owner's row; an owner's retry is the no-op 204.</summary>
    [Fact]
    public async Task Lower_Rank_Admin_Cannot_Remove_A_Removed_Owner_But_An_Owner_Retry_Is_204()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var scene = await SeedRemovedOwnerAsync(host, client);

        using var denied = await SendRemoveAsync(client, scene, scene.Actor, "rank-target-removed-delete");

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(denied)).ShouldBe(ApiErrorCode.Forbidden);
        (await MemberRankTestScenario.MembershipStatusAsync(host, scene.MembershipId)).ShouldBe("removed");
        (await MemberRankTestScenario.HoldsRoleAsync(host, scene.MembershipId, scene.AdminRoleId)).ShouldBeTrue();

        using var allowed = await SendRemoveAsync(client, scene, scene.Owner, "rank-target-removed-owner-delete");

        allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await MemberRankTestScenario.MembershipStatusAsync(host, scene.MembershipId)).ShouldBe("removed");
    }

    /// <summary>A lower-rank administrator cannot revoke a pending owner invite's grant; an owner can.</summary>
    [Fact]
    public async Task Lower_Rank_Admin_Cannot_Revoke_A_Pending_Owner_Invites_Role_But_An_Owner_Can()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var scene = await SeedPendingOwnerInviteAsync(host, client);

        using var denied = await SendPendingRevokeAsync(client, scene, scene.Actor, "rank-target-pending-revoke");

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(denied)).ShouldBe(ApiErrorCode.Forbidden);
        (await MemberRankTestScenario.HoldsRoleAsync(host, scene.InviteId, scene.AdminRoleId)).ShouldBeTrue();

        using var allowed = await SendPendingRevokeAsync(client, scene, scene.Owner, "rank-target-pending-owner-revoke");

        allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await MemberRankTestScenario.HoldsRoleAsync(host, scene.InviteId, scene.AdminRoleId)).ShouldBeFalse();
    }

    /// <summary>
    /// A lower-rank administrator's removal of a pending owner invite is the rank refusal, not the pending row's
    /// 409 <c>invalid_transition</c>; with only the caller changed, an owner reaches that state refusal.
    /// </summary>
    [Fact]
    public async Task Lower_Rank_Admin_Cannot_Remove_A_Pending_Owner_Invite_And_403_Precedes_409()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var scene = await SeedPendingOwnerInviteAsync(host, client);

        using var denied = await SendPendingRemoveAsync(client, scene, scene.Actor, "rank-target-pending-delete");

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(denied)).ShouldBe(
            ApiErrorCode.Forbidden,
            "the rank refusal is decided before the pending row's invalid_transition");
        (await MemberRankTestScenario.MembershipStatusAsync(host, scene.InviteId)).ShouldBe("pending");

        using var stateRefusal = await SendPendingRemoveAsync(
            client,
            scene,
            scene.Owner,
            "rank-target-pending-owner-delete");

        stateRefusal.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await SessionTestHost.ErrorCodeAsync(stateRefusal)).ShouldBe(ApiErrorCode.InvalidTransition);
    }

    /// <summary>A lower-rank administrator cannot cancel a pending owner invite; an owner can.</summary>
    [Fact]
    public async Task Lower_Rank_Admin_Cannot_Cancel_A_Pending_Owner_Invite_But_An_Owner_Can()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var scene = await SeedPendingOwnerInviteAsync(host, client);

        using var denied = await SendCancelInviteAsync(client, scene, scene.Actor, "rank-target-pending-cancel");

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(denied)).ShouldBe(ApiErrorCode.Forbidden);
        (await MemberRankTestScenario.MembershipStatusAsync(host, scene.InviteId)).ShouldBe("pending");

        using var allowed = await SendCancelInviteAsync(client, scene, scene.Owner, "rank-target-pending-owner-cancel");

        allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await MemberRankTestScenario.MembershipStatusAsync(host, scene.InviteId)).ShouldBe("revoked");
        (await MemberRankTestScenario.HoldsRoleAsync(host, scene.InviteId, scene.AdminRoleId)).ShouldBeTrue(
            "cancelling the invite keeps the grant history on the row");
    }

    /// <summary>
    /// Equal rank is enough to cancel: a custom administrator cancels an invite that carries another custom
    /// administrator's <c>membership:write</c> role, so both target rows rank one.
    /// </summary>
    [Fact]
    public async Task Custom_Admin_Cancels_An_Equal_Rank_Custom_Admin_Invite()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var founder = await host.SignInMemberAsync(client);
        var actor = await MemberRankTestScenario.SignInCustomAdminAsync(host, client, founder.OrgId);
        var targetRoleId = await MemberRankTestScenario.SeedCustomRoleAsync(host, founder.OrgId);
        var inviteId = await MemberRankTestScenario.SeedPendingInviteAsync(host, founder.OrgId, targetRoleId);

        using var cancelled = await SendInviteCancelAsync(
            client,
            founder.OrgId,
            inviteId,
            actor,
            "rank-target-pending-equal-cancel");

        cancelled.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await MemberRankTestScenario.MembershipStatusAsync(host, inviteId)).ShouldBe("revoked");
    }

    /// <summary>
    /// An unbound pending invite has no user to rank, so the rank comes from its membership id: the grant makes it
    /// rank one, and an equal-rank custom administrator may cancel it. The case checks the null user and the rank
    /// itself before the route, so a binding that silently ranked zero could not pass this test.
    /// </summary>
    [Fact]
    public async Task Unbound_Pending_Invite_With_Membership_Write_Ranks_From_Its_Membership_Row()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var founder = await host.SignInMemberAsync(client);
        var actor = await MemberRankTestScenario.SignInCustomAdminAsync(host, client, founder.OrgId);
        var targetRoleId = await MemberRankTestScenario.SeedCustomRoleAsync(host, founder.OrgId);
        var inviteId = await MemberRankTestScenario.SeedPendingInviteAsync(host, founder.OrgId, targetRoleId);

        (await host.ScalarAsync(
            "select user_id from public.membership where id = @id",
            [("id", inviteId)])).ShouldBeNull("an unbound invite has no user row to rank");

        await using var scope = await Fixture.BeginTenantScopeAsync(founder.OrgId, cancellationToken: Token);
        await using var rankCommand = scope.Command(
            "select evidgate.membership_rank(@org_id, @membership_id)");
        rankCommand.Parameters.AddWithValue("org_id", founder.OrgId);
        rankCommand.Parameters.AddWithValue("membership_id", inviteId);
        ((int)(await rankCommand.ExecuteScalarAsync(Token))!).ShouldBe(
            1,
            "`membership_rank` reads the grant of the membership row, not a user's active membership");

        using var cancelled = await SendInviteCancelAsync(
            client,
            founder.OrgId,
            inviteId,
            actor,
            "rank-target-unbound-cancel");

        cancelled.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await MemberRankTestScenario.MembershipStatusAsync(host, inviteId)).ShouldBe("revoked");
    }

    /// <summary>
    /// Granting is deliberately outside the rank rule: a lower-rank administrator may grant a role it already
    /// holds to a higher-rank member. This is not a bypass — <c>SelfEscalation</c> limits every grant to the
    /// permissions the caller holds, so the owner gains nothing the administrator could not already do.
    /// </summary>
    [Fact]
    public async Task Lower_Rank_Admin_Grants_A_Role_It_Holds_To_An_Owner()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var founder = await host.SignInMemberAsync(client);
        var owner = await host.SignInMemberInOrgAsync(client, founder.OrgId, "tenant_admin");
        var (actor, actorRoleId) = await MemberRankTestScenario.SignInCustomAdminWithRoleAsync(
            host,
            client,
            founder.OrgId);
        var ownerMembership = await MemberRankTestScenario.MembershipIdAsync(host, founder.OrgId, owner.UserId);

        using var granted = await MemberRankTestScenario.SendMutationAsync(
            client,
            HttpMethod.Post,
            $"/v1/orgs/{founder.OrgId}/memberships/{ownerMembership}/roles",
            actor,
            founder.OrgId,
            "rank-target-grant-sibling",
            $$"""{"role_id":"{{actorRoleId}}"}""",
            ifMatch: null);

        granted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await MemberRankTestScenario.HoldsRoleAsync(host, ownerMembership, actorRoleId)).ShouldBeTrue(
            "the role the caller holds is within the permissions it may hand out");
    }

    /// <summary>One org with an active owner, a removed owner that keeps its <c>tenant_admin</c>, and a lower-rank actor.</summary>
    private static async Task<RemovedOwnerScene> SeedRemovedOwnerAsync(SessionTestHost host, HttpClient client)
    {
        var founder = await host.SignInMemberAsync(client);
        var owner = await host.SignInMemberInOrgAsync(client, founder.OrgId, "tenant_admin");
        var removedOwner = await host.SignInMemberInOrgAsync(client, founder.OrgId, "tenant_admin");
        var adminRoleId = await MemberRankTestScenario.BuiltInRoleIdAsync(host.Fixture, founder.OrgId, "tenant_admin");
        var membershipId = await MemberRankTestScenario.MembershipIdAsync(host, founder.OrgId, removedOwner.UserId);

        await host.ArrangeAsync(
            "update public.membership set status = 'removed' where id = @id",
            ("id", membershipId));

        var actor = await MemberRankTestScenario.SignInCustomAdminAsync(host, client, founder.OrgId);
        var version = await MemberRankTestScenario.MembershipVersionAsync(host, membershipId);

        return new RemovedOwnerScene(
            founder.OrgId,
            owner,
            actor,
            membershipId,
            adminRoleId,
            version);
    }

    /// <summary>One org with an active owner, a pending owner invite carrying <c>tenant_admin</c>, and a lower-rank actor.</summary>
    private static async Task<PendingOwnerScene> SeedPendingOwnerInviteAsync(
        SessionTestHost host,
        HttpClient client)
    {
        var founder = await host.SignInMemberAsync(client);
        var owner = await host.SignInMemberInOrgAsync(client, founder.OrgId, "tenant_admin");
        var adminRoleId = await MemberRankTestScenario.BuiltInRoleIdAsync(host.Fixture, founder.OrgId, "tenant_admin");
        var inviteId = await MemberRankTestScenario.SeedPendingInviteAsync(host, founder.OrgId, adminRoleId);
        var actor = await MemberRankTestScenario.SignInCustomAdminAsync(host, client, founder.OrgId);

        return new PendingOwnerScene(founder.OrgId, owner, actor, inviteId, adminRoleId);
    }

    private static Task<HttpResponseMessage> SendRevokeAsync(
        HttpClient client,
        RemovedOwnerScene scene,
        SignedInMember caller,
        string key) =>
        MemberRankTestScenario.SendMutationAsync(
            client,
            HttpMethod.Delete,
            $"/v1/orgs/{scene.OrgId}/memberships/{scene.MembershipId}/roles/{scene.AdminRoleId}",
            caller,
            scene.OrgId,
            key,
            jsonBody: null,
            ifMatch: null);

    private static Task<HttpResponseMessage> SendDisableAsync(
        HttpClient client,
        RemovedOwnerScene scene,
        SignedInMember caller,
        string key) =>
        MemberRankTestScenario.SendMutationAsync(
            client,
            HttpMethod.Patch,
            $"/v1/orgs/{scene.OrgId}/memberships/{scene.MembershipId}",
            caller,
            scene.OrgId,
            key,
            """{"status":"removed"}""",
            scene.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static Task<HttpResponseMessage> SendRemoveAsync(
        HttpClient client,
        RemovedOwnerScene scene,
        SignedInMember caller,
        string key) =>
        MemberRankTestScenario.SendMutationAsync(
            client,
            HttpMethod.Delete,
            $"/v1/orgs/{scene.OrgId}/memberships/{scene.MembershipId}",
            caller,
            scene.OrgId,
            key,
            jsonBody: null,
            ifMatch: null);

    private static Task<HttpResponseMessage> SendPendingRevokeAsync(
        HttpClient client,
        PendingOwnerScene scene,
        SignedInMember caller,
        string key) =>
        MemberRankTestScenario.SendMutationAsync(
            client,
            HttpMethod.Delete,
            $"/v1/orgs/{scene.OrgId}/memberships/{scene.InviteId}/roles/{scene.AdminRoleId}",
            caller,
            scene.OrgId,
            key,
            jsonBody: null,
            ifMatch: null);

    private static Task<HttpResponseMessage> SendPendingRemoveAsync(
        HttpClient client,
        PendingOwnerScene scene,
        SignedInMember caller,
        string key) =>
        MemberRankTestScenario.SendMutationAsync(
            client,
            HttpMethod.Delete,
            $"/v1/orgs/{scene.OrgId}/memberships/{scene.InviteId}",
            caller,
            scene.OrgId,
            key,
            jsonBody: null,
            ifMatch: null);

    private static Task<HttpResponseMessage> SendCancelInviteAsync(
        HttpClient client,
        PendingOwnerScene scene,
        SignedInMember caller,
        string key) =>
        SendInviteCancelAsync(client, scene.OrgId, scene.InviteId, caller, key);

    private static Task<HttpResponseMessage> SendInviteCancelAsync(
        HttpClient client,
        Guid orgId,
        Guid inviteId,
        SignedInMember caller,
        string key) =>
        MemberRankTestScenario.SendMutationAsync(
            client,
            HttpMethod.Post,
            $"/v1/orgs/{orgId}/memberships/{inviteId}/cancel-invite",
            caller,
            orgId,
            key,
            jsonBody: null,
            ifMatch: null);

    private sealed record RemovedOwnerScene(
        Guid OrgId,
        SignedInMember Owner,
        SignedInMember Actor,
        Guid MembershipId,
        Guid AdminRoleId,
        int Version);

    private sealed record PendingOwnerScene(
        Guid OrgId,
        SignedInMember Owner,
        SignedInMember Actor,
        Guid InviteId,
        Guid AdminRoleId);
}
