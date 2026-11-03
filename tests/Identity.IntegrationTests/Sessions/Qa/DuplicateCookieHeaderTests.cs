using System.Net;
using System.Security.Cryptography;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Testing;
using Identity.IntegrationTests.Sessions.Http;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Qa;

/// <summary>
/// QA2 of the 02.3.2-T1 review: a request that carries the <c>__Host-evidgate_session</c> cookie TWICE — one
/// value nothing was minted from, one live value — never answers 500, never answers anything outside the
/// precedence D2 names, and the garbage value never authenticates anything by itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>What ASP.NET Core picks, and why the test asserts it.</b> The session authenticator reads the credential
/// with <c>HttpContext.Request.Cookies[SessionCookieNames.Session]</c>, i.e. through
/// <c>RequestCookieCollection</c>. That collection is a case-insensitive dictionary filled by
/// <c>CookieHeaderParserShared.TryParseValues</c>, which does <c>store[name] = value</c> for every pair it
/// parses (dotnet/aspnetcore, <c>src/Http/Shared/CookieHeaderParserShared.cs</c>:45), iterating the
/// <c>Cookie</c> header's values in the order they arrived
/// (<c>src/Http/Http/src/Internal/RequestCookieCollection.cs</c>, <c>Parse(StringValues)</c>). The consequence
/// is deterministic and is what these cases pin: <b>the LAST pair carrying a name wins</b>, across both a
/// single header with two pairs and two <c>Cookie</c> header lines. No feature here can make the parser
/// "reject a duplicate": the framework has no duplicate-cookie diagnostic at this layer.
/// </para>
/// <para>
/// So the two orders have two defined outcomes, and both are asserted exactly: live value last → the request is
/// served for that live session (200, the caller's own org in the body); garbage value last → the request
/// presents a value no row carries, which D2 answers 401 <c>unauthenticated</c> — never a 5xx, never
/// <c>session_revoked</c> (the live session is untouched by the duplicate), never a 403.
/// </para>
/// <para>
/// The garbage value is MINTED-SHAPE on purpose — <see cref="SessionAuthenticationStore.CookieValueLength"/>
/// base64url characters, like every value this server issues — so the request reaches the lookup instead of the
/// pre-lookup shape refusal. Both answers are the same 401 <c>unauthenticated</c> envelope (the shape check is
/// not an oracle), but only a minted-shape value proves the duplicate is judged by the store.
/// </para>
/// <para>
/// The session suites run as one block with <c>--filter-namespace 'Identity.IntegrationTests.Sessions*'</c>;
/// this file's namespace <c>Identity.IntegrationTests.Sessions.Qa</c> is inside that wildcard.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class DuplicateCookieHeaderTests : TenantTest
{
    /// <summary>The route that needs an authenticated member and answers the caller's orgs.</summary>
    private const string OrgsPath = "/v1/orgs";

    /// <summary>Shares the run's database: every row it reads is addressed by its own user or HMAC.</summary>
    public DuplicateCookieHeaderTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// QA2, one header carrying two pairs of the same name, both orders: the last pair wins, so the live value
    /// last is served and the garbage value last is refused as <c>unauthenticated</c> — and neither request
    /// disturbs the live session's row.
    /// </summary>
    [Fact]
    public async Task Two_Pairs_In_One_Header_Take_The_Last_And_Never_Answer_Server_Error()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var garbage = UnmintedValue();

        // garbage first, live second: RequestCookieCollection assigns in parse order, so the live pair is the
        // one the authenticator reads.
        using (var liveWins = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            OrgsPath,
            Pairs(garbage, member.SessionCookie)))
        {
            liveWins.StatusCode.ShouldBe(
                HttpStatusCode.OK,
                "the last pair of the name is the one ASP.NET Core keeps, and it is the live value");
            (await SessionTestHost.BodyAsync(liveWins))
                .ShouldContain(member.OrgId.ToString(), Case.Insensitive, "the live session's own org is listed");
        }

        // live first, garbage second: the request presents the garbage value, which no row carries.
        using (var garbageWins = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            OrgsPath,
            Pairs(member.SessionCookie, garbage)))
        {
            garbageWins.StatusCode.ShouldBe(
                HttpStatusCode.Unauthorized,
                "the garbage value is last, so it is the presented credential — a defined 401, never a 500");
            (await SessionTestHost.ErrorCodeAsync(garbageWins)).ShouldBe(
                ApiErrorCode.Unauthenticated,
                "an unknown value is unauthenticated; it is never reported as a revocation");
        }

        var row = await host.SessionRowAsync(await host.SessionIdOfAsync(member.SessionCookie));
        row.RevokedAt.ShouldBeNull("a duplicated cookie never revokes the session it also carried");
        row.RotationRequiredAt.ShouldBeNull("and it never marks a rotation");
    }

    /// <summary>
    /// QA2, two <c>Cookie</c> header lines, both orders: the parser walks the header's values in arrival order,
    /// so the last line's pair is the presented credential — the same two defined outcomes as one header.
    /// </summary>
    [Fact]
    public async Task Two_Cookie_Headers_Take_The_Last_Line_And_Never_Answer_Server_Error()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var garbage = UnmintedValue();

        using (var liveWins = await SendWithTwoCookieHeadersAsync(client, garbage, member.SessionCookie))
        {
            liveWins.StatusCode.ShouldBe(
                HttpStatusCode.OK,
                "two Cookie lines are two values of one header, so the live line sent last is the one kept");
            (await SessionTestHost.BodyAsync(liveWins))
                .ShouldContain(member.OrgId.ToString(), Case.Insensitive);
        }

        using (var garbageWins = await SendWithTwoCookieHeadersAsync(client, member.SessionCookie, garbage))
        {
            garbageWins.StatusCode.ShouldBe(
                HttpStatusCode.Unauthorized,
                "the garbage line is last, so the request presents a value no row carries");
            (await SessionTestHost.ErrorCodeAsync(garbageWins)).ShouldBe(ApiErrorCode.Unauthenticated);
        }

        var row = await host.SessionRowAsync(await host.SessionIdOfAsync(member.SessionCookie));
        row.RevokedAt.ShouldBeNull("the live session survives both requests");
        row.RotationRequiredAt.ShouldBeNull();
    }

    /// <summary>
    /// QA2: the garbage value never authenticates by itself — presented alone it is a plain 401
    /// <c>unauthenticated</c>, no <c>session</c> row carries its HMAC at all, and it never becomes a second live
    /// session of the account whose cookie was paired with it.
    /// </summary>
    [Fact]
    public async Task The_Unminted_Value_Never_Authenticates_On_Its_Own()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var garbage = UnmintedValue();

        using (var alone = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            OrgsPath,
            SessionTestHost.CookieHeaderFor(garbage)))
        {
            alone.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await SessionTestHost.ErrorCodeAsync(alone)).ShouldBe(ApiErrorCode.Unauthenticated);
        }

        (await host.CountAsync(
            "select count(*) from public.session where cookie_hmac = @hmac",
            ("hmac", host.HmacOf(garbage)))).ShouldBe(0, "nothing was ever minted from that value");

        (await host.CountAsync(
            "select count(*) from public.session where user_id = @user and revoked_at is null",
            ("user", member.UserId))).ShouldBe(1, "and it did not become a second live session");
    }

    /// <summary>One <c>Cookie</c> header carrying two pairs of the session cookie name, in the given order.</summary>
    /// <param name="first">The first value.</param>
    /// <param name="second">The second value.</param>
    private static string Pairs(string first, string second) =>
        SessionTestHost.CookieHeaderFor(first) + "; " + SessionTestHost.CookieHeaderFor(second);

    /// <summary>
    /// Two requests are not expressible through <see cref="SessionTestHost.SendAsync"/> (it sets one
    /// <c>Cookie</c> header), so the request is built here: the same user agent, then one <c>Cookie</c> line per
    /// value, in the order a proxy or a hand-written client would send them.
    /// </summary>
    /// <param name="client">A client of this host.</param>
    /// <param name="first">The value of the first <c>Cookie</c> line.</param>
    /// <param name="second">The value of the second <c>Cookie</c> line.</param>
    private static async Task<HttpResponseMessage> SendWithTwoCookieHeadersAsync(
        HttpClient client,
        string first,
        string second)
    {
        ArgumentNullException.ThrowIfNull(client);

        // Awaited inside the using scope: the request must outlive the send.
        using var request = new HttpRequestMessage(HttpMethod.Get, OrgsPath);
        request.Headers.Add("User-Agent", SessionTestHost.UserAgent);
        request.Headers.TryAddWithoutValidation("Cookie", SessionTestHost.CookieHeaderFor(first))
            .ShouldBeTrue();
        request.Headers.TryAddWithoutValidation("Cookie", SessionTestHost.CookieHeaderFor(second))
            .ShouldBeTrue();

        return await client.SendAsync(request, Token).ConfigureAwait(false);
    }

    /// <summary>
    /// A base64url value of exactly the length this server mints, which no session was ever minted from: the store
    /// shape-checks the presented value before hashing it, so a shorter or longer literal would prove the shape
    /// refusal instead of the lookup.
    /// </summary>
    private static string UnmintedValue()
    {
        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        value.Length.ShouldBe(
            SessionAuthenticationStore.CookieValueLength,
            "the garbage value must have the minted shape, or the request never reaches the session lookup");

        return value;
    }
}
