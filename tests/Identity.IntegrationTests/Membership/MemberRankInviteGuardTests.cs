using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Testing;
using Identity.IntegrationTests.Sessions.Http;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Membership;

/// <summary>
/// The pending-invite half of the rank rule after the guard split: a lower-rank administrator cannot PATCH a
/// pending owner's row even with a stale precondition, and cannot resend a pending owner invite at all, because
/// a resend would mint a fresh working token for a rank it does not hold.
/// </summary>
/// <remarks>
/// The PATCH case sends a valid but stale <c>If-Match</c>: the rank check runs before the version check, so the
/// answer must be 403 rather than 412/409. The resend case reads the row's stored prefix and digest around the
/// refusal, so a silent rotation would fail the test.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class MemberRankInviteGuardTests : TenantTest
{
    public MemberRankInviteGuardTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>
    /// A lower-rank administrator's PATCH of a pending owner invite is 403 even with a stale If-Match; with only
    /// the caller changed, an owner reaches the pending row's own 409 <c>invalid_transition</c>.
    /// </summary>
    [Fact]
    public async Task Lower_Rank_Admin_Cannot_Patch_A_Pending_Owner_Invite_Even_With_A_Stale_If_Match()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var scene = await SeedPendingOwnerInviteAsync(host, client);
        var version = await MemberRankTestScenario.MembershipVersionAsync(host, scene.InviteId);

        using var denied = await SendPendingPatchAsync(
            client,
            scene,
            scene.Actor,
            staleVersion: version + 1,
            "rank-invite-patch-stale");

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(denied)).ShouldBe(
            ApiErrorCode.Forbidden,
            "the rank refusal is decided before the stale precondition");
        (await MemberRankTestScenario.MembershipStatusAsync(host, scene.InviteId)).ShouldBe("pending");

        using var stateRefusal = await SendPendingPatchAsync(
            client,
            scene,
            scene.Owner,
            staleVersion: version,
            "rank-invite-patch-owner");

        stateRefusal.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await SessionTestHost.ErrorCodeAsync(stateRefusal)).ShouldBe(ApiErrorCode.InvalidTransition);
        (await MemberRankTestScenario.MembershipStatusAsync(host, scene.InviteId)).ShouldBe("pending");
    }

    /// <summary>
    /// A lower-rank administrator's resend of a pending owner invite is 403 and leaves the stored prefix and
    /// digest unchanged; an owner's resend is 202 and does rotate them.
    /// </summary>
    [Fact]
    public async Task Lower_Rank_Admin_Cannot_Resend_A_Pending_Owner_Invite_But_An_Owner_Can()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var founder = await host.SignInMemberAsync(client);
        var owner = await host.SignInMemberInOrgAsync(client, founder.OrgId, "tenant_admin");
        var adminRoleId = await MemberRankTestScenario.BuiltInRoleIdAsync(
            host.Fixture,
            founder.OrgId,
            "tenant_admin");
        var prefix = "before-" + Guid.CreateVersion7().ToString("N");
        var hash = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var inviteId = await MemberRankTestScenario.SeedPendingInviteAsync(
            host,
            founder.OrgId,
            adminRoleId,
            invitePrefix: prefix,
            inviteHash: hash);
        var actor = await MemberRankTestScenario.SignInCustomAdminAsync(host, client, founder.OrgId);
        var before = await MemberRankTestScenario.InviteTokenStateAsync(host, inviteId);

        before.ShouldNotBeNull("the seeded invite carries a prefix and digest");

        using var denied = await SendResendAsync(client, founder.OrgId, inviteId, actor, "rank-invite-resend-denied");

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(denied)).ShouldBe(ApiErrorCode.Forbidden);
        (await MemberRankTestScenario.InviteTokenStateAsync(host, inviteId)).ShouldBe(
            before,
            "a refused resend must not rotate the invite token");
        (await MemberRankTestScenario.MembershipStatusAsync(host, inviteId)).ShouldBe("pending");

        using var allowed = await SendResendAsync(client, founder.OrgId, inviteId, owner, "rank-invite-resend-owner");

        allowed.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await MemberRankTestScenario.InviteTokenStateAsync(host, inviteId)).ShouldNotBe(
            before,
            "an owner's resend rotates the prefix and digest");
    }

    private static async Task<PendingInviteScene> SeedPendingOwnerInviteAsync(
        SessionTestHost host,
        HttpClient client)
    {
        var founder = await host.SignInMemberAsync(client);
        var owner = await host.SignInMemberInOrgAsync(client, founder.OrgId, "tenant_admin");
        var adminRoleId = await MemberRankTestScenario.BuiltInRoleIdAsync(
            host.Fixture,
            founder.OrgId,
            "tenant_admin");
        var inviteId = await MemberRankTestScenario.SeedPendingInviteAsync(host, founder.OrgId, adminRoleId);
        var actor = await MemberRankTestScenario.SignInCustomAdminAsync(host, client, founder.OrgId);

        return new PendingInviteScene(founder.OrgId, owner, actor, inviteId);
    }

    private static Task<HttpResponseMessage> SendPendingPatchAsync(
        HttpClient client,
        PendingInviteScene scene,
        SignedInMember caller,
        int staleVersion,
        string key) =>
        MemberRankTestScenario.SendMutationAsync(
            client,
            HttpMethod.Patch,
            $"/v1/orgs/{scene.OrgId}/memberships/{scene.InviteId}",
            caller,
            scene.OrgId,
            key,
            """{"status":"removed"}""",
            staleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static Task<HttpResponseMessage> SendResendAsync(
        HttpClient client,
        Guid orgId,
        Guid inviteId,
        SignedInMember caller,
        string key) =>
        MemberRankTestScenario.SendMutationAsync(
            client,
            HttpMethod.Post,
            $"/v1/orgs/{orgId}/memberships/{inviteId}/resend",
            caller,
            orgId,
            key,
            jsonBody: null,
            ifMatch: null);

    private sealed record PendingInviteScene(
        Guid OrgId,
        SignedInMember Owner,
        SignedInMember Actor,
        Guid InviteId);
}
