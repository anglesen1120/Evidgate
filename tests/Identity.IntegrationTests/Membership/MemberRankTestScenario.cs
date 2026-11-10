using System.Text;
using Evidgate.Testing;
using Identity.IntegrationTests.Sessions.Http;

namespace Identity.IntegrationTests.Membership;

/// <summary>
/// The arrangements the member-rank target cases share: an active member with a custom role that carries
/// <c>membership:write</c> (rank one), the membership row behind a signed-in member, and a pending invite that
/// carries a chosen grant. Rows are written through the harness's owner seam; the mutations themselves always
/// travel the real routes.
/// </summary>
internal static class MemberRankTestScenario
{
    /// <summary>Signs a member in and gives it a custom role carrying <c>membership:write</c>.</summary>
    internal static async Task<SignedInMember> SignInCustomAdminAsync(
        SessionTestHost host,
        HttpClient client,
        Guid orgId) =>
        (await SignInCustomAdminWithRoleAsync(host, client, orgId)).Member;

    /// <summary>
    /// Signs a member in and gives it a fresh custom role carrying <c>membership:write</c>, returning both so a
    /// case can use the role as a target grant as well as the actor's rank.
    /// </summary>
    internal static async Task<(SignedInMember Member, Guid RoleId)> SignInCustomAdminWithRoleAsync(
        SessionTestHost host,
        HttpClient client,
        Guid orgId)
    {
        var signedIn = await host.SignInMemberInOrgAsync(client, orgId, roleCode: null);
        var roleId = await SeedCustomRoleAsync(host, orgId);
        await GrantToMembershipAsync(
            host,
            orgId,
            await MembershipIdAsync(host, orgId, signedIn.UserId),
            roleId);

        return (signedIn, roleId);
    }

    /// <summary>Seeds an org-local role whose only permission is <c>membership:write</c>.</summary>
    internal static async Task<Guid> SeedCustomRoleAsync(SessionTestHost host, Guid orgId)
    {
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

        return roleId;
    }

    /// <summary>Grants a role to a membership by its row id, unbound invites included.</summary>
    internal static Task GrantToMembershipAsync(
        SessionTestHost host,
        Guid orgId,
        Guid membershipId,
        Guid roleId) =>
        host.ArrangeAsync(
            "insert into public.membership_role (id, org_id, membership_id, role_id) "
            + "values (@id, @org, @membership, @role)",
            ("id", Guid.CreateVersion7()),
            ("org", orgId),
            ("membership", membershipId),
            ("role", roleId));

    /// <summary>Seeds a pending invite, with no user bound, that carries <paramref name="roleId"/> when given.</summary>
    internal static async Task<Guid> SeedPendingInviteAsync(
        SessionTestHost host,
        Guid orgId,
        Guid? roleId = null)
    {
        var membershipId = Guid.CreateVersion7();

        await host.ArrangeAsync(
            "insert into public.membership (id, org_id, invited_email, status) "
            + "values (@id, @org, @email, 'pending')",
            ("id", membershipId),
            ("org", orgId),
            ("email", $"pending-{membershipId:N}@example.test"));

        if (roleId is { } role)
        {
            await GrantToMembershipAsync(host, orgId, membershipId, role);
        }

        return membershipId;
    }

    /// <summary>The membership row of a user in the org.</summary>
    internal static async Task<Guid> MembershipIdAsync(SessionTestHost host, Guid orgId, Guid userId) =>
        (Guid)(await host.ScalarAsync(
            "select id from public.membership where org_id = @org_id and user_id = @user_id",
            [("org_id", orgId), ("user_id", userId)]))!;

    /// <summary>The row's status, read as the owner so a refused command cannot hide a write.</summary>
    internal static async Task<string> MembershipStatusAsync(SessionTestHost host, Guid membershipId) =>
        (string)(await host.ScalarAsync(
            "select status from public.membership where id = @id",
            [("id", membershipId)]))!;

    /// <summary>The row's current optimistic-concurrency version.</summary>
    internal static async Task<int> MembershipVersionAsync(SessionTestHost host, Guid membershipId) =>
        (int)(await host.ScalarAsync(
            "select version from public.membership where id = @id",
            [("id", membershipId)]))!;

    /// <summary>Whether the membership still holds the grant, read as the owner.</summary>
    internal static async Task<bool> HoldsRoleAsync(
        SessionTestHost host,
        Guid membershipId,
        Guid roleId) =>
        (long)(await host.ScalarAsync(
            "select count(*) from public.membership_role where membership_id = @membership and role_id = @role",
            [("membership", membershipId), ("role", roleId)]))! > 0;

    /// <summary>One mutation as <paramref name="caller"/>, with the org header, idempotency key and optional If-Match.</summary>
    internal static async Task<HttpResponseMessage> SendMutationAsync(
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

        return await client.SendAsync(request, SessionTestHost.Token);
    }

    /// <summary>The id of a built-in catalogue role, resolved through the shared identity fixture.</summary>
    internal static Task<Guid> BuiltInRoleIdAsync(DatabaseFixture fixture, Guid orgId, string code) =>
        IdentityTestData.RoleIdAsync(fixture, orgId, code, SessionTestHost.Token);
}
