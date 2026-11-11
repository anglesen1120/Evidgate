using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// The rank hierarchy of <c>DELETE /v1/auth/sessions/{s}</c>: a caller may end another member's session only when
/// that member does not outrank it in the ACTING org, the same question
/// <c>POST /v1/auth/users/{u}/sessions/revoke-all</c> asks (0091), and an id the caller may not reach is the same
/// 404 <c>not_found</c> an id that never existed gets.
/// </summary>
/// <remarks>
/// A refusal changes nothing: the target stays live and the acting org's chain gains no row. A successful
/// administrative revoke writes exactly one <c>auth.session_revoke_admin</c> row, inside the revoke's transaction.
/// A <c>tenant_admin</c> is rank 2, a custom <c>membership:write</c> role rank 1 and a plain member rank 0
/// (<c>evidgate.session_admin_rank</c>), so the equal- and lower-rank cases below are the positive controls of the
/// refusal cases.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AdminRevokeOneHierarchyTests : TenantTest
{
    /// <summary>Shares the assembly database: every case creates its own org and reads only its own rows.</summary>
    public AdminRevokeOneHierarchyTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// A session of a user who is not a member of the acting org answers the SAME 404 as an unknown id — status
    /// and body — while the same admin ends the session of a member who also belongs to a second org. The foreign
    /// row belongs to a real signed-in account, which is what makes the "no oracle" claim mean something.
    /// </summary>
    [Fact]
    public async Task A_Foreign_Session_Answers_The_Same_404_As_An_Unknown_Id()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();

        var member = await host.SignInMemberAsync(client);
        var secondOrg = await host.SeedOrganizationAsync();
        await host.SeedMembershipAsync(secondOrg, member.UserId);

        var outsider = await host.SignInMemberAsync(client);
        var outsiderSessionId = await host.SessionIdOfAsync(outsider.SessionCookie);

        var admin = await AdminRevokeAllTestData.SignInCustomAdminAsync(host, client, member.OrgId);
        var rowsBefore = await AdminRevokeAllTestData.AdminRowCountAsync(host, member.OrgId);

        using var foreign = await AdminRevokeAllTestData.RevokeOneAsync(
            client, admin, outsiderSessionId, member.OrgId);
        using var unknown = await AdminRevokeAllTestData.RevokeOneAsync(
            client, admin, Guid.CreateVersion7(), member.OrgId);

        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the target is not a member of the acting org");
        unknown.StatusCode.ShouldBe(foreign.StatusCode);
        (await AdminRevokeAllTestData.RawBodyAsync(foreign)).ShouldBe(
            await AdminRevokeAllTestData.RawBodyAsync(unknown),
            "a foreign session id and an unknown id are one answer, byte for byte");

        (await host.SessionRowAsync(outsiderSessionId)).RevokedAt.ShouldBeNull("a refused revoke writes nothing");
        (await AdminRevokeAllTestData.AdminRowCountAsync(host, member.OrgId)).ShouldBe(
            rowsBefore, "a refusal appends no row");

        // Control: the same admin, the same org header, a target that IS an active member of the org.
        var memberSessionId = await host.SessionIdOfAsync(member.SessionCookie);

        using var allowed = await AdminRevokeAllTestData.RevokeOneAsync(
            client, admin, memberSessionId, member.OrgId);

        allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent, "the member does not outrank the admin");
        (await host.SessionRowAsync(memberSessionId)).RevokedAt.ShouldNotBeNull();
        (await AdminRevokeAllTestData.AdminRowCountAsync(host, member.OrgId)).ShouldBe(
            rowsBefore + 1, "the one administrative revoke lands one audit row");
    }

    /// <summary>
    /// A custom administrator (rank 1) may not end a <c>tenant_admin</c>'s (rank 2) session: 404 <c>not_found</c>,
    /// the owner's row stays live and still authenticates, and no audit row is written.
    /// </summary>
    [Fact]
    public async Task A_Member_Administrator_Cannot_End_A_Tenant_Admins_Session()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var owner = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");
        var ownerSessionId = await host.SessionIdOfAsync(owner.SessionCookie);
        var admin = await AdminRevokeAllTestData.SignInCustomAdminAsync(host, client, member.OrgId);
        var rowsBefore = await AdminRevokeAllTestData.AdminRowCountAsync(host, member.OrgId);

        using var refused = await AdminRevokeAllTestData.RevokeOneAsync(
            client, admin, ownerSessionId, member.OrgId);

        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the owner outranks the custom administrator");
        (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);

        (await host.SessionRowAsync(ownerSessionId)).RevokedAt.ShouldBeNull("the refused revoke wrote nothing");
        (await AdminRevokeAllTestData.AdminRowCountAsync(host, member.OrgId)).ShouldBe(
            rowsBefore, "a rank refusal appends no row");

        using var next = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            AdminRevokeAllTestData.SingleSessionPath,
            SessionTestHost.CookieHeaderFor(owner.SessionCookie));

        next.StatusCode.ShouldBe(HttpStatusCode.OK, "the owner's session is still the credential");
    }

    /// <summary>Equal rank at the top is allowed: one <c>tenant_admin</c> ends another's session, one row.</summary>
    [Fact]
    public async Task A_Tenant_Admin_Ends_Another_Tenant_Admins_Session()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var owner = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");
        var otherOwner = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");
        var otherSessionId = await host.SessionIdOfAsync(otherOwner.SessionCookie);
        var rowsBefore = await AdminRevokeAllTestData.AdminRowCountAsync(host, member.OrgId);

        using var revoked = await AdminRevokeAllTestData.RevokeOneAsync(
            client, owner, otherSessionId, member.OrgId);

        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent, "equal rank is not a refusal");
        (await host.SessionRowAsync(otherSessionId)).RevokedAt.ShouldNotBeNull();
        await AssertOneAdminRowAsync(host, member.OrgId, rowsBefore, owner.UserId, otherOwner.UserId, otherSessionId);
    }

    /// <summary>Equal rank among custom administrators is allowed, and writes exactly one row.</summary>
    [Fact]
    public async Task A_Member_Administrator_Ends_An_Equal_Rank_Administrators_Session()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var admin = await AdminRevokeAllTestData.SignInCustomAdminAsync(host, client, member.OrgId);
        var peer = await AdminRevokeAllTestData.SignInCustomAdminAsync(host, client, member.OrgId);
        var peerSessionId = await host.SessionIdOfAsync(peer.SessionCookie);
        var rowsBefore = await AdminRevokeAllTestData.AdminRowCountAsync(host, member.OrgId);

        using var revoked = await AdminRevokeAllTestData.RevokeOneAsync(
            client, admin, peerSessionId, member.OrgId);

        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent, "rank 1 may end rank 1");
        (await host.SessionRowAsync(peerSessionId)).RevokedAt.ShouldNotBeNull();
        await AssertOneAdminRowAsync(host, member.OrgId, rowsBefore, admin.UserId, peer.UserId, peerSessionId);
    }

    /// <summary>The same-org lower rank still works: a custom administrator ends a plain member's session.</summary>
    [Fact]
    public async Task A_Member_Administrator_Ends_A_Plain_Members_Session()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var target = await host.SignInMemberInOrgAsync(client, member.OrgId, roleCode: null);
        var targetSessionId = await host.SessionIdOfAsync(target.SessionCookie);
        var admin = await AdminRevokeAllTestData.SignInCustomAdminAsync(host, client, member.OrgId);
        var rowsBefore = await AdminRevokeAllTestData.AdminRowCountAsync(host, member.OrgId);

        using var revoked = await AdminRevokeAllTestData.RevokeOneAsync(
            client, admin, targetSessionId, member.OrgId);

        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent, "rank 1 outranks rank 0");
        (await host.SessionRowAsync(targetSessionId)).RevokedAt.ShouldNotBeNull();
        await AssertOneAdminRowAsync(host, member.OrgId, rowsBefore, admin.UserId, target.UserId, targetSessionId);
    }

    /// <summary>
    /// The delegating write leaves the chain's existing entries byte-identical: an administrative revoke appends at
    /// the tip, and every row written before it keeps its sequence, <c>prev_hash</c> and <c>entry_hash</c>.
    /// </summary>
    [Fact]
    public async Task An_Admin_Revoke_Leaves_The_Earlier_Chain_Entries_Byte_Identical()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var victim = await host.SignInMemberAsync(client);
        var victimSessionId = await host.SessionIdOfAsync(victim.SessionCookie);
        var admin = await host.SignInMemberInOrgAsync(client, victim.OrgId, "tenant_admin");

        var entriesBefore = await ChainEntriesAsync(host, victim.OrgId);
        entriesBefore.ShouldNotBeEmpty("the sign-in wrote the chain this case compares against");

        using var revoked = await AdminRevokeAllTestData.RevokeOneAsync(
            client, admin, victimSessionId, victim.OrgId);
        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ChainEntriesAsync(host, victim.OrgId)).ShouldStartWith(
            entriesBefore,
            Case.Sensitive,
            "the append extends the chain; it never rewrites an entry already on it");
    }

    /// <summary>One administrative revoke: one more audit row, naming this actor, target and revoked session.</summary>
    private static async Task AssertOneAdminRowAsync(
        SessionTestHost host,
        Guid orgId,
        long rowsBefore,
        Guid actorId,
        Guid targetUserId,
        Guid sessionId)
    {
        (await AdminRevokeAllTestData.AdminRowCountAsync(host, orgId)).ShouldBe(
            rowsBefore + 1, "exactly one audit row, not one per org and not a second on the same call");

        using var document = JsonDocument.Parse(
            await AdminRevokeAllTestData.NewestAdminRowJsonAsync(host, orgId));
        var row = document.RootElement;

        row.GetProperty("action").GetString().ShouldBe(AdminRevokeAllTestData.AdminRevokeAction);
        row.GetProperty("actor_id").GetString().ShouldBe(actorId.ToString("D"), "the actor is the caller");
        row.GetProperty("object_type").GetString().ShouldBe("session");
        row.GetProperty("object_id").GetString().ShouldBe(sessionId.ToString("D"));
        row.GetProperty("metadata").GetProperty("target_user_id").GetString()
            .ShouldBe(targetUserId.ToString("D"), "the row names the session's owner");
    }

    /// <summary>One org's chain as <c>seq|prev_hash|entry_hash</c> lines, oldest first.</summary>
    private static async Task<string> ChainEntriesAsync(SessionTestHost host, Guid orgId) =>
        (string)(await host.ScalarAsync(
            "select coalesce(string_agg(a.seq || '|' || encode(a.prev_hash, 'hex') "
            + "|| '|' || encode(a.entry_hash, 'hex'), chr(10) order by a.seq), '') "
            + "from public.audit_log a where a.org_id = @org",
            [("org", orgId)]))!;
}
