using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// <c>POST /v1/auth/users/{u}/sessions/revoke-all</c> over the real host: one org admin ends every live session
/// of one member of that org in one call, audit row included.
/// </summary>
/// <remarks>
/// <para>
/// One row per call that revoked something, written in the revoke's transaction by the definer: the target as
/// its object, <c>target_user_id</c> and <c>revoked_count</c> as metadata strings, so the record says who ended
/// whose sessions and how many. A call that revoked nothing writes no row.
/// </para>
/// <para>
/// A faulted audit append aborts the whole call, so a revoked session always has a record. The refusals of this
/// route live in <see cref="AdminRevokeAllRefusalTests"/>.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AdminRevokeAllTests : TenantTest
{
    /// <summary>Own database: the rollback case installs a trigger on <c>public.audit_log</c>.</summary>
    public AdminRevokeAllTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>
    /// The administered case: an admin of the member's org ends BOTH live sessions of that member in one call —
    /// 204, no cookie of its own touched — while another member's session and the admin's own session stay live,
    /// the member's next request is 401 <c>session_revoked</c>, and exactly one audit row names the target and the
    /// count.
    /// </summary>
    [Fact]
    public async Task An_Admin_Ends_Every_Live_Session_Of_A_Member_And_One_Audit_Row_Names_The_Target()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var firstId = await host.SessionIdOfAsync(member.SessionCookie);

        // A second device of the same account: the call ends every live row, not only the one it could name.
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        var second = await host.CreateSessionAsync(member.UserId);
        var secondId = await host.SessionIdOfAsync(second.CookieValue);

        // The control rows the call must not touch: a colleague of the same org and the admin itself.
        var colleague = await host.SignInMemberInOrgAsync(client, member.OrgId, roleCode: null);
        var colleagueId = await host.SessionIdOfAsync(colleague.SessionCookie);
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");
        var adminId = await host.SessionIdOfAsync(admin.SessionCookie);

        using (var revoked = await AdminRevokeAllTestData.RevokeAllAsync(client, admin, member.UserId, member.OrgId))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            AssertNoCallerCookieTouched(revoked);
        }

        (await host.SessionRowAsync(firstId)).RevokedAt.ShouldNotBeNull("the first live row of the target is ended");
        (await host.SessionRowAsync(secondId)).RevokedAt.ShouldNotBeNull("and so is the second device");
        (await host.SessionRowAsync(colleagueId)).RevokedAt.ShouldBeNull("another member's session is not the target");
        (await host.SessionRowAsync(adminId)).RevokedAt.ShouldBeNull("the caller's own session is never the target");

        (await AdminRevokeAllTestData.LiveCountAsync(host, member.UserId)).ShouldBe(
            0L,
            "no live session of the target survives the call");

        await AssertTheOneAdminRowAsync(host, member.OrgId, admin.UserId, member.UserId, "2");

        // And the revocation is a row state: the member's very next request is refused on the dead cookie.
        using var next = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            AdminRevokeAllTestData.SingleSessionPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie));

        next.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(next)).ShouldBe(ApiErrorCode.SessionRevoked);
    }

    /// <summary>
    /// A target with no live session answers 204 and writes NO audit row: the call is idempotent and revoked
    /// nothing, so a row would assert a state change that never happened — the record must stay honest, which is
    /// why the definer inserts only when its update touched a row.
    /// </summary>
    [Fact]
    public async Task A_Target_With_Only_Dead_Sessions_Answers_204_And_Writes_No_Row()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var memberSessionId = await host.SessionIdOfAsync(member.SessionCookie);
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");

        // The member's only row is already dead, so the call has nothing to end.
        await host.RevokeSessionAsync(memberSessionId);

        using (var response = await AdminRevokeAllTestData.RevokeAllAsync(client, admin, member.UserId, member.OrgId))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, "an authorised call on a target with nothing live succeeds");
        }

        (await AdminRevokeAllTestData.LiveCountAsync(host, member.UserId)).ShouldBe(0L);
        (await AdminRevokeAllTestData.AdminRowCountAsync(host, member.OrgId)).ShouldBe(
            0L,
            "the call revoked nothing, so no row may claim it did");
    }

    /// <summary>
    /// The definer's own fault: a throwaway trigger on <c>audit_log</c> that refuses this action's insert makes the
    /// whole call abort — 503 <c>unavailable</c>, every session of the target STILL live, no row, no cookie
    /// touched — and the retry, with the fault gone, records the revoke exactly once.
    /// </summary>
    [Fact]
    public async Task A_Faulted_Audit_Rolls_Every_Revoke_Back_And_Answers_503()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var firstId = await host.SessionIdOfAsync(member.SessionCookie);
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        var second = await host.CreateSessionAsync(member.UserId);
        var secondId = await host.SessionIdOfAsync(second.CookieValue);
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");

        try
        {
            await AdminRevokeAllTestData.InstallAuditFaultAsync(host, member.OrgId);

            using var failed = await AdminRevokeAllTestData.RevokeAllAsync(client, admin, member.UserId, member.OrgId);

            failed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await SessionTestHost.ErrorCodeAsync(failed)).ShouldBe(ApiErrorCode.Unavailable);
            AssertNoCallerCookieTouched(failed);
        }
        finally
        {
            await AdminRevokeAllTestData.DropAuditFaultAsync(host);
        }

        (await host.SessionRowAsync(firstId)).RevokedAt.ShouldBeNull(
            "the revokes share the definer's transaction, so a failed append leaves every session live");
        (await host.SessionRowAsync(secondId)).RevokedAt.ShouldBeNull();
        (await AdminRevokeAllTestData.LiveCountAsync(host, member.UserId)).ShouldBe(2L);
        (await AdminRevokeAllTestData.AdminRowCountAsync(host, member.OrgId)).ShouldBe(
            0L,
            "the faulted call committed nothing");

        // The retry, with the fault gone, is an ordinary successful call: nothing was left half-done.
        using (var retried = await AdminRevokeAllTestData.RevokeAllAsync(client, admin, member.UserId, member.OrgId))
        {
            retried.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await AdminRevokeAllTestData.LiveCountAsync(host, member.UserId)).ShouldBe(0L);
        (await AdminRevokeAllTestData.AdminRowCountAsync(host, member.OrgId)).ShouldBe(1L, "the retry records the call once");
    }

    /// <summary>A 204 of this route never sets or expires a cookie of the caller: the admin stays signed in.</summary>
    /// <param name="response">The route's answer.</param>
    private static void AssertNoCallerCookieTouched(HttpResponseMessage response)
    {
        response.Headers.CacheControl.ShouldNotBeNull("a session answer is never reusable by a shared cache")
            .NoStore.ShouldBeTrue();

        foreach (var cookieName in AdminRevokeAllTestData.CallerCookies)
        {
            SessionTestHost.Cookies(response, cookieName).ShouldBeEmpty(
                cookieName + ": the admin's own credential is not the target");
        }
    }

    /// <summary>
    /// Exactly one row, describing the act: the admin as actor, the TARGET user as object, and metadata whose
    /// two keys are in canonical order with the count this call revoked.
    /// </summary>
    /// <param name="host">The host whose owner connection reads the chain.</param>
    /// <param name="orgId">The org whose chain holds the row.</param>
    /// <param name="actorId">The admin that made the call.</param>
    /// <param name="targetUserId">The member whose sessions ended.</param>
    /// <param name="revokedCount">The metadata count, as the string the row must carry.</param>
    private static async Task AssertTheOneAdminRowAsync(
        SessionTestHost host,
        Guid orgId,
        Guid actorId,
        Guid targetUserId,
        string revokedCount)
    {
        (await AdminRevokeAllTestData.AdminRowCountAsync(host, orgId)).ShouldBe(
            1L,
            "exactly one row per call, not one per session");

        using var row = JsonDocument.Parse(await AdminRevokeAllTestData.NewestAdminRowJsonAsync(host, orgId));
        var stored = row.RootElement;

        stored.GetProperty("actor_type").GetString().ShouldBe("user");
        stored.GetProperty("actor_id").GetString().ShouldBe(actorId.ToString("D"));
        stored.GetProperty("action").GetString().ShouldBe(AdminRevokeAllTestData.AdminRevokeAction);
        stored.GetProperty("object_type").GetString().ShouldBe("user");
        stored.GetProperty("object_id").GetString().ShouldBe(targetUserId.ToString("D"));

        var metadata = stored.GetProperty("metadata");
        string.Join(",", metadata.EnumerateObject().Select(property => property.Name)).ShouldBe(
            "revoked_count,target_user_id",
            "the canonical string sorts the two keys by UTF-16 code unit, which the hash depends on");
        metadata.GetProperty("revoked_count").GetString().ShouldBe(revokedCount, "both live rows were revoked");
        metadata.GetProperty("target_user_id").GetString().ShouldBe(targetUserId.ToString("D"));
    }
}
