using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// Every refusal of <c>POST /v1/auth/users/{u}/sessions/revoke-all</c> over the real host: the caller lacking
/// <c>membership:write</c>, a foreign org, an unknown user, a malformed id or absent header, a target that is not
/// an active member, and the caller addressing itself.
/// </summary>
/// <remarks>
/// <para>
/// Every one of them is the single revoke's one 404, asserted byte for byte: an envelope that differs by a byte
/// is an oracle about other accounts. The two pre-handler refusals (401 without a session, 403 without the
/// antiforgery header) are here too, because they are what stops the route short of the definer.
/// </para>
/// <para>
/// A refusal changes nothing: no session row of the target and no audit row of the org.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AdminRevokeAllRefusalTests : TenantTest
{
    /// <summary>
    /// One request id, sent with every probe: 00 §9.1 has the edge echo an inbound <c>X-Request-Id</c> and 00 §9.2
    /// has the envelope carry it, so a fixed value makes the compared bodies identical without re-serialising them.
    /// </summary>
    private const string FixedRequestId = "req-refusal-parity";

    /// <summary>Own database: the class precedes the route's rollback case, which needs its own copy.</summary>
    public AdminRevokeAllRefusalTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>
    /// A membership is not the permission: a member of the same org holding no role answers 404 <c>not_found</c>
    /// and nothing — not one row of the target, not one audit row — changes.
    /// </summary>
    [Fact]
    public async Task A_Caller_Without_Membership_Write_Answers_404_And_Changes_Nothing()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var memberSessionId = await host.SessionIdOfAsync(member.SessionCookie);
        var plain = await host.SignInMemberInOrgAsync(client, member.OrgId, roleCode: null);

        using (var refused = await AdminRevokeAllTestData.RevokeAllAsync(client, plain, member.UserId, member.OrgId))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, "an unauthorised caller learns nothing about the target");
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        (await host.SessionRowAsync(memberSessionId)).RevokedAt.ShouldBeNull("a refused call revokes nothing");
        (await AdminRevokeAllTestData.LiveCountAsync(host, member.UserId)).ShouldBe(1L);
        (await AdminRevokeAllTestData.AdminRowCountAsync(host, member.OrgId)).ShouldBe(0L);
    }

    /// <summary>
    /// The caller must hold <c>membership:write</c> in the org the header NAMES: an admin of another org naming
    /// this one answers 404, and an admin of this org naming another answers 404 too — the header cannot widen
    /// what the definer authorises.
    /// </summary>
    [Fact]
    public async Task An_Admin_Of_Another_Org_Answers_404_And_Changes_Nothing()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var memberSessionId = await host.SessionIdOfAsync(member.SessionCookie);

        var otherOrg = await host.SeedOrganizationAsync();
        var foreignAdmin = await host.SignInMemberInOrgAsync(client, otherOrg, "tenant_admin");

        using (var refused = await AdminRevokeAllTestData.RevokeAllAsync(client, foreignAdmin, member.UserId, member.OrgId))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the caller administers no membership in this org");
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        var localAdmin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");

        using (var refused = await AdminRevokeAllTestData.RevokeAllAsync(client, localAdmin, member.UserId, otherOrg))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the org named is not one the caller administers");
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        (await host.SessionRowAsync(memberSessionId)).RevokedAt.ShouldBeNull();
        (await AdminRevokeAllTestData.AdminRowCountAsync(host, member.OrgId)).ShouldBe(0L);
    }

    /// <summary>
    /// An id that never existed answers the route's one 404, exactly as an unknown session id does on the single
    /// revoke, and writes nothing.
    /// </summary>
    [Fact]
    public async Task An_Unknown_User_Answers_404_And_Changes_Nothing()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");

        using (var refused = await AdminRevokeAllTestData.RevokeAllAsync(client, admin, Guid.CreateVersion7(), member.OrgId))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        (await AdminRevokeAllTestData.AdminRowCountAsync(host, member.OrgId)).ShouldBe(0L);
    }

    /// <summary>
    /// The target must be an ACTIVE member of the named org: an account of another org, and an account whose
    /// membership of this org was removed, both answer 404 and keep their live sessions.
    /// </summary>
    [Fact]
    public async Task A_Target_Who_Is_Not_An_Active_Member_Of_The_Org_Answers_404()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");

        // A real account of ANOTHER org, signed in: its row is live, but this org administers no membership of it.
        var stranger = await host.SignInMemberAsync(client);
        var strangerSessionId = await host.SessionIdOfAsync(stranger.SessionCookie);

        using (var refused = await AdminRevokeAllTestData.RevokeAllAsync(client, admin, stranger.UserId, member.OrgId))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        (await host.SessionRowAsync(strangerSessionId)).RevokedAt.ShouldBeNull("a refused call revokes nothing");

        // The removal path: the account keeps a live session while its membership of this org is no longer active.
        var removed = await host.SeedUserAsync();
        await host.SeedMembershipAsync(member.OrgId, removed, status: "removed");
        var removedTicket = await host.CreateSessionAsync(removed);
        var removedSessionId = await host.SessionIdOfAsync(removedTicket.CookieValue);

        using (var refused = await AdminRevokeAllTestData.RevokeAllAsync(client, admin, removed, member.OrgId))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a removed membership is not an administered member");
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        (await host.SessionRowAsync(removedSessionId)).RevokedAt.ShouldBeNull();
        (await AdminRevokeAllTestData.AdminRowCountAsync(host, member.OrgId)).ShouldBe(0L);
    }

    /// <summary>
    /// The caller cannot end its OWN sessions through the administrative route: addressing itself answers 404,
    /// because the self route is <c>POST /v1/auth/sessions/revoke-all</c> and this one only ever acts on another
    /// member. Nothing of the caller's is revoked.
    /// </summary>
    [Fact]
    public async Task An_Admin_Addressing_Itself_Answers_404()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");
        var adminSessionId = await host.SessionIdOfAsync(admin.SessionCookie);

        using (var refused = await AdminRevokeAllTestData.RevokeAllAsync(client, admin, admin.UserId, member.OrgId))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the self route is the only way to end your own sessions");
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        (await host.SessionRowAsync(adminSessionId)).RevokedAt.ShouldBeNull();
        (await AdminRevokeAllTestData.AdminRowCountAsync(host, member.OrgId)).ShouldBe(0L);
    }

    /// <summary>
    /// Every refusal of this route is the single revoke's <c>not_found</c> — the same status, code, message,
    /// details and field order, byte for byte — so a client cannot tell a non-admin, a foreign org, an unknown
    /// user, a malformed id, an absent header, a non-member or a self-addressed call from an unknown session id.
    /// A malformed <c>{u}</c> or org header is never the undocumented 400 parameter binding would raise before the
    /// credential was judged.
    /// </summary>
    [Fact]
    public async Task Every_Refusal_Is_Byte_Identical_To_The_Single_Revokes_404()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();

        // One fixed request id on every request of this case: the edge echoes it (00 §9.1) and the envelope
        // carries the echo (00 §9.2), so the raw bodies can be compared without touching the JSON.
        client.DefaultRequestHeaders.Add("X-Request-Id", FixedRequestId);

        var member = await host.SignInMemberAsync(client);
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");
        var plain = await host.SignInMemberInOrgAsync(client, member.OrgId, roleCode: null);
        var otherOrg = await host.SeedOrganizationAsync();
        var foreignAdmin = await host.SignInMemberInOrgAsync(client, otherOrg, "tenant_admin");
        var stranger = await host.SignInMemberAsync(client);
        var strangerSessionId = await host.SessionIdOfAsync(stranger.SessionCookie);

        using var baseline = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            AdminRevokeAllTestData.SingleSessionPath + "/" + Guid.CreateVersion7(),
            SessionTestHost.CookieHeaderFor(admin.SessionCookie, admin.Xsrf),
            xsrfToken: admin.Xsrf.RequestToken,
            orgHeader: member.OrgId.ToString("D"));

        baseline.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the single revoke's 404 is the reference envelope");
        var expected = await AdminRevokeAllTestData.RawBodyAsync(baseline);
        expected.ShouldNotBeEmpty();

        (SignedInMember Caller, string Path, string? OrgHeader, string Because)[] refusals =
        [
            (plain, AdminRevokeAllTestData.PathFor(member.UserId), member.OrgId.ToString("D"),
                "a member holding no membership:write"),
            (foreignAdmin, AdminRevokeAllTestData.PathFor(member.UserId), member.OrgId.ToString("D"),
                "an admin of another org"),
            (admin, AdminRevokeAllTestData.PathFor(Guid.CreateVersion7()), member.OrgId.ToString("D"),
                "an unknown user"),
            (admin, AdminRevokeAllTestData.Prefix + "not-a-uuid" + AdminRevokeAllTestData.Suffix,
                member.OrgId.ToString("D"), "a {u} that is not a uuid"),
            (admin, AdminRevokeAllTestData.PathFor(member.UserId), null, "no org header at all"),
            (admin, AdminRevokeAllTestData.PathFor(member.UserId), "not-a-uuid", "an org header that is not a uuid"),
            (admin, AdminRevokeAllTestData.PathFor(stranger.UserId), member.OrgId.ToString("D"),
                "a target who is not a member of this org"),
            (admin, AdminRevokeAllTestData.PathFor(admin.UserId), member.OrgId.ToString("D"), "the caller itself"),
        ];

        await AssertEveryRefusalMatchesAsync(client, baseline.StatusCode, expected, refusals);

        (await AdminRevokeAllTestData.LiveCountAsync(host, member.UserId)).ShouldBe(1L);
        (await host.SessionRowAsync(strangerSessionId)).RevokedAt.ShouldBeNull();
        (await AdminRevokeAllTestData.AdminRowCountAsync(host, member.OrgId)).ShouldBe(0L);
    }

    /// <summary>
    /// Sends every refusal probe on the same client, and compares its answer with the baseline's raw bytes: the
    /// status, the code, the message, the details and their field order must all be the baseline's.
    /// </summary>
    /// <param name="client">The client whose default <c>X-Request-Id</c> both sides carry.</param>
    /// <param name="baselineStatus">The single revoke's status.</param>
    /// <param name="expected">The single revoke's body, as it crossed the wire.</param>
    /// <param name="refusals">The probes: caller, path, org header and the reason it must refuse.</param>
    private static async Task AssertEveryRefusalMatchesAsync(
        HttpClient client,
        HttpStatusCode baselineStatus,
        byte[] expected,
        IEnumerable<(SignedInMember Caller, string Path, string? OrgHeader, string Because)> refusals)
    {
        foreach (var (caller, path, orgHeader, because) in refusals)
        {
            using var refused = await SessionTestHost.SendAsync(
                client,
                HttpMethod.Post,
                path,
                SessionTestHost.CookieHeaderFor(caller.SessionCookie, caller.Xsrf),
                xsrfToken: caller.Xsrf.RequestToken,
                orgHeader: orgHeader);

            refused.StatusCode.ShouldBe(baselineStatus, because + " is the route's one 404");
            (await AdminRevokeAllTestData.RawBodyAsync(refused)).ShouldBe(
                expected,
                because + ": a refusal of this route must be byte-identical to the single revoke's not_found");
        }
    }

    /// <summary>
    /// The mutation is cookie-authenticated, so the host-wide gate refuses it before any handler runs: a request
    /// carrying the session and the antiforgery COOKIE but no <c>X-XSRF-TOKEN</c> header is 403
    /// <c>csrf_failed</c>, and the target's sessions stay live.
    /// </summary>
    [Fact]
    public async Task A_Call_Without_The_Xsrf_Header_Is_403_CsrfFailed_And_Changes_Nothing()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var memberSessionId = await host.SessionIdOfAsync(member.SessionCookie);
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");

        using (var refused = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Post,
            AdminRevokeAllTestData.PathFor(member.UserId),
            SessionTestHost.CookieHeaderFor(admin.SessionCookie, admin.Xsrf),
            orgHeader: member.OrgId.ToString("D")))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.CsrfFailed);
        }

        (await host.SessionRowAsync(memberSessionId)).RevokedAt.ShouldBeNull("the gate runs before any handler");
        (await AdminRevokeAllTestData.AdminRowCountAsync(host, member.OrgId)).ShouldBe(0L);
    }

    /// <summary>
    /// The route needs a caller: without a session cookie it answers 401 <c>unauthenticated</c> through the
    /// authenticator, before any id, header or target is judged.
    /// </summary>
    [Fact]
    public async Task A_Call_Without_A_Session_Is_401_Unauthenticated()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        host.Recorder.Clear();

        var path = AdminRevokeAllTestData.PathFor(member.UserId);

        using (var refused = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Post,
            path,
            orgHeader: member.OrgId.ToString("D")))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the route cannot be driven anonymously");
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.Unauthenticated);
            host.Recorder.PatternFor(HttpMethod.Post.Method, path).ShouldNotBeNull(
                "the refusal must come from the route, not from an unmatched path");
        }

        (await AdminRevokeAllTestData.LiveCountAsync(host, member.UserId)).ShouldBe(1L);
    }
}
