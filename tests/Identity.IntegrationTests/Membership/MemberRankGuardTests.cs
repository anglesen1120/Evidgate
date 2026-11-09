using System.Net;
using System.Text;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Evidgate.Testing;
using Identity.IntegrationTests.Sessions.Http;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Membership;

/// <summary>
/// The administrative hierarchy of the three member mutations: a caller may remove, disable or
/// demote a target only when the target's rank in the acting org is not above the caller's.
/// A custom administrator holding <c>membership:write</c> is rank one; the built-in
/// <c>tenant_admin</c> is rank two.
/// </summary>
/// <remarks>
/// Every case signs in through the real routes, so the permission pipeline and the rotating
/// cookies are the deployed ones. Refusals leave the membership, the target's sessions and the
/// org's audit chain untouched; an allowed equal or lower target changes them.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class MemberRankGuardTests : TenantTest
{
    public MemberRankGuardTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>A custom administrator may not remove an owner, even when another owner remains.</summary>
    [Fact]
    public async Task Custom_Admin_Cannot_Remove_An_Owner()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var founder = await host.SignInMemberAsync(client);
        var owner = await host.SignInMemberInOrgAsync(client, founder.OrgId, "tenant_admin");
        _ = await host.SignInMemberInOrgAsync(client, founder.OrgId, "tenant_admin");
        var custom = await SignInCustomAdminAsync(host, client, founder.OrgId);
        var membershipId = await MembershipIdAsync(host, founder.OrgId, owner.UserId);
        var sessionId = await host.SessionIdOfAsync(owner.SessionCookie);
        var auditBefore = await AuditCountAsync(host, founder.OrgId, "membership.remove");

        using var response = await SendMutationAsync(
            client,
            HttpMethod.Delete,
            $"/v1/orgs/{founder.OrgId}/memberships/{membershipId}",
            custom,
            founder.OrgId,
            "rank-remove-denied",
            jsonBody: null,
            ifMatch: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.Forbidden);
        (await MembershipStatusAsync(host, membershipId)).ShouldBe("active");
        (await host.SessionRowAsync(sessionId)).RevokedAt.ShouldBeNull();
        (await AuditCountAsync(host, founder.OrgId, "membership.remove")).ShouldBe(auditBefore);
    }

    /// <summary>The disable route applies the same rank check, before the version branch.</summary>
    [Fact]
    public async Task Custom_Admin_Cannot_Disable_An_Owner()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var founder = await host.SignInMemberAsync(client);
        var owner = await host.SignInMemberInOrgAsync(client, founder.OrgId, "tenant_admin");
        _ = await host.SignInMemberInOrgAsync(client, founder.OrgId, "tenant_admin");
        var custom = await SignInCustomAdminAsync(host, client, founder.OrgId);
        var membershipId = await MembershipIdAsync(host, founder.OrgId, owner.UserId);
        var version = (int)(await host.ScalarAsync(
            "select version from public.membership where id = @id",
            [("id", membershipId)]))!;
        var sessionId = await host.SessionIdOfAsync(owner.SessionCookie);
        var auditBefore = await AuditCountAsync(host, founder.OrgId, "membership.update");

        using var response = await SendMutationAsync(
            client,
            HttpMethod.Patch,
            $"/v1/orgs/{founder.OrgId}/memberships/{membershipId}",
            custom,
            founder.OrgId,
            "rank-disable-denied",
            """{"status":"removed"}""",
            version.ToString(System.Globalization.CultureInfo.InvariantCulture));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.Forbidden);
        (await MembershipStatusAsync(host, membershipId)).ShouldBe("active");
        (await host.SessionRowAsync(sessionId)).RevokedAt.ShouldBeNull();
        (await AuditCountAsync(host, founder.OrgId, "membership.update")).ShouldBe(auditBefore);
    }

    /// <summary>A custom administrator may not revoke an owner's <c>tenant_admin</c> grant.</summary>
    [Fact]
    public async Task Custom_Admin_Cannot_Revoke_An_Owners_Admin_Role()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var founder = await host.SignInMemberAsync(client);
        var owner = await host.SignInMemberInOrgAsync(client, founder.OrgId, "tenant_admin");
        _ = await host.SignInMemberInOrgAsync(client, founder.OrgId, "tenant_admin");
        var custom = await SignInCustomAdminAsync(host, client, founder.OrgId);
        var membershipId = await MembershipIdAsync(host, founder.OrgId, owner.UserId);
        var adminRoleId = await IdentityTestData.RoleIdAsync(Fixture, founder.OrgId, "tenant_admin", Token);
        var sessionId = await host.SessionIdOfAsync(owner.SessionCookie);
        var auditBefore = await AuditCountAsync(host, founder.OrgId, "membership.role_revoke");

        using var response = await SendMutationAsync(
            client,
            HttpMethod.Delete,
            $"/v1/orgs/{founder.OrgId}/memberships/{membershipId}/roles/{adminRoleId}",
            custom,
            founder.OrgId,
            "rank-revoke-denied",
            jsonBody: null,
            ifMatch: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.Forbidden);
        (await HoldsRoleAsync(host, membershipId, adminRoleId)).ShouldBeTrue();
        (await host.SessionRowAsync(sessionId)).RotationRequiredAt.ShouldBeNull();
        (await AuditCountAsync(host, founder.OrgId, "membership.role_revoke")).ShouldBe(auditBefore);
    }

    /// <summary>Equal rank is allowed: one custom administrator removes another, and a plain member.</summary>
    [Fact]
    public async Task Custom_Admin_Removes_An_Equal_And_A_Lower_Rank_Target()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var founder = await host.SignInMemberAsync(client);
        var actor = await SignInCustomAdminAsync(host, client, founder.OrgId);
        var peer = await SignInCustomAdminAsync(host, client, founder.OrgId);
        var plain = await host.SignInMemberInOrgAsync(client, founder.OrgId, roleCode: null);
        var peerMembership = await MembershipIdAsync(host, founder.OrgId, peer.UserId);
        var plainMembership = await MembershipIdAsync(host, founder.OrgId, plain.UserId);

        using var equal = await SendMutationAsync(
            client,
            HttpMethod.Delete,
            $"/v1/orgs/{founder.OrgId}/memberships/{peerMembership}",
            actor,
            founder.OrgId,
            "rank-remove-equal",
            jsonBody: null,
            ifMatch: null);

        equal.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await MembershipStatusAsync(host, peerMembership)).ShouldBe("removed");
        (await host.SessionRowAsync(await host.SessionIdOfAsync(peer.SessionCookie))).RevokedAt.ShouldNotBeNull();

        using var lower = await SendMutationAsync(
            client,
            HttpMethod.Delete,
            $"/v1/orgs/{founder.OrgId}/memberships/{plainMembership}",
            actor,
            founder.OrgId,
            "rank-remove-lower",
            jsonBody: null,
            ifMatch: null);

        lower.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await MembershipStatusAsync(host, plainMembership)).ShouldBe("removed");
    }

    /// <summary>A higher rank is allowed: an owner removes a custom administrator.</summary>
    [Fact]
    public async Task Owner_Removes_A_Custom_Admin()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var founder = await host.SignInMemberAsync(client);
        var owner = await host.SignInMemberInOrgAsync(client, founder.OrgId, "tenant_admin");
        var custom = await SignInCustomAdminAsync(host, client, founder.OrgId);
        var membershipId = await MembershipIdAsync(host, founder.OrgId, custom.UserId);

        using var response = await SendMutationAsync(
            client,
            HttpMethod.Delete,
            $"/v1/orgs/{founder.OrgId}/memberships/{membershipId}",
            owner,
            founder.OrgId,
            "rank-remove-higher",
            jsonBody: null,
            ifMatch: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await MembershipStatusAsync(host, membershipId)).ShouldBe("removed");
    }

    /// <summary>An owner may disable and demote a custom administrator.</summary>
    [Fact]
    public async Task Owner_Disables_And_Demotes_A_Custom_Admin()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var founder = await host.SignInMemberAsync(client);
        var owner = await host.SignInMemberInOrgAsync(client, founder.OrgId, "tenant_admin");
        var first = await SignInCustomAdminAsync(host, client, founder.OrgId);
        var second = await SignInCustomAdminWithRoleAsync(host, client, founder.OrgId);
        var firstMembership = await MembershipIdAsync(host, founder.OrgId, first.UserId);
        var secondMembership = await MembershipIdAsync(host, founder.OrgId, second.Member.UserId);
        var version = (int)(await host.ScalarAsync(
            "select version from public.membership where id = @id",
            [("id", firstMembership)]))!;

        using var disabled = await SendMutationAsync(
            client,
            HttpMethod.Patch,
            $"/v1/orgs/{founder.OrgId}/memberships/{firstMembership}",
            owner,
            founder.OrgId,
            "rank-disable-higher",
            """{"status":"removed"}""",
            version.ToString(System.Globalization.CultureInfo.InvariantCulture));

        disabled.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await MembershipStatusAsync(host, firstMembership)).ShouldBe("removed");

        using var demoted = await SendMutationAsync(
            client,
            HttpMethod.Delete,
            $"/v1/orgs/{founder.OrgId}/memberships/{secondMembership}/roles/{second.RoleId}",
            owner,
            founder.OrgId,
            "rank-revoke-higher",
            jsonBody: null,
            ifMatch: null);

        demoted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await HoldsRoleAsync(host, secondMembership, second.RoleId)).ShouldBeFalse();
    }

    /// <summary>Equal rank is allowed on the disable route: one custom administrator disables another.</summary>
    [Fact]
    public async Task Custom_Admin_Disables_An_Equal_Rank_Target()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var founder = await host.SignInMemberAsync(client);
        var actor = await SignInCustomAdminAsync(host, client, founder.OrgId);
        var peer = await SignInCustomAdminAsync(host, client, founder.OrgId);
        var peerMembership = await MembershipIdAsync(host, founder.OrgId, peer.UserId);
        var version = (int)(await host.ScalarAsync(
            "select version from public.membership where id = @id",
            [("id", peerMembership)]))!;

        using var response = await SendMutationAsync(
            client,
            HttpMethod.Patch,
            $"/v1/orgs/{founder.OrgId}/memberships/{peerMembership}",
            actor,
            founder.OrgId,
            "rank-disable-equal",
            """{"status":"removed"}""",
            version.ToString(System.Globalization.CultureInfo.InvariantCulture));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await MembershipStatusAsync(host, peerMembership)).ShouldBe("removed");
        (await host.SessionRowAsync(await host.SessionIdOfAsync(peer.SessionCookie))).RevokedAt.ShouldNotBeNull();
    }

    /// <summary>Equal rank is allowed on the revoke route: one custom administrator demotes another.</summary>
    [Fact]
    public async Task Custom_Admin_Revokes_An_Equal_Rank_Targets_Role()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var founder = await host.SignInMemberAsync(client);
        var actor = await SignInCustomAdminAsync(host, client, founder.OrgId);
        var peer = await SignInCustomAdminWithRoleAsync(host, client, founder.OrgId);
        var peerMembership = await MembershipIdAsync(host, founder.OrgId, peer.Member.UserId);

        using var response = await SendMutationAsync(
            client,
            HttpMethod.Delete,
            $"/v1/orgs/{founder.OrgId}/memberships/{peerMembership}/roles/{peer.RoleId}",
            actor,
            founder.OrgId,
            "rank-revoke-equal",
            jsonBody: null,
            ifMatch: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await HoldsRoleAsync(host, peerMembership, peer.RoleId)).ShouldBeFalse();
        (await host.SessionRowAsync(await host.SessionIdOfAsync(peer.Member.SessionCookie)))
            .RotationRequiredAt.ShouldNotBeNull();
    }

    /// <summary>An unbound invite ranks zero, so the guard passes it to the route's state refusal.</summary>
    [Fact]
    public async Task Pending_Invite_Is_Not_A_Rank_Refusal()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var founder = await host.SignInMemberAsync(client);
        var custom = await SignInCustomAdminAsync(host, client, founder.OrgId);
        var inviteId = Guid.CreateVersion7();
        await host.ArrangeAsync(
            "insert into public.membership (id, org_id, invited_email, status) values (@id, @org, @email, 'pending')",
            ("id", inviteId),
            ("org", founder.OrgId),
            ("email", $"pending-{inviteId:N}@example.test"));

        using var response = await SendMutationAsync(
            client,
            HttpMethod.Delete,
            $"/v1/orgs/{founder.OrgId}/memberships/{inviteId}",
            custom,
            founder.OrgId,
            "rank-remove-pending",
            jsonBody: null,
            ifMatch: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.InvalidTransition);
    }

    /// <summary>A request without a resolved principal holds no rank and never reaches the statement.</summary>
    [Fact]
    public async Task Missing_Principal_Is_Refused_Without_A_Statement()
    {
        var db = new RecordingTenantDb(result: true);

        var allowed = await MemberRankGuard.ActorOutranksOrEqualsAsync(
            db,
            Guid.CreateVersion7(),
            actor: null,
            Guid.CreateVersion7(),
            Token);

        allowed.ShouldBeFalse();
        db.Statements.ShouldBe(0);
    }

    /// <summary>The comparison is one SQL statement carrying the org, actor and target.</summary>
    [Fact]
    public async Task Rank_Comparison_Is_One_Parameterised_Statement()
    {
        var db = new RecordingTenantDb(result: true);
        var orgId = Guid.CreateVersion7();
        var actorId = Guid.CreateVersion7();
        var targetId = Guid.CreateVersion7();

        var allowed = await MemberRankGuard.ActorOutranksOrEqualsAsync(
            db,
            orgId,
            actorId,
            targetId,
            Token);

        allowed.ShouldBeTrue();
        db.Statements.ShouldBe(1);
        var sql = db.Sql.ShouldBeOfType<string>();
        sql.ShouldContain("evidgate.session_admin_rank(@org_id, @actor)");
        sql.ShouldContain("evidgate.session_admin_rank(@org_id, @target)");
        Parameter(db, "org_id").ShouldBe(orgId);
        Parameter(db, "actor").ShouldBe(actorId);
        Parameter(db, "target").ShouldBe(targetId);
    }

    /// <summary>A pending target is passed as SQL NULL, which the function ranks zero.</summary>
    [Fact]
    public async Task Null_Target_Is_Passed_To_The_Function()
    {
        var db = new RecordingTenantDb(result: true);

        var allowed = await MemberRankGuard.ActorOutranksOrEqualsAsync(
            db,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            targetUserId: null,
            Token);

        allowed.ShouldBeTrue();
        Parameter(db, "target").ShouldBeNull();
    }

    private static object? Parameter(RecordingTenantDb db, string name) =>
        db.Parameters?.GetType().GetProperty(name)?.GetValue(db.Parameters);

    private static async Task<SignedInMember> SignInCustomAdminAsync(
        SessionTestHost host,
        HttpClient client,
        Guid orgId)
    {
        return (await SignInCustomAdminWithRoleAsync(host, client, orgId)).Member;
    }

    private static async Task<(SignedInMember Member, Guid RoleId)> SignInCustomAdminWithRoleAsync(
        SessionTestHost host,
        HttpClient client,
        Guid orgId)
    {
        var signedIn = await host.SignInMemberInOrgAsync(client, orgId, roleCode: null);
        var roleId = Guid.CreateVersion7();

        await host.ArrangeAsync(
            "insert into public.role (id, org_id, code, name, is_builtin, kind) "
            + "values (@id, @org, @code, 'Member administrator', false, 'member')",
            ("id", roleId),
            ("org", orgId),
            ("code", "member_admin_" + roleId.ToString("N")));
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

        return (signedIn, roleId);
    }

    private static async Task<Guid> MembershipIdAsync(SessionTestHost host, Guid orgId, Guid userId) =>
        (Guid)(await host.ScalarAsync(
            "select id from public.membership where org_id = @org_id and user_id = @user_id",
            [("org_id", orgId), ("user_id", userId)]))!;

    private static async Task<string> MembershipStatusAsync(SessionTestHost host, Guid membershipId) =>
        (string)(await host.ScalarAsync(
            "select status from public.membership where id = @id",
            [("id", membershipId)]))!;

    private static async Task<bool> HoldsRoleAsync(SessionTestHost host, Guid membershipId, Guid roleId) =>
        (long)(await host.ScalarAsync(
            "select count(*) from public.membership_role where membership_id = @membership and role_id = @role",
            [("membership", membershipId), ("role", roleId)]))! > 0;

    private static async Task<long> AuditCountAsync(SessionTestHost host, Guid orgId, string action) =>
        (long)(await host.ScalarAsync(
            "select count(*) from public.audit_log where org_id = @org and action = @action",
            [("org", orgId), ("action", action)]))!;

    private static async Task<HttpResponseMessage> SendMutationAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        SignedInMember caller,
        Guid orgId,
        string idempotencyKey,
        string? jsonBody,
        string? ifMatch)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", SessionTestHost.CookieHeaderFor(caller.SessionCookie, caller.Xsrf));
        request.Headers.Add(SessionTestHost.XsrfHeaderName, caller.Xsrf.RequestToken);
        request.Headers.Add("X-Evidgate-Org", orgId.ToString("D"));
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        }

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
