using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// AC6 of 02.3.2-T2 over HTTP: <c>GET /v1/auth/sessions</c> lists exactly the caller's LIVE sessions, marks the
/// one that made the request, and carries no secret — driven on the real host, the real authenticator and the
/// real <c>evidgate_resolve.session_list</c> definer of migration 0048, against the harness database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a list test needs a database.</b> "The caller's own sessions, live only" is a statement about rows:
/// the second device is a real <c>session</c> row minted by the store, the revoked one is revoked through the
/// owner seam, and the expired one is an <c>expires_at</c> the owner moved into the past. A stub would assert
/// the handler's mapping and nothing about the query the route exists for.
/// </para>
/// <para>
/// <b>Nothing sensitive on the wire.</b> The body is scanned for the cookie value, for the stored HMAC and for
/// the pepper words, and the item's property set is asserted exactly — so a later "helpful" column cannot start
/// leaking through the list (D9).
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class SessionListTests : TenantTest
{
    /// <summary>The route under test (02.3.2 §5).</summary>
    private const string SessionsPath = "/v1/auth/sessions";

    /// <summary>Shares the run's database: every assertion is about the sessions of the account it signed in.</summary>
    public SessionListTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// AC6 (happy path + boundary): the list answers the caller's live sessions newest first, marks exactly the
    /// requesting one, and drops a session that was revoked or that is past its idle deadline.
    /// </summary>
    [Fact]
    public async Task The_List_Shows_The_Callers_Live_Sessions_With_The_Current_One_Marked()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        var currentId = await host.SessionIdOfAsync(member.SessionCookie);

        using (var response = await ListAsync(client, member))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var items = await ItemsAsync(response);
            items.Count.ShouldBe(1, "the caller has exactly the session it signed in with");
            items[0].GetProperty("id").GetGuid().ShouldBe(currentId);
            items[0].GetProperty("current").GetBoolean().ShouldBeTrue();
        }

        // A second device of the SAME account: a live row, but not the one this request authenticated as. The
        // clock moves first so the two rows' created_at differ — the list orders by it — while the caller stays
        // live, because the move is far inside its idle window.
        host.Clock.Advance(TimeSpan.FromSeconds(5));

        var second = await host.CreateSessionAsync(member.UserId);
        var secondId = await host.SessionIdOfAsync(second.CookieValue);

        using (var response = await ListAsync(client, member))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var items = await ItemsAsync(response);
            items.Count.ShouldBe(2);
            items[0].GetProperty("id").GetGuid().ShouldBe(secondId, "the list is newest first");
            items.Count(item => item.GetProperty("current").GetBoolean()).ShouldBe(
                1,
                "exactly the requesting session is marked current");
            items.Single(item => item.GetProperty("current").GetBoolean())
                .GetProperty("id").GetGuid().ShouldBe(currentId);
        }

        // A revoked row is not a session the caller can still see.
        await host.RevokeSessionAsync(secondId);

        // A row past its idle deadline is expired (AC2) and equally gone from the list; the owner seam moves the
        // deadline instead of the clock, so the caller's own session stays live for the request that reads.
        var idle = await host.CreateSessionAsync(member.UserId);
        var idleId = await host.SessionIdOfAsync(idle.CookieValue);
        await host.ArrangeAsync(
            "update public.session set expires_at = @past where id = @id",
            ("past", host.Clock.GetUtcNow().AddSeconds(-1).ToUniversalTime()),
            ("id", idleId));

        using (var response = await ListAsync(client, member))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var items = await ItemsAsync(response);
            items.Count.ShouldBe(1, "revoked and expired rows are not listed");
            items[0].GetProperty("id").GetGuid().ShouldBe(currentId);
        }
    }

    /// <summary>
    /// AC6 / D9: the body carries the documented members and nothing else — no cookie value, no stored HMAC, no
    /// pepper word — and every listed instant is the row's, not a placeholder.
    /// </summary>
    [Fact]
    public async Task The_List_Carries_No_Secret_And_Only_The_Documented_Members()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        using var response = await ListAsync(client, member);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        (response.Headers.CacheControl?.NoStore ?? false).ShouldBeTrue(
            "the list is personal data: no shared cache may keep a copy");

        var body = await SessionTestHost.BodyAsync(response);
        body.ShouldNotContain(member.SessionCookie, Case.Sensitive, "the cookie value is never echoed");
        body.ShouldNotContain(Convert.ToHexString(host.HmacOf(member.SessionCookie)), Case.Insensitive);
        body.ShouldNotContain("hmac", Case.Insensitive, "not even the column's name reaches the wire");
        body.ShouldNotContain("pepper", Case.Insensitive);

        var items = JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray().ToArray();
        items.ShouldHaveSingleItem();

        var members = items[0].EnumerateObject().Select(property => property.Name).ToArray();
        members.Length.ShouldBe(8, "the list emits exactly the contract's members");
        foreach (var expected in new[]
        {
            "absolute_expires_at",
            "created_at",
            "current",
            "expires_at",
            "id",
            "ip",
            "last_seen_at",
            "user_agent",
        })
        {
            members.ShouldContain(expected);
        }

        // The instants are the row's own, and the device metadata is the one the sign-in presented.
        var row = await host.SessionRowAsync(await host.SessionIdOfAsync(member.SessionCookie));
        items[0].GetProperty("last_seen_at").GetDateTimeOffset().ShouldBe(row.LastSeenAt);
        items[0].GetProperty("expires_at").GetDateTimeOffset().ShouldBe(row.ExpiresAt);
        items[0].GetProperty("absolute_expires_at").GetDateTimeOffset().ShouldBe(row.AbsoluteExpiresAt);
        items[0].GetProperty("user_agent").GetString().ShouldBe(SessionTestHost.UserAgent);
    }

    /// <summary>
    /// A list needs a caller: no cookie and a bearer request (T38: never cookie-authenticated) both answer 401
    /// <c>unauthenticated</c>, and the route really matched — so the answer is about the session, not a 404 page.
    /// </summary>
    [Fact]
    public async Task The_List_Without_A_Live_Session_Is_401_Unauthenticated()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        host.Recorder.Clear();

        using (var anonymous = await SessionTestHost.SendAsync(client, HttpMethod.Get, SessionsPath))
        {
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await SessionTestHost.ErrorCodeAsync(anonymous)).ShouldBe(ApiErrorCode.Unauthenticated);
        }

        using (var bearer = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            SessionsPath,
            authorization: "Bearer not-a-session"))
        {
            bearer.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await SessionTestHost.ErrorCodeAsync(bearer)).ShouldBe(ApiErrorCode.Unauthenticated);
        }

        host.Recorder.PatternFor("GET", SessionsPath).ShouldNotBeNull();
    }

    /// <summary>One list request as <paramref name="member"/>'s browser sends it.</summary>
    /// <param name="client">A client of the host.</param>
    /// <param name="member">The signed-in account.</param>
    private static Task<HttpResponseMessage> ListAsync(HttpClient client, SignedInMember member) =>
        SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie, member.Xsrf));

    /// <summary>The <c>items</c> array of a list response, each element cloned off the parsed document.</summary>
    /// <param name="response">The 200 response.</param>
    private static async Task<IReadOnlyList<JsonElement>> ItemsAsync(HttpResponseMessage response)
    {
        var body = await SessionTestHost.BodyAsync(response);
        using var document = JsonDocument.Parse(body);

        return [.. document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone())];
    }
}
