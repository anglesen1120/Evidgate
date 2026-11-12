using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// The two administrative revoke routes and the reads their cases assert with: the one request each case sends,
/// the pinned audit action, the custom administrator both routes are refused or allowed by rank for, and the
/// owner-seam counts of the rows a call may change.
/// </summary>
/// <remarks>
/// The writes go over the real host as the app role; every row is then read as the owner, because
/// <c>evidgate_app</c> holds no privilege on <c>public.session</c>.
/// </remarks>
internal static class AdminRevokeAllTestData
{
    /// <summary>The addressed-user half of the route; the id follows it.</summary>
    internal const string Prefix = "/v1/auth/users/";

    /// <summary>The route's tail.</summary>
    internal const string Suffix = "/sessions/revoke-all";

    /// <summary>The single-session route, whose 404 this route must reproduce exactly.</summary>
    internal const string SingleSessionPath = "/v1/auth/sessions";

    /// <summary>The audited action the definer writes; it lives in SQL, so the literal is pinned here.</summary>
    internal const string AdminRevokeAction = "auth.session_revoke_admin";

    /// <summary>
    /// The three cookies a 204 of this route must never set or expire: the caller stays signed in.
    /// </summary>
    internal static readonly string[] CallerCookies =
    [
        SessionTestHost.SessionCookieName,
        SessionTestHost.XsrfCookieName,
        SessionTestHost.AntiforgeryCookieName,
    ];

    /// <summary>The path of one addressed user.</summary>
    /// <param name="userId">The user id the route addresses.</param>
    internal static string PathFor(Guid userId) => Prefix + userId.ToString("D") + Suffix;

    /// <summary>The path of one session row.</summary>
    /// <param name="sessionId">The row the single-session route addresses.</param>
    internal static string SessionPath(Guid sessionId) => SingleSessionPath + "/" + sessionId.ToString("D");

    /// <summary>One administrative single-session revoke as <paramref name="caller"/>'s browser sends it.</summary>
    /// <param name="client">A client of the host.</param>
    /// <param name="caller">The signed-in account.</param>
    /// <param name="sessionId">The row addressed.</param>
    /// <param name="orgId">The <c>X-Evidgate-Org</c> value, or null for none.</param>
    internal static Task<HttpResponseMessage> RevokeOneAsync(
        HttpClient client,
        SignedInMember caller,
        Guid sessionId,
        Guid? orgId) =>
        SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionPath(sessionId),
            SessionTestHost.CookieHeaderFor(caller.SessionCookie, caller.Xsrf),
            xsrfToken: caller.Xsrf.RequestToken,
            orgHeader: orgId?.ToString("D"));

    /// <summary>
    /// A member of <paramref name="orgId"/> holding a CUSTOM role that carries <c>membership:write</c> — rank 1 of
    /// the revoke hierarchy (0091/0096), as opposed to the built-in <c>tenant_admin</c> at rank 2.
    /// </summary>
    /// <param name="host">The host whose owner seam writes the role rows.</param>
    /// <param name="client">A client of the host.</param>
    /// <param name="orgId">The org the role and the membership belong to.</param>
    /// <param name="roleCode">The role's code, or a unique one when null.</param>
    internal static async Task<SignedInMember> SignInCustomAdminAsync(
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

    /// <summary>One administrative revoke-all as <paramref name="caller"/>'s browser sends it.</summary>
    /// <param name="client">A client of the host.</param>
    /// <param name="caller">The signed-in account.</param>
    /// <param name="targetUserId">The user id the route addresses.</param>
    /// <param name="orgId">The <c>X-Evidgate-Org</c> value, or null for none.</param>
    internal static Task<HttpResponseMessage> RevokeAllAsync(
        HttpClient client,
        SignedInMember caller,
        Guid targetUserId,
        Guid? orgId) =>
        SessionTestHost.SendAsync(
            client,
            HttpMethod.Post,
            PathFor(targetUserId),
            SessionTestHost.CookieHeaderFor(caller.SessionCookie, caller.Xsrf),
            xsrfToken: caller.Xsrf.RequestToken,
            orgHeader: orgId?.ToString("D"));

    /// <summary>The response body exactly as it crossed the wire: no JSON re-serialisation.</summary>
    /// <param name="response">The response.</param>
    internal static Task<byte[]> RawBodyAsync(HttpResponseMessage response) =>
        response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

    /// <summary>How many live rows the account holds, read as the owner.</summary>
    /// <param name="host">The host whose owner connection reads the row.</param>
    /// <param name="userId">The account whose live sessions are counted.</param>
    internal static Task<long> LiveCountAsync(SessionTestHost host, Guid userId) =>
        host.CountAsync(
            "select count(*) from public.session where user_id = @user and revoked_at is null",
            ("user", userId));

    /// <summary>How many administrative-revoke rows the org's chain holds.</summary>
    /// <param name="host">The host whose owner connection reads the chain.</param>
    /// <param name="orgId">The org whose chain is counted.</param>
    internal static Task<long> AdminRowCountAsync(SessionTestHost host, Guid orgId) =>
        host.CountAsync(
            "select count(*) from public.audit_log where org_id = @org and action = @action",
            ("org", orgId),
            ("action", AdminRevokeAction));

    /// <summary>The newest administrative-revoke row of the org, as stored JSON text.</summary>
    /// <param name="host">The host whose owner connection reads the chain.</param>
    /// <param name="orgId">The org whose chain is read.</param>
    internal static async Task<string> NewestAdminRowJsonAsync(SessionTestHost host, Guid orgId)
    {
        var payload = (await host.ScalarAsync(
            "select to_jsonb(a)::text from public.audit_log a "
            + "where a.org_id = @org and a.action = @action order by a.seq desc limit 1",
            [("org", orgId), ("action", AdminRevokeAction)])).ShouldNotBeNull(
            "the call must have appended its one audit row");

        return (string)payload;
    }

    /// <summary>
    /// Installs the throwaway fault: a trigger on <c>audit_log</c> that refuses this action's insert for this
    /// org only, so no other write of the harness is disturbed and the failure is the definer's own append.
    /// </summary>
    /// <param name="host">The host whose owner seam arranges the trigger.</param>
    /// <param name="orgId">The org whose row must be refused.</param>
    internal static async Task InstallAuditFaultAsync(SessionTestHost host, Guid orgId)
    {
        await host.ArrangeAsync(
            """
            create or replace function public.admin_revoke_all_audit_fault() returns trigger
              language plpgsql
              set search_path = ''
              as $$
              begin
                raise exception 'admin revoke-all audit fault' using errcode = '58000';
              end
              $$;
            """);

        // A utility statement takes no bind parameters, so the org travels as a literal uuid (a Guid's "D" text
        // carries no SQL) and the action is the pinned constant.
        await host.ArrangeAsync(
            "create trigger admin_revoke_all_audit_fault before insert on public.audit_log "
            + "for each row when (new.action = '" + AdminRevokeAction + "' and new.org_id = '"
            + orgId.ToString("D") + "'::uuid) execute function public.admin_revoke_all_audit_fault()");
    }

    /// <summary>Drops the throwaway trigger and its function, whatever the test's outcome was.</summary>
    /// <param name="host">The host whose owner seam removes them.</param>
    internal static async Task DropAuditFaultAsync(SessionTestHost host)
    {
        await host.ArrangeAsync("drop trigger if exists admin_revoke_all_audit_fault on public.audit_log");
        await host.ArrangeAsync("drop function if exists public.admin_revoke_all_audit_fault()");
    }
}
