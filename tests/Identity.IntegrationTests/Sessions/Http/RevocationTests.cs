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
    /// Resets the harness database, then restores the migration-seeded permission catalogue.
    /// </summary>
    /// <remarks>
    /// The administrative half of AC6 signs a <c>tenant_admin</c> in and revokes a member's session, so the
    /// built-in roles and their grant rows — which Respawn's blanket truncate removes with every other table —
    /// must be back before the test body runs. <see cref="CatalogueSeed"/> is the same step the store suites and
    /// <see cref="CatalogueTestBase"/> take.
    /// </remarks>
    public override async ValueTask InitializeAsync()
    {
        await Fixture.ResetAsync(Token);
        await CatalogueSeed.ApplyAsync(Fixture, Token);
    }

    /// <summary>
    /// AC4/AC6 (Sec F5): revoking the caller's OWN session is a sign-out, so the 204 also expires the session
    /// cookie and the antiforgery pair exactly as <c>logout</c> does; the next request (or a replay of the dead
    /// value) answers 401 <c>session_revoked</c>, the row — not the cookie — being what changed.
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

            // The request just ended its own credential: the browser is told to drop the session cookie AND the
            // pair, the same shape `logout` answers (Sec F5).
            AssertCookiesExpired(revoked);
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

        AssertCookiesExpired(loggedOut);

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
    /// AC6 (Code F2): a segment that is not a uuid answers the SAME 404 <c>not_found</c> a real-but-unknown id
    /// does, and only after authentication — the id is parsed by the handler, so a malformed value never falls out
    /// as the undocumented 400 parameter binding would raise before the credential was judged.
    /// </summary>
    /// <remarks>
    /// The literal <c>revoke-all</c> is the interesting malformed value: the router matches it against
    /// <c>/auth/sessions/{s}</c> for <c>DELETE</c> (only <c>POST</c> has its own template), so it reaches this
    /// handler as an id and must be answered as one.
    /// </remarks>
    [Fact]
    public async Task A_Malformed_Session_Id_Answers_The_Same_404_A_Real_Id_Does()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var caller = await host.SignInMemberAsync(client);
        var callerId = await host.SessionIdOfAsync(caller.SessionCookie);
        host.Recorder.Clear();

        using var malformed = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionsPath + "/not-a-uuid",
            SessionTestHost.CookieHeaderFor(caller.SessionCookie, caller.Xsrf),
            xsrfToken: caller.Xsrf.RequestToken);

        using var unknown = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionsPath + "/" + Guid.CreateVersion7(),
            SessionTestHost.CookieHeaderFor(caller.SessionCookie, caller.Xsrf),
            xsrfToken: caller.Xsrf.RequestToken);

        malformed.StatusCode.ShouldBe(
            HttpStatusCode.NotFound,
            "a malformed id is the route's one 404, never a binding 400 no response declares");
        malformed.StatusCode.ShouldBe(unknown.StatusCode);

        var malformedError = await ErrorAsync(malformed);
        malformedError.Code.ShouldBe(ApiErrorCode.NotFound);
        malformedError.ShouldBe(await ErrorAsync(unknown), "the id's shape is not an oracle either");

        host.Recorder.PatternFor("DELETE", SessionsPath + "/not-a-uuid").ShouldBe(
            SessionsPath + "/{s}",
            "the malformed value must reach the handler as the route's id, not die in parameter binding");

        // `DELETE /sessions/revoke-all` is this route: the literal segment binds as the id and answers the same 404.
        using (var literal = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            RevokeAllPath,
            SessionTestHost.CookieHeaderFor(caller.SessionCookie, caller.Xsrf),
            xsrfToken: caller.Xsrf.RequestToken))
        {
            literal.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await ErrorAsync(literal)).ShouldBe(malformedError, "the literal route name is just a value that is not a uuid");
        }

        // Authentication outranks the id's shape: the same malformed value without a cookie is 401, not 404 and
        // not a binding 400.
        using (var anonymous = await SessionTestHost.SendAsync(client, HttpMethod.Delete, SessionsPath + "/not-a-uuid"))
        {
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await SessionTestHost.ErrorCodeAsync(anonymous)).ShouldBe(ApiErrorCode.Unauthenticated);
            host.Recorder.PatternFor("DELETE", SessionsPath + "/not-a-uuid").ShouldBe(SessionsPath + "/{s}");
        }

        (await host.SessionRowAsync(callerId)).RevokedAt.ShouldBeNull("no refused call revokes anything");
    }

    /// <summary>
    /// AC6 (QA G3): a caller may revoke ANOTHER session of its own account — the multi-device case the route
    /// exists for. The answer is 204 with no <c>Set-Cookie</c> at all: the calling session is still live, so
    /// neither a re-issue nor an expiry belongs in that response.
    /// </summary>
    [Fact]
    public async Task Revoking_Another_Own_Session_Keeps_The_Calling_Session_Signed_In()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var currentId = await host.SessionIdOfAsync(member.SessionCookie);

        // A second device: a live row of the same account, minted later so the list order is unambiguous.
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        var other = await host.CreateSessionAsync(member.UserId);
        var otherId = await host.SessionIdOfAsync(other.CookieValue);

        using (var revoked = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionsPath + "/" + otherId,
            SessionTestHost.CookieHeaderFor(member.SessionCookie, member.Xsrf),
            xsrfToken: member.Xsrf.RequestToken))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            AssertNoSessionCookie(revoked, "revoking another row must not touch the caller's own cookies");
        }

        (await host.SessionRowAsync(otherId)).RevokedAt.ShouldNotBeNull("the addressed row is the one revoked");
        (await host.SessionRowAsync(currentId)).RevokedAt.ShouldBeNull("the calling session is not the target");

        // And the caller really still authenticates on the row it kept.
        using (var list = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie)))
        {
            list.StatusCode.ShouldBe(HttpStatusCode.OK);
            SessionTestHost.CookieValue(list, SessionTestHost.SessionCookieName).ShouldBeNull(
                "a live caller keeps the cookie it has");
        }
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
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");

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
        var plain = await host.SignInMemberInOrgAsync(client, member.OrgId, roleCode: null);

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
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");

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

            // The session cookie AND the antiforgery pair die with the row (QA G8), so nothing the browser holds
            // can drive a further mutation on the dead session.
            AssertCookiesExpired(revoked);
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
    /// The body refusals of <c>revoke-all</c> (the two responses the contract declares besides 204/401/403/503):
    /// a body that is not valid JSON is 422 <c>request_invalid</c> and a body over the 1024-byte bound is 413
    /// <c>payload_too_large</c>, both BEFORE the definer — so a client cannot use the write path to get past either
    /// check, and no row of the account moves.
    /// </summary>
    /// <remarks>
    /// The same two answers <c>POST /auth/invite-token</c> gives the same shapes: the media type and the size are
    /// security boundaries in this codebase (<c>Result.Invalid</c> is the 422 <c>request_invalid</c> mapping), so a
    /// refusal here is the shared one and not a route-local invention. A body WITHOUT the member
    /// (<c>{}</c>) is the documented default — keep the current session — and is asserted at the end, so the
    /// refusals above cannot be passing for "every body is refused".
    /// </remarks>
    [Fact]
    public async Task A_Refused_Revoke_All_Body_Answers_Its_Own_Error_And_Writes_Nothing()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var currentId = await host.SessionIdOfAsync(member.SessionCookie);
        var other = await host.CreateSessionAsync(member.UserId);
        var otherId = await host.SessionIdOfAsync(other.CookieValue);

        using (var malformed = await RevokeAllAsync(client, member, body: "{\"keep_current\":"))
        {
            malformed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await SessionTestHost.ErrorCodeAsync(malformed)).ShouldBe(ApiErrorCode.RequestInvalid);
        }

        var oversizedBody = "{\"keep_current\": true, \"pad\": \"" + new string('x', 2000) + "\"}";
        using (var oversized = await RevokeAllAsync(client, member, body: oversizedBody))
        {
            oversized.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
            (await SessionTestHost.ErrorCodeAsync(oversized)).ShouldBe(ApiErrorCode.PayloadTooLarge);
        }

        (await host.SessionRowAsync(otherId)).RevokedAt.ShouldBeNull("a refused body revokes nothing");
        (await host.SessionRowAsync(currentId)).RevokedAt.ShouldBeNull();

        // The documented default: an object without `keep_current` keeps the current session and still revokes
        // the account's other rows.
        using (var empty = await RevokeAllAsync(client, member, body: "{}"))
        {
            empty.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            AssertNoSessionCookie(empty, "keep_current defaults to true, so the caller stays signed in");
        }

        (await host.SessionRowAsync(otherId)).RevokedAt.ShouldNotBeNull("`{}` is the default, not a refusal");
        (await host.SessionRowAsync(currentId)).RevokedAt.ShouldBeNull();
    }

    /// <summary>
    /// AC5: each of the three mutations, carrying a valid session and its antiforgery COOKIE but no header, is
    /// refused 403 <c>csrf_failed</c> by the host-wide gate — and no row is revoked, which is what proves no
    /// handler ran.
    /// </summary>
    /// <remarks>
    /// The probe needs a row that a handler WOULD change, or the assertion cannot fail: <c>revoke-all</c> keeps the
    /// answering session by default, so the caller's own row stays live even if the gate were bypassed. A second
    /// session of the same account is seeded for exactly that reason (QA F1) — it is the one a leaked-through
    /// <c>revoke-all</c> would kill, and the one a leaked-through <c>DELETE</c> of the current id would not.
    /// </remarks>
    [Fact]
    public async Task Every_Session_Mutation_Without_The_Pair_Is_403_CsrfFailed()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var currentId = await host.SessionIdOfAsync(member.SessionCookie);
        var other = await host.CreateSessionAsync(member.UserId);
        var otherId = await host.SessionIdOfAsync(other.CookieValue);

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
        (await host.SessionRowAsync(otherId)).RevokedAt.ShouldBeNull(
            "a refused revoke-all must not have revoked the account's other sessions");
    }

    /// <summary>
    /// AC5 (QA G7): a PRESENT but wrong header is as refused as a missing one. Each mutation carries the real
    /// cookie pair and an <c>X-XSRF-TOKEN</c> that is not the pair's request token, and the answer is 403
    /// <c>csrf_failed</c> with nothing written — the caller's own row stays live and still authenticates, so the
    /// probe cannot pass because the session died.
    /// </summary>
    [Fact]
    public async Task Every_Session_Mutation_With_A_Mismatched_Token_Is_403_CsrfFailed()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var currentId = await host.SessionIdOfAsync(member.SessionCookie);
        var other = await host.CreateSessionAsync(member.UserId);
        var otherId = await host.SessionIdOfAsync(other.CookieValue);

        var probes = new (HttpMethod Method, string Path)[]
        {
            // A row a leaked-through DELETE would revoke, so the assertion can fail.
            (HttpMethod.Delete, SessionsPath + "/" + otherId),
            (HttpMethod.Post, RevokeAllPath),
            (HttpMethod.Post, LogoutPath),
        };

        foreach (var (method, path) in probes)
        {
            using var response = await SessionTestHost.SendAsync(
                client,
                method,
                path,
                SessionTestHost.CookieHeaderFor(member.SessionCookie, member.Xsrf),
                xsrfToken: "not-the-pairs-token");

            response.StatusCode.ShouldBe(
                HttpStatusCode.Forbidden,
                method + " " + path + " must validate the header against this session's pair");
            (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.CsrfFailed);
        }

        (await host.SessionRowAsync(currentId)).RevokedAt.ShouldBeNull();
        (await host.SessionRowAsync(otherId)).RevokedAt.ShouldBeNull();

        using var list = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie));
        list.StatusCode.ShouldBe(HttpStatusCode.OK, "the refused mutations left the caller signed in");
    }

    /// <summary>
    /// AC4 (QA G1/G2): a REVOKED cookie is refused by the authenticator on every one of the four routes — the read
    /// included — with 401 <c>session_revoked</c>, before any handler runs: a second live session of the same
    /// account is still live afterwards, which is a row no bypassed handler could have left alone.
    /// </summary>
    /// <remarks>
    /// The mutations are sent WITHOUT the antiforgery pair on purpose: the credential is judged first, so the
    /// answer is the session's own 401 and not the middleware's 403 — that ordering is what "before any handler
    /// runs" means, and a 403 here would mean the dead cookie was still in scope.
    /// </remarks>
    [Fact]
    public async Task A_Revoked_Cookie_Is_401_Session_Revoked_On_Every_Route_And_Writes_Nothing()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var currentId = await host.SessionIdOfAsync(member.SessionCookie);

        var survivor = await host.CreateSessionAsync(member.UserId);
        var survivorId = await host.SessionIdOfAsync(survivor.CookieValue);

        // The caller's own credential dies (what a logout or an admin revoke leaves behind), the other row lives.
        await host.RevokeSessionAsync(currentId);

        foreach (var (method, path) in DeadCookieProbes(survivorId))
        {
            using var response = await SessionTestHost.SendAsync(
                client,
                method,
                path,
                SessionTestHost.CookieHeaderFor(member.SessionCookie));

            response.StatusCode.ShouldBe(
                HttpStatusCode.Unauthorized,
                method + " " + path + " must judge the dead cookie before anything else");
            (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.SessionRevoked);
        }

        (await host.SessionRowAsync(survivorId)).RevokedAt.ShouldBeNull(
            "no route may act on a session the dead cookie did not prove");
    }

    /// <summary>
    /// AC4 (QA G1/G2, the timeout half): a cookie past its idle deadline is 401 <c>session_expired</c> on all four
    /// routes, equally before any handler — the account's other live session survives untouched.
    /// </summary>
    [Fact]
    public async Task An_Expired_Cookie_Is_401_Session_Expired_On_Every_Route_And_Writes_Nothing()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var currentId = await host.SessionIdOfAsync(member.SessionCookie);

        var survivor = await host.CreateSessionAsync(member.UserId);
        var survivorId = await host.SessionIdOfAsync(survivor.CookieValue);

        // The owner seam moves the deadline instead of the clock, so the LIVE row of the same account keeps its
        // own window (the idiom SessionListTests uses for the same arrangement).
        await host.ArrangeAsync(
            "update public.session set expires_at = @past where id = @id",
            ("past", host.Clock.GetUtcNow().AddSeconds(-1).ToUniversalTime()),
            ("id", currentId));

        foreach (var (method, path) in DeadCookieProbes(survivorId))
        {
            using var response = await SessionTestHost.SendAsync(
                client,
                method,
                path,
                SessionTestHost.CookieHeaderFor(member.SessionCookie));

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, method + " " + path);
            (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.SessionExpired);
        }

        (await host.SessionRowAsync(survivorId)).RevokedAt.ShouldBeNull();
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
    /// The four session routes, each addressed at a row OTHER than the credential the probe presents — so a handler
    /// that ran anyway would be visible in that row.
    /// </summary>
    /// <param name="target">The live row of the same account the probes would touch if a handler ran.</param>
    private static (HttpMethod Method, string Path)[] DeadCookieProbes(Guid target) =>
    [
        (HttpMethod.Get, SessionsPath),
        (HttpMethod.Delete, SessionsPath + "/" + target),
        (HttpMethod.Post, RevokeAllPath),
        (HttpMethod.Post, LogoutPath),
    ];

    /// <summary>
    /// The sign-out shape of a 204: the session cookie and the antiforgery pair, each expired exactly once with
    /// <c>Max-Age=0</c>, the deletion's <c>Path=/</c> and <c>Secure</c> — the attributes they were issued with, so
    /// a <c>__Host-</c> browser accepts the deletion.
    /// </summary>
    /// <param name="response">The 204 that ended the caller's own session.</param>
    private static void AssertCookiesExpired(HttpResponseMessage response)
    {
        foreach (var cookieName in new[]
        {
            SessionTestHost.SessionCookieName,
            SessionTestHost.XsrfCookieName,
            SessionTestHost.AntiforgeryCookieName,
        })
        {
            var expiries = SessionTestHost.Cookies(response, cookieName);
            expiries.ShouldHaveSingleItem($"{cookieName} must be expired exactly once by the sign-out response");

            var expiry = expiries[0];
            expiry.ShouldContain("max-age=0", Case.Insensitive, "the sign-out response drops the cookie");
            expiry.ShouldContain("path=/", Case.Insensitive, "a deletion must match the cookie's path");
            expiry.ShouldContain("secure", Case.Insensitive);
        }
    }

    /// <summary>
    /// The complement: a response that must NOT touch the caller's cookies — neither an expiry nor a re-issue —
    /// because the session it presented is still the credential.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="because">Why the response must leave the cookies alone.</param>
    private static void AssertNoSessionCookie(HttpResponseMessage response, string because)
    {
        foreach (var cookieName in new[]
        {
            SessionTestHost.SessionCookieName,
            SessionTestHost.XsrfCookieName,
            SessionTestHost.AntiforgeryCookieName,
        })
        {
            SessionTestHost.Cookies(response, cookieName).ShouldBeEmpty($"{cookieName}: {because}");
        }
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
