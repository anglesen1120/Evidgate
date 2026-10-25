using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// 02.3.2-T1 AC7 (and AC8 at the HTTP boundary) over the REAL host: a session id is issued again — never reused —
/// by a login that presents the cookie of the session it replaces and by the FIRST request that follows a
/// privilege change, the predecessor is revoked in the same write, <c>rotated_from</c> chains the successor to
/// it, and the old cookie value never authenticates again.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the cases prove.</b> AC7(a) is a sign-in that carries the session cookie the browser still holds: the
/// completion revokes that row in the same statement as the insert (D5a). AC7(b) is a privilege change — arranged
/// through the owner seam as <c>rotation_required_at</c>, which is exactly the state a role change produces inside
/// its transaction — and the next request rotates the session before its handler runs (D5b), answering with the
/// refreshed cookie and pair. AC8's replay is asserted against the dead value in both.
/// </para>
/// <para>
/// <b>The rotated cookie is a login cookie.</b> It goes through the same writer, so the attribute set is compared
/// with the sign-in's own <c>Set-Cookie</c> (name, <c>Secure</c>, <c>HttpOnly</c>, <c>SameSite=Lax</c>,
/// <c>Path=/</c>, no <c>Domain</c>), and its <c>Max-Age</c> is the REMAINING absolute lifetime: a clock moved five
/// minutes past the sign-in must shorten the rotated cookie by exactly five minutes, because a rotation never
/// extends an absolute deadline.
/// </para>
/// <para>
/// <b>Single winner.</b> Two concurrent requests on one rotation-required cookie: exactly one is authenticated
/// (and receives the <c>Set-Cookie</c> pair), the other is 401 <c>session_revoked</c>, and exactly one successor
/// row exists. The invariant is timing-independent — whichever interleaving the server picks, the old row can be
/// revoked once.
/// </para>
/// </remarks>
/// <param name="fixture">The migrated harness database.</param>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class RotationTests(DatabaseFixture fixture) : HarnessTest(fixture)
{
    /// <summary>The cookie-authenticated mutation every case drives (its 204 is the "handler ran" signal).</summary>
    private const string PendingPath = "/v1/auth/link/pending";

    /// <summary>How far the clock moves between a sign-in and a rotation in the Max-Age case.</summary>
    private static readonly TimeSpan Elapsed = TimeSpan.FromMinutes(5);

    /// <summary>
    /// AC7(a), AC8: a sign-in that presents the cookie of the session it replaces mints a NEW session id, chains
    /// <c>rotated_from</c> to the predecessor, revokes the predecessor, and leaves the old value dead.
    /// </summary>
    [Fact]
    public async Task A_Sign_In_That_Presents_The_Current_Cookie_Rotates_The_Session()
    {
        await using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var signIn = await SignInAsync(host, client);
        var (member, signInCookie, signInPair) = (signIn.Member, signIn.SessionCookie, signIn.Pair);
        var predecessorId = await host.SessionIdOfAsync(signInCookie);

        using var second = await host.SignInAsync(client, SessionTestHost.Google, signInCookie);

        second.StatusCode.ShouldBe(HttpStatusCode.Found, "the second sign-in completes with its redirect");

        var rotatedCookie = SessionTestHost.SessionCookieValue(second);
        rotatedCookie.ShouldNotBeNull("the rotation issues a new session cookie");
        rotatedCookie.ShouldNotBe(signInCookie, "a login rotation never reuses the value it replaced");

        var successorId = await host.SessionIdOfAsync(rotatedCookie!);
        successorId.ShouldNotBe(predecessorId, "the session id is new (AC7)");

        // The chain, read as the only role that may read the table: the successor names its predecessor, and
        // exactly one row does.
        (await host.CountAsync(
            "select count(*) from public.session where id = @id and rotated_from = @from",
            ("id", successorId),
            ("from", predecessorId))).ShouldBe(1, "rotated_from points at the predecessor");

        (await host.SessionRowAsync(predecessorId)).RevokedAt.ShouldNotBeNull(
            "the predecessor is revoked in the same statement as the insert");
        (await host.SessionRowAsync(successorId)).RevokedAt.ShouldBeNull();
        (await host.SessionRowAsync(successorId)).RotationRequiredAt.ShouldBeNull();

        // AC8: the old cookie is dead, and it is reported as such — not as an anonymous request.
        using var replay = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(signInCookie, signInPair),
            xsrfToken: signInPair.RequestToken);

        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(replay)).ShouldBe(ApiErrorCode.SessionRevoked);

        // The refreshed pair is bound to the new session: it validates, while the predecessor's pair does not.
        var refreshedPair = XsrfPair.From(second);

        using var accepted = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(rotatedCookie!, refreshedPair),
            xsrfToken: refreshedPair.RequestToken);
        accepted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var stalePair = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(rotatedCookie!, signInPair),
            xsrfToken: signInPair.RequestToken);
        stalePair.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(stalePair)).ShouldBe(ApiErrorCode.CsrfFailed);

        // The sign-in left the account with exactly one live session: the successor.
        (await host.CountAsync(
            "select count(*) from public.session where user_id = @user and revoked_at is null",
            ("user", member.UserId))).ShouldBe(1);
    }

    /// <summary>
    /// AC7(b): the next request after a privilege change rotates the session — a new id, the predecessor dead,
    /// the chain intact — with a cookie that carries the login attributes and the REMAINING absolute lifetime,
    /// and a pair re-issued for the NEW session.
    /// </summary>
    [Fact]
    public async Task A_Privilege_Change_Rotates_The_Next_Request()
    {
        await using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var signIn = await SignInAsync(host, client);
        var (member, sessionCookie, pair) = (signIn.Member, signIn.SessionCookie, signIn.Pair);
        var predecessorId = await host.SessionIdOfAsync(sessionCookie);
        var predecessor = await host.SessionRowAsync(predecessorId);

        // The login's own cookie header, for the attribute comparison below.
        var loginCookieHeader = signIn.SessionSetCookie;
        loginCookieHeader.ShouldContain(SessionTestHost.SessionCookieName + "=");

        // The privilege change 02.1.2's role change performs inside its transaction, through the real seam, and
        // then five minutes of ordinary time passing: the flag alone must not act, only the next request may.
        (await RequireRotationAsync(host, member.OrgId, member.UserId)).ShouldBe(
            1,
            "the privilege change flags the account's one live session");
        host.Clock.Advance(Elapsed);

        using var response = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(sessionCookie, pair),
            xsrfToken: pair.RequestToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, "the rotation happens, then the handler runs");

        var rotatedCookie = SessionTestHost.SessionCookieValue(response);
        rotatedCookie.ShouldNotBeNull("the rotated cookie is issued in the same response");
        rotatedCookie.ShouldNotBe(sessionCookie);

        var successorId = await host.SessionIdOfAsync(rotatedCookie!);
        successorId.ShouldNotBe(predecessorId);

        // ---- the cookie itself: the login's attributes exactly, and the remaining ABSOLUTE lifetime.
        var rotatedHeader = SessionTestHost.Cookies(response, SessionTestHost.SessionCookieName).ShouldHaveSingleItem();
        AttributesOf(rotatedHeader).ShouldBe(
            AttributesOf(loginCookieHeader),
            "a rotated cookie is written by the login's own writer, so the attribute sets cannot drift (D1)");
        rotatedHeader.ShouldNotContain("domain=", Case.Insensitive);
        rotatedHeader.ShouldContain("max-age=", Case.Insensitive);
        rotatedHeader.ShouldContain("expires=", Case.Insensitive);

        var expectedRemaining = (int)(predecessor.AbsoluteExpiresAt - host.Clock.GetUtcNow()).TotalSeconds;
        MaxAgeSeconds(rotatedHeader).ShouldBeInRange(expectedRemaining - 2, expectedRemaining + 2);
        MaxAgeSeconds(rotatedHeader).ShouldBeLessThan(
            host.AbsoluteSeconds,
            "Max-Age is the REMAINING absolute lifetime, never a fresh one: a rotation does not extend the deadline");

        // ---- the refreshed pair is bound to the NEW session, so the predecessor's token dies with it. The
        // framework keeps a valid antiforgery cookie token (it is not session-bound) and mints only the request
        // token, which is: that half is what carries the session id, so the pair is a new one for the browser.
        var refreshedPair = XsrfPair.From(response, pair);

        using var withOldPair = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(rotatedCookie!, pair),
            xsrfToken: pair.RequestToken);
        withOldPair.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(withOldPair)).ShouldBe(ApiErrorCode.CsrfFailed);

        using var withNewPair = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(rotatedCookie!, refreshedPair),
            xsrfToken: refreshedPair.RequestToken);
        withNewPair.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // ---- the rows: chain, revocation, inherited absolute deadline, cleared flag.
        var successor = await host.SessionRowAsync(successorId);
        successor.RotationRequiredAt.ShouldBeNull("the successor starts unflagged");
        successor.AbsoluteExpiresAt.ShouldBe(
            predecessor.AbsoluteExpiresAt,
            "the hard deadline is inherited: a rotation never extends it (D5)");
        successor.ExpiresAt.ShouldBeLessThanOrEqualTo(successor.AbsoluteExpiresAt);

        (await host.CountAsync(
            "select count(*) from public.session where id = @id and rotated_from = @from",
            ("id", successorId),
            ("from", predecessorId))).ShouldBe(1);

        var revokedPredecessor = await host.SessionRowAsync(predecessorId);
        revokedPredecessor.RevokedAt.ShouldNotBeNull();
        revokedPredecessor.RotationRequiredAt.ShouldBeNull("the flag is consumed by the rotation");

        // AC8: a replay of the predecessor's value stays refused.
        using var replay = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(sessionCookie, pair),
            xsrfToken: pair.RequestToken);
        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(replay)).ShouldBe(ApiErrorCode.SessionRevoked);
    }

    /// <summary>
    /// AC7(b), the single-winner addendum: two requests with the SAME rotation-required cookie — exactly one is
    /// authenticated and receives the <c>Set-Cookie</c> pair, the other is 401 <c>session_revoked</c> and issues
    /// no cookie, and exactly one successor row exists.
    /// </summary>
    [Fact]
    public async Task Two_Concurrent_Requests_Rotate_Once_And_Only_One_Wins()
    {
        await using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var signIn = await SignInAsync(host, client);
        var (member, sessionCookie, pair) = (signIn.Member, signIn.SessionCookie, signIn.Pair);
        var predecessorId = await host.SessionIdOfAsync(sessionCookie);

        (await RequireRotationAsync(host, member.OrgId, member.UserId)).ShouldBe(1, "one live session is flagged");

        var cookieHeader = SessionTestHost.CookieHeaderFor(sessionCookie, pair);
        var first = SessionTestHost.SendAsync(client, HttpMethod.Delete, PendingPath, cookieHeader, pair.RequestToken);
        var second = SessionTestHost.SendAsync(client, HttpMethod.Delete, PendingPath, cookieHeader, pair.RequestToken);
        var responses = await Task.WhenAll(first, second);

        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.NoContent).ShouldBe(
                1,
                "exactly one request is authenticated, and it is the one that performed the rotation");

            var loser = responses.Single(response => response.StatusCode != HttpStatusCode.NoContent);
            loser.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await SessionTestHost.ErrorCodeAsync(loser)).ShouldBe(
                ApiErrorCode.SessionRevoked,
                "the loser of a rotation never authenticates on the old row");
            SessionTestHost.SessionCookieValue(loser).ShouldBeNull("only the winner is issued a session");
            SessionTestHost.Cookies(loser, SessionTestHost.XsrfCookieName).ShouldBeEmpty(
                "only the winner is issued a pair");

            (await host.CountAsync(
                "select count(*) from public.session where rotated_from = @id",
                ("id", predecessorId))).ShouldBe(1, "the successor insert is gated by the predecessor's revoke");

            (await host.SessionRowAsync(predecessorId)).RevokedAt.ShouldNotBeNull();

            (await host.CountAsync(
                "select count(*) from public.session where user_id = @user and revoked_at is null",
                ("user", member.UserId))).ShouldBe(1, "the browser is left with exactly one live session");
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// AC7, the failure path: when the predecessor dies between the privilege change and the request (a logout,
    /// an admin revoke or the winner of a concurrent rotation), the request answers 401 <c>session_revoked</c>
    /// and never authenticates on the old row — no successor is minted and no cookie is issued.
    /// </summary>
    [Fact]
    public async Task A_Revoked_Predecessor_Never_Authenticates_On_The_Old_Session()
    {
        await using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var signIn = await SignInAsync(host, client);
        var (member, sessionCookie, pair) = (signIn.Member, signIn.SessionCookie, signIn.Pair);
        var sessionId = await host.SessionIdOfAsync(sessionCookie);

        (await RequireRotationAsync(host, member.OrgId, member.UserId)).ShouldBe(1, "one live session is flagged");
        await host.RevokeSessionAsync(sessionId);

        using var response = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(sessionCookie, pair),
            xsrfToken: pair.RequestToken);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "the credential is judged before the pair, so a dead session is 401 rather than 403");
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.SessionRevoked);

        SessionTestHost.SessionCookieValue(response).ShouldBeNull("a failed rotation issues no session");
        SessionTestHost.Cookies(response, SessionTestHost.XsrfCookieName).ShouldBeEmpty();

        (await host.CountAsync(
            "select count(*) from public.session where rotated_from = @id",
            ("id", sessionId))).ShouldBe(0, "no successor exists for a rotation that could not run");

        (await host.SessionRowAsync(sessionId)).RevokedAt.ShouldNotBeNull();
    }

    /// <summary>
    /// AC7 / D5a, fixation: neither a client-supplied session id nor a client-chosen cookie VALUE is honoured at
    /// login. The presented value is only ever matched as a cookie its holder could present, so a value this
    /// server never minted is no predecessor: the sign-in mints a fresh opaque value, chains nothing and leaves
    /// the chosen value dead. An over-long value — longer than any value this server mints — is ignored the same
    /// way, never hashed into a lookup.
    /// </summary>
    [Fact]
    public async Task A_Client_Supplied_Cookie_Value_Is_Never_Honoured_At_Login()
    {
        await using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SeedMemberAsync();
        const string Chosen = "client-chosen-session-value";

        using var first = await host.SignInAsync(client, SessionTestHost.Google, Chosen);

        first.StatusCode.ShouldBe(HttpStatusCode.Found);

        var issued = SessionTestHost.SessionCookieValue(first);
        issued.ShouldNotBeNull("the sign-in mints the value, the client never supplies it");
        issued.ShouldNotBe(Chosen);

        // The chosen value is not a credential: no row is addressed by its hash, and a request with it is
        // anonymous.
        (await host.CountAsync(
            "select count(*) from public.session where cookie_hmac = @hmac",
            ("hmac", host.HmacOf(Chosen)))).ShouldBe(0);

        using var probe = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(Chosen));
        probe.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(probe)).ShouldBe(ApiErrorCode.Unauthenticated);

        (await host.CountAsync(
            "select count(*) from public.session where user_id = @user and rotated_from is not null",
            ("user", member.UserId))).ShouldBe(
            0,
            "a value the server never minted is not a predecessor, so nothing is chained to it");

        // Boundary: an over-long value is refused without a lookup, so it cannot revoke or chain anything either.
        var overlong = new string('z', 200);

        using var second = await host.SignInAsync(client, SessionTestHost.Google, overlong);

        second.StatusCode.ShouldBe(HttpStatusCode.Found);

        var secondIssued = SessionTestHost.SessionCookieValue(second);
        secondIssued.ShouldNotBeNull();
        secondIssued.ShouldNotBe(overlong);
        secondIssued.ShouldNotBe(issued, "every sign-in mints a fresh value");

        (await host.CountAsync(
            "select count(*) from public.session where user_id = @user and rotated_from is not null",
            ("user", member.UserId))).ShouldBe(0);
    }

    /// <summary>
    /// One sign-in whose response (the login's own cookies) the caller keeps: the account is seeded first, so the
    /// sign-in is the only thing the case arranges through the routes.
    /// </summary>
    /// <param name="host">The host.</param>
    /// <param name="client">A client of it.</param>
    private static async Task<SignIn> SignInAsync(SessionTestHost host, HttpClient client)
    {
        var member = await host.SeedMemberAsync();

        using var response = await host.SignInAsync(client);

        response.StatusCode.ShouldBe(HttpStatusCode.Found, "a seeded member's sign-in completes with the 302");

        var cookie = SessionTestHost.SessionCookieValue(response);
        cookie.ShouldNotBeNull("the sign-in sets the session cookie");

        return new SignIn(
            member,
            cookie,
            XsrfPair.From(response),
            SessionTestHost.Cookies(response, SessionTestHost.SessionCookieName).ShouldHaveSingleItem());
    }

    /// <summary>
    /// The privilege change a role or permission change performs inside its transaction, through the real contract
    /// (<see cref="ISessionPrivilegeChange"/>) rather than by stamping the column: the number of sessions it flags
    /// is part of what it promises.
    /// </summary>
    /// <param name="host">The host whose services carry the seam.</param>
    /// <param name="userId">The account whose roles or permissions changed.</param>
    /// <summary>
    /// One privilege change through the REAL seam: <c>ISessionPrivilegeChange</c> writes on the caller's tenant
    /// scope (its own rule — the stamp must roll back with the role change that produced it), so the test opens
    /// the scope a role-change command has open and commits it exactly as that command does.
    /// </summary>
    /// <param name="host">The host whose clock the stamp comes from.</param>
    /// <param name="orgId">The organization whose scope the write runs in.</param>
    /// <param name="userId">The account whose live sessions are flagged.</param>
    private static async Task<int> RequireRotationAsync(SessionTestHost host, Guid orgId, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(host);

        await using var scope = await host.Fixture.BeginTenantScopeAsync(orgId, cancellationToken: Token);
        var marked = await new SessionRevocationStore(scope.Db, host.Clock)
            .RequireRotationForUserAsync(userId, Token);
        await scope.CommitAsync(Token);

        return marked;
    }

    /// <summary>
    /// The attribute list of a <c>Set-Cookie</c> header, normalised for comparison: the lifetime pair is dropped
    /// because the rotated cookie deliberately carries a different (remaining) one, and the value is never
    /// included.
    /// </summary>
    /// <param name="setCookie">One <c>Set-Cookie</c> header.</param>
    private static IReadOnlyList<string> AttributesOf(string setCookie)
    {
        ArgumentException.ThrowIfNullOrEmpty(setCookie);

        return
        [
            .. setCookie
                .Split(';')
                .Skip(1)
                .Select(attribute => attribute.Trim().ToLowerInvariant())
                .Where(attribute => attribute.Length > 0)
                .Where(attribute => !attribute.StartsWith("max-age=", StringComparison.Ordinal))
                .Where(attribute => !attribute.StartsWith("expires=", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>The <c>Max-Age</c> of one <c>Set-Cookie</c> header, in seconds.</summary>
    /// <param name="setCookie">One <c>Set-Cookie</c> header.</param>
    private static int MaxAgeSeconds(string setCookie)
    {
        ArgumentException.ThrowIfNullOrEmpty(setCookie);

        var match = Regex.Match(setCookie, @"(?:^|;)\s*max-age=(\d+)", RegexOptions.IgnoreCase);

        match.Success.ShouldBeTrue("a session cookie always carries a Max-Age (D1)");

        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>What one sign-in of a case produced, including the login's own <c>Set-Cookie</c> header.</summary>
    /// <param name="Member">The seeded account the verified identity resolves to.</param>
    /// <param name="SessionCookie">The <c>__Host-evidgate_session</c> value the browser now holds.</param>
    /// <param name="Pair">The antiforgery pair issued in the same response, bound to that session.</param>
    /// <param name="SessionSetCookie">The raw <c>Set-Cookie</c> header of the session cookie.</param>
    private sealed record SignIn(SeededMember Member, string SessionCookie, XsrfPair Pair, string SessionSetCookie);
}
