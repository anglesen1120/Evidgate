using System.Net;
using System.Text.Json;
using Evidgate.Api.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Endpoints;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// AC4, AC5, AC6 and AC8 of 02.3.2-T2 over HTTP: <c>DELETE /v1/auth/sessions/{s}</c>,
/// <c>POST /v1/auth/sessions/revoke-all</c> and <c>POST /v1/auth/logout</c> — the real host, the real
/// authenticator and the real <c>evidgate_resolve</c> definers of migration 0048, over the harness database.
/// </summary>
/// <remarks>
/// <para>
/// <b>The refusals are the interesting half.</b> "Another user's session answers 404, never 403" is proven
/// against a REAL foreign row (an account that signed in), not a random id, and the answer is compared with the
/// random-id answer: same status, same code, same message. The administrative path is proven twice over — a
/// <c>tenant_admin</c> revoking a member's session succeeds, and the same caller naming an org it does not
/// administer (or a caller without <c>membership:write</c>) leaves the target row exactly as it was.
/// </para>
/// <para>
/// <b>AC5 is proven where it is enforced.</b> Each mutation is driven with a valid session pair whose header is
/// withheld, and the host-wide middleware must answer 403 <c>csrf_failed</c> before any handler runs — the rows
/// stay live, which is what "before any handler" means here.
/// </para>
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class RevocationTests : HarnessTest
{
    /// <summary>The single-session route of AC6.</summary>
    private const string SessionsPath = "/v1/auth/sessions";

    /// <summary>The revoke-all route.</summary>
    private const string RevokeAllPath = "/v1/auth/sessions/revoke-all";

    /// <summary>The logout route of AC8.</summary>
    private const string LogoutPath = "/v1/auth/logout";

    public RevocationTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// AC4: a session revoked by its owner is dead on the very NEXT request (401 <c>session_revoked</c>), the
    /// revocation itself answers 204, and a REPLAY of the same request is refused by the authenticator's 401
    /// <c>session_revoked</c> — the row is dead, so the caller is no longer authenticated.
    /// </summary>
    [Fact]
    public async Task Revoking_Own_Session_Takes_Effect_On_The_Next_Request()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var sessionId = await host.SessionIdOfAsync(member.SessionCookie);

        using (var revoked = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionsPath + "/" + sessionId,
            SessionTestHost.CookieHeaderFor(member.SessionCookie, member.Xsrf),
            xsrfToken: member.Xsrf.RequestToken))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent, "the caller may always revoke its own row");
        }

        (await host.SessionRowAsync(sessionId)).RevokedAt.ShouldNotBeNull();

        using var next = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie));

        next.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(next)).ShouldBe(ApiErrorCode.SessionRevoked);

        // Not a second 204: the row is already revoked, so the authenticator refuses the cookie it revoked BEFORE
        // this route's handler runs, and the replay answers 401 `session_revoked`. That refusal is the honest
        // answer — the caller is no longer authenticated — and it is why the write itself is idempotent without
        // the route having to special-case a dead row.
        using var replay = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionsPath + "/" + sessionId,
            SessionTestHost.CookieHeaderFor(member.SessionCookie, member.Xsrf),
            xsrfToken: member.Xsrf.RequestToken);

        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// AC8 (D9): after logout the row is revoked, the response expires the session cookie and the antiforgery
    /// pair, and replaying the OLD value answers 401 <c>session_revoked</c> — revocation is a row state, so
    /// nothing about the cookie itself had to change.
    /// </summary>
    [Fact]
    public async Task Logout_Revokes_The_Row_Expires_The_Cookies_And_A_Replay_Is_401()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var sessionId = await host.SessionIdOfAsync(member.SessionCookie);

        using var loggedOut = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Post,
            LogoutPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie, member.Xsrf),
            xsrfToken: member.Xsrf.RequestToken);

        loggedOut.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await SessionTestHost.BodyAsync(loggedOut)).ShouldBeNullOrWhiteSpace("a 204 carries no body");

        (await host.SessionRowAsync(sessionId)).RevokedAt.ShouldNotBeNull("logout is a row update, not a key rotation");

        foreach (var cookieName in new[]
        {
            SessionTestHost.SessionCookieName,
            SessionTestHost.XsrfCookieName,
            SessionTestHost.AntiforgeryCookieName,
        })
        {
            var expiries = SessionTestHost.Cookies(loggedOut, cookieName);
            expiries.ShouldHaveSingleItem($"{cookieName} must be expired exactly once by the sign-out response");

            var expiry = expiries[0];
            expiry.ShouldContain("max-age=0", Case.Insensitive, "the sign-out response drops the cookie");
            expiry.ShouldContain("path=/", Case.Insensitive, "a deletion must match the cookie's path");
            expiry.ShouldContain("secure", Case.Insensitive);
        }

        // AC8: the browser that ignores Set-Cookie is still refused on its next request.
        using var replay = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie));

        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(replay)).ShouldBe(ApiErrorCode.SessionRevoked);
    }

    /// <summary>
    /// AC6: another user's session and an id that never existed answer the SAME 404 — status, code and message —
    /// and neither row is touched. The foreign row is real (its account signed in), which is what makes the
    /// "no oracle" claim mean something.
    /// </summary>
    [Fact]
    public async Task Another_Users_Session_Answers_The_Same_404_As_An_Unknown_Id()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var caller = await host.SignInMemberAsync(client);
        var stranger = await host.SignInMemberAsync(client);
        var strangerSessionId = await host.SessionIdOfAsync(stranger.SessionCookie);

        using var foreign = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionsPath + "/" + strangerSessionId,
            SessionTestHost.CookieHeaderFor(caller.SessionCookie, caller.Xsrf),
            xsrfToken: caller.Xsrf.RequestToken);

        using var unknown = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionsPath + "/" + Guid.CreateVersion7(),
            SessionTestHost.CookieHeaderFor(caller.SessionCookie, caller.Xsrf),
            xsrfToken: caller.Xsrf.RequestToken);

        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a foreign id is not_found, never forbidden");
        unknown.StatusCode.ShouldBe(foreign.StatusCode);

        var foreignError = await ErrorAsync(foreign);
        var unknownError = await ErrorAsync(unknown);

        foreignError.Code.ShouldBe(ApiErrorCode.NotFound);
        unknownError.ShouldBe(foreignError, "a foreign id and an unknown id are indistinguishable");

        (await host.SessionRowAsync(strangerSessionId)).RevokedAt.ShouldBeNull("a refused revoke writes nothing");
        (await host.SessionRowAsync(await host.SessionIdOfAsync(caller.SessionCookie))).RevokedAt.ShouldBeNull();
    }

    /// <summary>
    /// AC6: a <c>tenant_admin</c> revoking a member's session in the org it administers succeeds — 204, the row
    /// is revoked, and the member's very next request is 401 <c>session_revoked</c> (member removal is
    /// immediate, 02.1.2 AC3).
    /// </summary>
    [Fact]
    public async Task An_Admin_With_Membership_Write_Revokes_A_Members_Session()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var memberSessionId = await host.SessionIdOfAsync(member.SessionCookie);
        var admin = await SignInMemberInOrgAsync(host, client, member.OrgId, "tenant_admin");

        using (var revoked = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionsPath + "/" + memberSessionId,
            SessionTestHost.CookieHeaderFor(admin.SessionCookie, admin.Xsrf),
            xsrfToken: admin.Xsrf.RequestToken,
            orgHeader: member.OrgId.ToString()))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await host.SessionRowAsync(memberSessionId)).RevokedAt.ShouldNotBeNull();

        using var next = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie));

        next.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(next)).ShouldBe(ApiErrorCode.SessionRevoked);
    }

    /// <summary>
    /// AC6 (refusal side, twice): a member WITHOUT <c>membership:write</c>, and an admin naming an org it does
    /// not administer, both answer 404 <c>not_found</c> and leave the target row live — the administrative path
    /// cannot be borrowed by naming an org or by holding a membership alone.
    /// </summary>
    [Fact]
    public async Task An_Admin_Without_The_Permission_Or_Outside_The_Org_Answers_404_And_Changes_Nothing()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var memberSessionId = await host.SessionIdOfAsync(member.SessionCookie);

        // A member of the SAME org with no role at all: a membership is not the permission.
        var plain = await SignInMemberInOrgAsync(host, client, member.OrgId, roleCode: null);

        using (var refused = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionsPath + "/" + memberSessionId,
            SessionTestHost.CookieHeaderFor(plain.SessionCookie, plain.Xsrf),
            xsrfToken: plain.Xsrf.RequestToken,
            orgHeader: member.OrgId.ToString()))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await ErrorAsync(refused)).Code.ShouldBe(ApiErrorCode.NotFound);
        }

        (await host.SessionRowAsync(memberSessionId)).RevokedAt.ShouldBeNull("the refused revoke wrote nothing");

        // An admin of that org naming ANOTHER org: the definer must find the actor's membership:write in the org
        // it was handed, and there is none.
        var otherOrg = await host.SeedOrganizationAsync();
        var admin = await SignInMemberInOrgAsync(host, client, member.OrgId, "tenant_admin");

        using (var refused = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionsPath + "/" + memberSessionId,
            SessionTestHost.CookieHeaderFor(admin.SessionCookie, admin.Xsrf),
            xsrfToken: admin.Xsrf.RequestToken,
            orgHeader: otherOrg.ToString()))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await ErrorAsync(refused)).Code.ShouldBe(ApiErrorCode.NotFound);
        }

        (await host.SessionRowAsync(memberSessionId)).RevokedAt.ShouldBeNull();

        // And without the header at all only the owner path applies: the admin's own id is what it may revoke.
        using (var refused = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionsPath + "/" + memberSessionId,
            SessionTestHost.CookieHeaderFor(admin.SessionCookie, admin.Xsrf),
            xsrfToken: admin.Xsrf.RequestToken))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        (await host.SessionRowAsync(memberSessionId)).RevokedAt.ShouldBeNull();
    }

    /// <summary>
    /// AC6: <c>revoke-all</c> keeps the current session by default (no body), kills every other live one, is
    /// idempotent, and leaves the caller signed in.
    /// </summary>
    [Fact]
    public async Task Revoke_All_Keeps_The_Current_Session_By_Default_And_Is_Idempotent()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var currentId = await host.SessionIdOfAsync(member.SessionCookie);

        var other = await host.CreateSessionAsync(member.UserId);
        var otherId = await host.SessionIdOfAsync(other.CookieValue);

        using (var revoked = await RevokeAllAsync(client, member, body: null))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await host.SessionRowAsync(otherId)).RevokedAt.ShouldNotBeNull("every other session is revoked");
        (await host.SessionRowAsync(currentId)).RevokedAt.ShouldBeNull("the current session is kept by default");

        // The caller is still signed in, and a second call is a no-op that answers 204.
        using (var list = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie)))
        {
            list.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var again = await RevokeAllAsync(client, member, body: null))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await host.SessionRowAsync(currentId)).RevokedAt.ShouldBeNull();
    }

    /// <summary>
    /// AC4/AC6: <c>revoke-all</c> with <c>keep_current: false</c> kills the caller's own row too, expires the
    /// cookies like logout does, and the old value is refused on the next request.
    /// </summary>
    [Fact]
    public async Task Revoke_All_With_Keep_Current_False_Kills_The_Current_Session()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var currentId = await host.SessionIdOfAsync(member.SessionCookie);

        using (var revoked = await RevokeAllAsync(client, member, body: "{\"keep_current\": false}"))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);

            var expiries = SessionTestHost.Cookies(revoked, SessionTestHost.SessionCookieName);
            expiries.ShouldHaveSingleItem();
            expiries[0].ShouldContain("max-age=0", Case.Insensitive);
        }

        (await host.SessionRowAsync(currentId)).RevokedAt.ShouldNotBeNull();

        using var replay = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie));

        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(replay)).ShouldBe(ApiErrorCode.SessionRevoked);
    }

    /// <summary>
    /// AC5: each of the three mutations, carrying a valid session and its antiforgery COOKIE but no header, is
    /// refused 403 <c>csrf_failed</c> by the host-wide gate — and no row is revoked, which is what proves no
    /// handler ran.
    /// </summary>
    [Fact]
    public async Task Every_Session_Mutation_Without_The_Pair_Is_403_CsrfFailed()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var currentId = await host.SessionIdOfAsync(member.SessionCookie);

        var probes = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Delete, SessionsPath + "/" + currentId),
            (HttpMethod.Post, RevokeAllPath),
            (HttpMethod.Post, LogoutPath),
        };

        foreach (var (method, path) in probes)
        {
            using var response = await SessionTestHost.SendAsync(
                client,
                method,
                path,
                SessionTestHost.CookieHeaderFor(member.SessionCookie, member.Xsrf));

            response.StatusCode.ShouldBe(
                HttpStatusCode.Forbidden,
                method + " " + path + " must require the X-XSRF-TOKEN header");
            (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.CsrfFailed);
        }

        (await host.SessionRowAsync(currentId)).RevokedAt.ShouldBeNull("the gate runs before any handler");
    }

    /// <summary>
    /// Every session route needs a caller: without a cookie each answers 401 <c>unauthenticated</c> (the GET
    /// through its handler, the three mutations through the same authenticator), so none of them can be driven
    /// anonymously.
    /// </summary>
    [Fact]
    public async Task Every_Session_Route_Without_A_Session_Is_401_Unauthenticated()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        host.Recorder.Clear();

        var probes = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, SessionsPath),
            (HttpMethod.Delete, SessionsPath + "/" + Guid.CreateVersion7()),
            (HttpMethod.Post, RevokeAllPath),
            (HttpMethod.Post, LogoutPath),
        };

        foreach (var (method, path) in probes)
        {
            using var response = await SessionTestHost.SendAsync(client, method, path);

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, method + " " + path + " needs a session");
            (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.Unauthenticated);

            host.Recorder.PatternFor(method.Method, path).ShouldNotBeNull(
                "the refusal must come from the route, not from an unmatched path");
        }
    }

    /// <summary>
    /// The module cannot reference the host, so the admin header's name is declared twice; this pins the two
    /// together, because a rename on one side would silently stop the administrative path from being asked for.
    /// </summary>
    [Fact]
    public void The_Admin_Org_Header_Matches_The_Host_Constant()
    {
        SessionEndpoints.OrgHeader.ShouldBe(TenantResolver.OrgHeader);
    }

    /// <summary>One <c>revoke-all</c> as <paramref name="member"/>'s browser sends it, with the body it names.</summary>
    /// <param name="client">A client of the host.</param>
    /// <param name="member">The signed-in account.</param>
    /// <param name="body">The optional JSON body, or null for none (the documented default).</param>
    private static Task<HttpResponseMessage> RevokeAllAsync(HttpClient client, SignedInMember member, string? body) =>
        SessionTestHost.SendAsync(
            client,
            HttpMethod.Post,
            RevokeAllPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie, member.Xsrf),
            xsrfToken: member.Xsrf.RequestToken,
            jsonBody: body);

    /// <summary>
    /// Seeds one more account of an EXISTING org, optionally grants it a built-in role, and signs it in: the
    /// arrangement the administrative cases need, because the victim and the admin must share an org while each
    /// signs in through the real login routes.
    /// </summary>
    /// <param name="host">The host.</param>
    /// <param name="client">A client of the host.</param>
    /// <param name="orgId">The org both accounts belong to.</param>
    /// <param name="roleCode">The built-in role to hold, or null for a membership with no role.</param>
    private async Task<SignedInMember> SignInMemberInOrgAsync(
        SessionTestHost host,
        HttpClient client,
        Guid orgId,
        string? roleCode)
    {
        var subject = "subject-" + Guid.CreateVersion7().ToString("N");
        var email = subject + "@example.test";

        var userId = await host.SeedUserAsync(email);

        await host.ArrangeAsync(
            """
            insert into public.user_identity (id, user_id, provider, subject, email_at_provider)
            values (@id, @user_id, @provider, @subject, @email)
            """,
            ("id", Guid.CreateVersion7()),
            ("user_id", userId),
            ("provider", SessionTestHost.Google),
            ("subject", subject),
            ("email", email));

        await host.SeedMembershipAsync(orgId, userId);

        if (roleCode is not null)
        {
            // The same two helpers every other identity suite uses (IdentityTestData): the built-in role of the
            // catalogue and the grant row, so this suite never hand-writes the shapes the commands produce.
            var roleId = await IdentityTestData.RoleIdAsync(Fixture, orgId, roleCode);
            await IdentityTestData.GrantRoleToMemberAsync(Fixture, orgId, userId, roleId);
        }

        host.VerifyIdentity(SessionTestHost.Google, subject, email);

        using var response = await host.SignInAsync(client, SessionTestHost.Google);
        response.StatusCode.ShouldBe(HttpStatusCode.Found, "the account's sign-in completes with the 302");

        var cookie = SessionTestHost.SessionCookieValue(response)
            ?? throw new InvalidOperationException("the sign-in set no session cookie");

        return new SignedInMember(orgId, userId, email, cookie, XsrfPair.From(response));
    }

    /// <summary>The 00 §9.2 <c>error.code</c> and <c>error.message</c> of a response.</summary>
    /// <param name="response">The response.</param>
    private static async Task<(string Code, string Message)> ErrorAsync(HttpResponseMessage response)
    {
        var body = await SessionTestHost.BodyAsync(response);
        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("error");

        return (error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!);
    }
}
