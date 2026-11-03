using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// AC2–AC4 of 02.3.2 over HTTP: a live session authenticates a real route, the two deadlines are enforced on an
/// injected clock, a revoked row answers its own 401 <em>before any handler runs</em>, and the request path costs
/// exactly one <c>evidgate_resolve.session_authenticate</c> statement however many layers ask for the caller.
/// </summary>
/// <remarks>
/// <para>
/// The route under test is the real <c>GET /v1/orgs</c> (02.1.1 §5): it is the smallest served route that needs
/// an authenticated member and no permission, and it answers the caller's own orgs, so "the handler ran for THIS
/// session's user" is a body assertion rather than a guess. The mutation half uses the real
/// <c>POST /v1/orgs</c>.
/// </para>
/// <para>
/// "Before any handler runs" is asserted three ways, because a status code alone would not prove it: the route
/// really matched (<see cref="RequestRecorder"/>), the handler's own statement never ran (the statement log), and
/// the database still holds exactly the rows the arrangement left (no organization was created). Time is the
/// host's clock, so an idle or absolute expiry is one <c>Advance</c> — never a wait.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class SessionAuthTests : TenantTest
{
    /// <summary>The route that needs an authenticated member and answers the caller's orgs.</summary>
    private const string OrgsPath = "/v1/orgs";

    /// <summary>The function the handler of <see cref="OrgsPath"/> calls; its absence proves no handler ran.</summary>
    private const string OrgsStatement = "evidgate_resolve.member_orgs";

    /// <summary>The one statement the session path may cost per request (D3, P1/P3).</summary>
    private const string AuthStatement = "session_authenticate";

    /// <summary>Own database: an unfiltered organization count proves no handler ran.</summary>
    public SessionAuthTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>
    /// AC2 (happy path) + P1/P3: the cookie authenticates the request, the handler answers the caller's own org
    /// (which is the proof the principal really was this session's user), and the whole request cost ONE
    /// <c>session_authenticate</c> statement even though the middleware's refusal pass and the tenant filter both
    /// asked (memoization).
    /// </summary>
    [Fact]
    public async Task A_Live_Session_Authenticates_The_Read_And_Costs_One_Statement()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        host.Statements.Clear();
        host.Recorder.Clear();

        using var response = await SessionTestHost
            .SendAsync(client, HttpMethod.Get, OrgsPath, SessionTestHost.CookieHeaderFor(member.SessionCookie))
            ;

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        host.Recorder.PatternFor("GET", OrgsPath).ShouldNotBeNull().ShouldContain("orgs");

        var body = await SessionTestHost.BodyAsync(response);
        body.ShouldContain(member.OrgId.ToString(), Case.Insensitive, "the session's user really is the caller");

        host.Statements.Containing(AuthStatement).Count.ShouldBe(1, "one request, one session_authenticate statement");
    }

    /// <summary>
    /// AC2 boundary (P1/P3): a cookie-authenticated MUTATION is judged by the antiforgery middleware and then by
    /// the tenant filter, and the two share the one statement the authenticator that already ran made — a second
    /// statement would be a second lookup of the same cookie in the same request.
    /// </summary>
    [Fact]
    public async Task A_Mutation_Shares_One_Statement_Between_The_Antiforgery_Middleware_And_The_Filter()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        host.Statements.Clear();
        host.Recorder.Clear();

        using var response = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Post,
            OrgsPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie, member.Xsrf),
            xsrfToken: member.Xsrf.RequestToken,
            jsonBody: "{}");

        // The body is invalid on purpose: what this case asserts is that authentication is not what refused it.
        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
        host.Recorder.PatternFor("POST", OrgsPath).ShouldNotBeNull();

        host.Statements.Containing(AuthStatement).Count.ShouldBe(1, "the middleware and the filter share one statement");
    }

    /// <summary>
    /// AC2 boundary: a session is live one second before its idle deadline and dead AT it (the row's
    /// <c>expires_at</c> is inclusive), and the expired request runs no handler. Two sessions are minted at the same
    /// instant and left untouched, so the deadline is the deadline and not a slide: the request inside the window
    /// uses one of them, the boundary request the other.
    /// </summary>
    [Fact]
    public async Task Idle_Expiry_Answers_Session_Expired_At_The_Deadline()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        // A second, untouched session of the same account: the inside-the-window request must not slide the one
        // the boundary request is measured on.
        var untouched = await host.CreateSessionAsync(member.UserId);

        host.Clock.Advance(TimeSpan.FromSeconds(host.IdleSeconds - 1));

        using (var inside = await SessionTestHost
            .SendAsync(client, HttpMethod.Get, OrgsPath, SessionTestHost.CookieHeaderFor(member.SessionCookie))
            )
        {
            inside.StatusCode.ShouldBe(HttpStatusCode.OK, "one second before the deadline the session is live");
        }

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        host.Statements.Clear();

        using var expired = await SessionTestHost
            .SendAsync(client, HttpMethod.Get, OrgsPath, SessionTestHost.CookieHeaderFor(untouched.CookieValue))
            ;

        expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(expired)).ShouldBe(ApiErrorCode.SessionExpired);
        host.Statements.Containing(OrgsStatement).ShouldBeEmpty("no handler may run for an expired session");
    }

    /// <summary>
    /// AC2: activity inside the window slides <c>expires_at</c>, so a request that arrives after the ORIGINAL idle
    /// deadline is still served — the proof is behavioural (the read answers 200) plus the row's own deadline.
    /// </summary>
    [Fact]
    public async Task Activity_Inside_The_Idle_Window_Slides_The_Deadline()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var cookies = SessionTestHost.CookieHeaderFor(member.SessionCookie);
        var sessionId = await host.SessionIdOfAsync(member.SessionCookie);

        host.Clock.Advance(TimeSpan.FromMinutes(5));

        using (var slid = await SessionTestHost.SendAsync(client, HttpMethod.Get, OrgsPath, cookies))
        {
            slid.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var afterSlide = await host.SessionRowAsync(sessionId);
        afterSlide.LastSeenAt.ShouldBe(host.Clock.UtcNow, TimeSpan.FromSeconds(1), "the accepted request is the last seen");
        afterSlide.ExpiresAt.ShouldBe(
            host.Clock.UtcNow.AddSeconds(host.IdleSeconds),
            TimeSpan.FromSeconds(1),
            "the idle deadline restarts from the activity");

        // The ORIGINAL deadline (sign-in + idle) has now passed and the request is still served: the slide is what
        // kept the session alive, not a deadline the browser could have reached anyway.
        host.Clock.Advance(TimeSpan.FromSeconds(host.IdleSeconds - 300 + 1));

        using var late = await SessionTestHost.SendAsync(client, HttpMethod.Get, OrgsPath, cookies);

        late.StatusCode.ShouldBe(HttpStatusCode.OK, "the slid deadline, not the original one, is what counts");
    }

    /// <summary>
    /// AC2 boundary (P1/P3): the slide is written at most once per 60 s per session — a second request inside the
    /// throttle window still authenticates but writes nothing, and the first request after it slides again.
    /// </summary>
    [Fact]
    public async Task The_Slide_Write_Is_Throttled_To_One_Per_Minute()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var cookies = SessionTestHost.CookieHeaderFor(member.SessionCookie);
        var sessionId = await host.SessionIdOfAsync(member.SessionCookie);

        host.Clock.Advance(TimeSpan.FromMinutes(5));
        using (var first = await SessionTestHost.SendAsync(client, HttpMethod.Get, OrgsPath, cookies))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var slid = await host.SessionRowAsync(sessionId);

        // 30 s later: authenticated, but the row must not be touched — one write per minute, not one per request.
        host.Clock.Advance(TimeSpan.FromSeconds(30));
        using (var throttled = await SessionTestHost.SendAsync(client, HttpMethod.Get, OrgsPath, cookies))
        {
            throttled.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var unchanged = await host.SessionRowAsync(sessionId);
        unchanged.LastSeenAt.ShouldBe(slid.LastSeenAt, TimeSpan.Zero, "the 60 s throttle swallowed this write");
        unchanged.ExpiresAt.ShouldBe(slid.ExpiresAt, TimeSpan.Zero);

        // 31 s after the last write (61 s since it): due again.
        host.Clock.Advance(TimeSpan.FromSeconds(31));
        using (var due = await SessionTestHost.SendAsync(client, HttpMethod.Get, OrgsPath, cookies))
        {
            due.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var written = await host.SessionRowAsync(sessionId);
        written.LastSeenAt.ShouldBe(host.Clock.UtcNow, TimeSpan.FromSeconds(1), "past the throttle window the slide runs");
    }

    /// <summary>
    /// AC3: the sliding deadline never passes <c>absolute_expires_at</c>, and the request that arrives AT the
    /// absolute deadline answers 401 <c>session_expired</c> even though activity kept the session "fresh".
    /// </summary>
    [Fact]
    public async Task Absolute_Expiry_Caps_Any_Slide()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var cookies = SessionTestHost.CookieHeaderFor(member.SessionCookie);
        var sessionId = await host.SessionIdOfAsync(member.SessionCookie);

        // Activity in steps that always stay inside the idle window, right up to the hard deadline: each accepted
        // request slides the idle deadline, and each slide is CLAMPED to absolute_expires_at.
        var step = host.IdleSeconds / 2;
        while (host.Clock.UtcNow < SessionTestHost.StartInstant.AddSeconds(host.AbsoluteSeconds).Subtract(TimeSpan.FromSeconds(step)))
        {
            host.Clock.Advance(TimeSpan.FromSeconds(step));

            using var active = await SessionTestHost
                .SendAsync(client, HttpMethod.Get, OrgsPath, cookies)
                ;
            active.StatusCode.ShouldBe(
                HttpStatusCode.OK,
                "the session stays live while activity arrives inside its idle window: " + host.Clock.UtcNow);
        }

        var row = await host.SessionRowAsync(sessionId);
        row.ExpiresAt.ShouldBe(
            row.AbsoluteExpiresAt,
            TimeSpan.Zero,
            "the last slide was clamped to the hard deadline, so the idle one can never outlive it");

        // Exactly AT the hard deadline, with activity a step earlier: 401, and no handler.
        host.Clock.Advance(SessionTestHost.StartInstant.AddSeconds(host.AbsoluteSeconds) - host.Clock.UtcNow);
        host.Statements.Clear();

        using var capped = await SessionTestHost.SendAsync(client, HttpMethod.Get, OrgsPath, cookies);

        capped.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(capped)).ShouldBe(ApiErrorCode.SessionExpired);
        host.Statements.Containing(OrgsStatement).ShouldBeEmpty("the absolute deadline stops the handler too");
    }

    /// <summary>
    /// AC4: a revoked row answers 401 <c>session_revoked</c> on the very next request, and nothing of the handler
    /// runs — the route matched, the statement log holds only the one authentication statement, and no row moved.
    /// </summary>
    [Fact]
    public async Task A_Revoked_Cookie_Answers_Session_Revoked_Before_The_Handler_Runs()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var sessionId = await host.SessionIdOfAsync(member.SessionCookie);

        await host.RevokeSessionAsync(sessionId);

        host.Statements.Clear();
        host.Recorder.Clear();

        using var response = await SessionTestHost
            .SendAsync(client, HttpMethod.Get, OrgsPath, SessionTestHost.CookieHeaderFor(member.SessionCookie))
            ;

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.SessionRevoked);

        host.Recorder.PatternFor("GET", OrgsPath).ShouldNotBeNull("the route really matched: this is not a 404");
        host.Statements.Containing(AuthStatement).Count.ShouldBe(1);
        host.Statements.Containing(OrgsStatement).ShouldBeEmpty("the refusal precedes the handler");
    }

    /// <summary>
    /// AC4 on a MUTATION: the antiforgery middleware judges the credential first, so a revoked cookie answers 401
    /// <c>session_revoked</c> — not 403 <c>csrf_failed</c> — and the create never runs, even with a valid pair.
    /// </summary>
    [Fact]
    public async Task A_Revoked_Cookie_On_A_Mutation_Is_Refused_Before_The_Handler_Runs()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var sessionId = await host.SessionIdOfAsync(member.SessionCookie);

        await host.RevokeSessionAsync(sessionId);
        var organizationsBefore = await host.CountAsync("select count(*) from public.organization");

        host.Statements.Clear();
        using var response = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Post,
            OrgsPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie, member.Xsrf),
            xsrfToken: member.Xsrf.RequestToken,
            jsonBody: """{"name":"Refused org","slug":"refused-org"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(response))
            .ShouldBe(ApiErrorCode.SessionRevoked, "the credential is judged before the pair");

        host.Statements.Containing(AuthStatement).Count.ShouldBe(1);
        (await host.CountAsync("select count(*) from public.organization"))
            .ShouldBe(organizationsBefore, "no handler ran, so no org was created");
    }

    /// <summary>
    /// D2 precedence boundary: a row that is BOTH revoked and past its absolute deadline answers
    /// <c>session_revoked</c> — a logout must never be reported to the browser as a timeout.
    /// </summary>
    [Fact]
    public async Task Revocation_Outranks_Expiry()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var sessionId = await host.SessionIdOfAsync(member.SessionCookie);

        await host.RevokeSessionAsync(sessionId);
        host.Clock.Advance(TimeSpan.FromSeconds(host.AbsoluteSeconds + 60));

        using var response = await SessionTestHost
            .SendAsync(client, HttpMethod.Get, OrgsPath, SessionTestHost.CookieHeaderFor(member.SessionCookie))
            ;

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.SessionRevoked);
    }

    /// <summary>
    /// D2 (negative): a value no session was ever minted from answers 401 <c>unauthenticated</c> — the same answer
    /// an absent cookie gets, so the route is no oracle for whether a value looks like one of its own. The value is
    /// minted SHAPE (43 base64url characters) and unknown at once, which is the only case that reaches the lookup.
    /// </summary>
    [Fact]
    public async Task An_Unknown_Cookie_Answers_Unauthenticated()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        member.SessionCookie.Length.ShouldBe(
            SessionAuthenticationStore.CookieValueLength,
            "the shape check's length is derived from the mint and must equal what the store actually issues");

        host.Statements.Clear();
        using var response = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            OrgsPath,
            SessionTestHost.SessionCookieName + "=AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcde");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.Unauthenticated);
        host.Statements.Containing(AuthStatement).Count.ShouldBe(1, "the lookup ran: this is the database's answer");
    }

    /// <summary>
    /// SEC3: a value of the wrong LENGTH or the wrong ALPHABET cannot have been minted by this server, so it is
    /// answered 401 <c>unauthenticated</c> without an HMAC and without a statement — an attacker cannot choose the
    /// work a garbage cookie costs. Discoverable only as the same 401 an absent cookie gets (no oracle).
    /// </summary>
    [Theory]
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcd")]
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcdef")]
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abc+e")]
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_ab/de")]
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcd=")]
    public async Task A_Value_Of_The_Wrong_Shape_Never_Reaches_The_Database(string value)
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        await host.SignInMemberAsync(client);

        host.Statements.Clear();
        using var response = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            OrgsPath,
            SessionTestHost.SessionCookieName + "=" + value);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.Unauthenticated);
        host.Statements.Containing(AuthStatement).ShouldBeEmpty(
            "a value that cannot be a minted one is refused before the HMAC and before the statement");
    }

    /// <summary>
    /// D2 boundary: a value longer than any this server mints cannot be a session, so it is refused WITHOUT a
    /// database round trip (the bound is what keeps an attacker from choosing the work).
    /// </summary>
    [Fact]
    public async Task Boundary_An_Oversized_Cookie_Value_Never_Reaches_The_Database()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        await host.SignInMemberAsync(client);

        host.Statements.Clear();
        using var response = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            OrgsPath,
            SessionTestHost.SessionCookieName + "=" + new string('a', 129));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.Unauthenticated);
        host.Statements.Containing(AuthStatement).ShouldBeEmpty("an impossible value is refused before the lookup");
    }

    /// <summary>
    /// D2 negative: no cookie at all is 401 <c>unauthenticated</c> with no statement — an anonymous request must
    /// not cost the database a lookup it could never satisfy.
    /// </summary>
    [Fact]
    public async Task An_Absent_Cookie_Answers_Unauthenticated_Without_A_Statement()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();

        host.Statements.Clear();
        using var response = await SessionTestHost.SendAsync(client, HttpMethod.Get, OrgsPath);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.Unauthenticated);
        host.Statements.Containing(AuthStatement).ShouldBeEmpty();
    }

    /// <summary>
    /// D7: a live session of a disabled account answers 403 <c>account_disabled</c> — an authorization answer, not
    /// a credential one — before the handler, and no org is listed.
    /// </summary>
    [Fact]
    public async Task A_Disabled_Account_Answers_Account_Disabled()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        await host.DisableUserAsync(member.UserId);

        host.Statements.Clear();
        using var response = await SessionTestHost
            .SendAsync(client, HttpMethod.Get, OrgsPath, SessionTestHost.CookieHeaderFor(member.SessionCookie))
            ;

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.AccountDisabled);
        host.Statements.Containing(OrgsStatement).ShouldBeEmpty("the handler never runs for a disabled account");
    }

    /// <summary>
    /// T38/D6: a request that carries an <c>Authorization</c> header is NEVER authenticated by its cookie — even a
    /// revoked one, whose refusal would otherwise answer 401 <c>session_revoked</c>. The session abstains, nothing
    /// is looked up, and the route answers its own anonymous 401.
    /// </summary>
    [Fact]
    public async Task A_Bearer_Request_Is_Never_Authenticated_By_Its_Cookie()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var sessionId = await host.SessionIdOfAsync(member.SessionCookie);

        // Revoked on purpose: a cookie judgement would answer session_revoked, so an "unauthenticated" answer can
        // only mean the cookie was never judged at all.
        await host.RevokeSessionAsync(sessionId);

        host.Statements.Clear();
        using var response = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            OrgsPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie),
            authorization: "Bearer not-a-ci-token");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(response))
            .ShouldBe(ApiErrorCode.Unauthenticated, "the session abstains for a bearer request");
        host.Statements.Containing(AuthStatement).ShouldBeEmpty("an abstaining authenticator runs no statement");
    }
}
