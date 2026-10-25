using System.Net;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Login.Linking;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// 02.3.2-T1 AC5 (T38, 00 §9.1) over the REAL host: a request whose authority is the
/// <c>__Host-evidgate_session</c> cookie and whose method mutates must carry an <c>X-XSRF-TOKEN</c> header that
/// validates against the pair bound to THAT session — otherwise it answers 403 <c>csrf_failed</c> and no handler
/// runs. A bearer request (an <c>Authorization</c> header) is never cookie-authenticated and is therefore outside
/// the rule; safe methods are outside it by definition; and the one documented exemption
/// (<c>POST /v1/auth/invite-token</c>, which SETS a cookie instead of authenticating with one) is exercised here.
/// </summary>
/// <remarks>
/// <para>
/// The route the positive cases drive is <c>DELETE /v1/auth/link/pending</c>: it is cookie-authenticated, it
/// mutates, it needs no body, and its success is observable without arranging anything else (204 plus the
/// deletion of the staged-link cookie). A refusal therefore has to be the antiforgery refusal — an assertion on a
/// route whose success needed rows would be satisfied by a 404 of the wrong thing.
/// </para>
/// <para>
/// The pair is the one the deployment issued: the sign-in response's own <c>XSRF-TOKEN</c> and
/// <c>__Host-evidgate_af</c> cookies (D6), never a token the test minted. That is what makes "a token issued for
/// another session" meaningful — the pair is bound to the session id inside the token.
/// </para>
/// </remarks>
/// <param name="fixture">The migrated harness database.</param>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class XsrfTests(DatabaseFixture fixture) : HarnessTest(fixture)
{
    /// <summary>The cookie-authenticated mutation the positive cases drive (02.3.1-T2's <c>DELETE pending</c>).</summary>
    private const string PendingPath = "/v1/auth/link/pending";

    /// <summary>AC5, negative: a mutation carrying the session cookie and no <c>X-XSRF-TOKEN</c> is 403.</summary>
    [Fact]
    public async Task A_Mutation_Without_The_Header_Is_403_CsrfFailed()
    {
        await using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        using var response = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(member.SessionCookie));

        await AssertCsrfRefusedAsync(response);

        // No handler ran: the route's only write is the staged-link cookie's deletion, and a request the pair
        // refused never reached it.
        SessionTestHost.Cookies(response, LinkConfirmCookie.Name).ShouldBeEmpty(
            "a refused request must not run the handler that deletes the staged link");
    }

    /// <summary>
    /// AC5, boundary: the SESSION and the PAIR COOKIES without the header. The pair is present and correct as a
    /// cookie; only the double-submit half is missing, so this is the shape a script-less cross-site form post
    /// cannot supply.
    /// </summary>
    [Fact]
    public async Task A_Mutation_With_The_Pair_Cookies_But_No_Header_Is_403_CsrfFailed()
    {
        await using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        using var response = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(member.SessionCookie, member.Xsrf));

        await AssertCsrfRefusedAsync(response);
    }

    /// <summary>
    /// AC5, negative: a header that does not match the pair (a mangled token, a header from another pair, a
    /// header with no pair cookie at all) is one refusal — the four shapes are indistinguishable to the client.
    /// </summary>
    [Fact]
    public async Task Any_Header_That_Does_Not_Match_The_Pair_Is_403_CsrfFailed()
    {
        await using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        // The minted token with one byte changed: the token's own integrity check fails.
        using var mangled = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(member.SessionCookie, member.Xsrf),
            xsrfToken: Mangle(member.Xsrf.RequestToken));
        await AssertCsrfRefusedAsync(mangled);

        // A header with no pair cookie at all: nothing to validate the request token against.
        using var headerOnly = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(member.SessionCookie),
            xsrfToken: member.Xsrf.RequestToken);
        await AssertCsrfRefusedAsync(headerOnly);

        // The right XSRF cookie but NO antiforgery cookie: the two halves of the pair belong together, and a
        // request token whose cookie-token half is missing fails validation.
        using var halfPair = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(member.SessionCookie)
                + "; " + SessionTestHost.XsrfCookieName + "=" + member.Xsrf.RequestToken,
            xsrfToken: member.Xsrf.RequestToken);
        await AssertCsrfRefusedAsync(halfPair);
    }

    /// <summary>
    /// AC5, negative and the reason the pair carries additional data: a token ISSUED FOR ANOTHER SESSION does
    /// not validate against this one, even though it is a well-formed pair the deployment minted (T38's two-tab
    /// window, and the window a rotation closes).
    /// </summary>
    [Fact]
    public async Task A_Token_Issued_For_Another_Session_Is_403_CsrfFailed()
    {
        await using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var first = await host.SignInMemberAsync(client, SessionTestHost.Google);
        var second = await host.SignInMemberAsync(client, SessionTestHost.GitHub);

        first.SessionCookie.ShouldNotBe(second.SessionCookie);

        // Session A's cookie with session B's pair, complete and untouched.
        using var response = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(first.SessionCookie, second.Xsrf),
            xsrfToken: second.Xsrf.RequestToken);

        await AssertCsrfRefusedAsync(response);
    }

    /// <summary>AC5, positive: the matching pair the deployment issued lets the mutation through.</summary>
    [Fact]
    public async Task The_Matching_Pair_Passes()
    {
        await using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        using var response = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(member.SessionCookie, member.Xsrf),
            xsrfToken: member.Xsrf.RequestToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, "a validated pair runs the handler");
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBeNull();
        SessionTestHost.Cookies(response, LinkConfirmCookie.Name).ShouldNotBeEmpty(
            "the handler ran: it clears the staged-link cookie");
    }

    /// <summary>
    /// AC5 / T38, positive: a request carrying an <c>Authorization</c> header is never cookie-authenticated, so
    /// the antiforgery rule does not apply to it — and the session cookie it also carries buys it nothing. Both
    /// observations are one case: with and without a valid pair the answer is the session's own 401, never
    /// 403 <c>csrf_failed</c> and never a handler run.
    /// </summary>
    [Fact]
    public async Task A_Bearer_Request_Is_Exempt_And_Its_Cookie_Buys_Nothing()
    {
        await using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        const string bearer = "Bearer not-a-token-this-host-issues";

        using var withoutPair = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(member.SessionCookie, member.Xsrf),
            authorization: bearer);

        withoutPair.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "a bearer request abstains from the cookie");
        (await SessionTestHost.ErrorCodeAsync(withoutPair)).ShouldBe(ApiErrorCode.Unauthenticated);
        SessionTestHost.Cookies(withoutPair, LinkConfirmCookie.Name).ShouldBeEmpty("no handler ran");

        // Even a perfectly valid pair does not turn the bearer request into a cookie-authenticated one: the
        // session authenticator abstains on the header alone, before it reads the cookie.
        using var withPair = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(member.SessionCookie, member.Xsrf),
            xsrfToken: member.Xsrf.RequestToken,
            authorization: bearer);

        withPair.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(withPair)).ShouldBe(ApiErrorCode.Unauthenticated);
    }

    /// <summary>AC5: a safe method is outside the rule — a GET with the session and no header is served.</summary>
    [Fact]
    public async Task A_Get_Is_Not_Affected()
    {
        await using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        using var response = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            PendingPath,
            cookieHeader: SessionTestHost.CookieHeaderFor(member.SessionCookie));

        // The handler ran (it answered its own 404 not_found: nothing is staged for this session) and the
        // request really reached that route.
        host.Recorder.PatternFor(HttpMethod.Get.Method, PendingPath).ShouldNotBeNull(
            "the read must reach the route it asserts about");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a read needs no antiforgery pair");
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(
            ApiErrorCode.NotFound,
            "the read was served by its handler, never refused by the antiforgery filter");
    }

    /// <summary>
    /// AC5: the three <c>/v1/auth/link/*</c> mutations 02.3.1-T2 declared the header for are covered by the
    /// filter — the hand-over note of 02.3.2 §10 (they were contract-only before this task).
    /// </summary>
    /// <param name="method">The mutation's verb.</param>
    /// <param name="path">Its path.</param>
    [Theory]
    [InlineData("POST", "/v1/auth/link/google")]
    [InlineData("POST", "/v1/auth/link/confirm")]
    [InlineData("DELETE", "/v1/auth/link/pending")]
    public async Task The_Three_Link_Mutations_Require_The_Pair(string method, string path)
    {
        await using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        using var response = await SessionTestHost.SendAsync(
            client,
            new HttpMethod(method),
            path,
            cookieHeader: SessionTestHost.CookieHeaderFor(member.SessionCookie));

        await AssertCsrfRefusedAsync(response);
    }

    /// <summary>
    /// AC5: <c>POST /v1/auth/invite-token</c> is the ONE documented exemption — it SETS a cookie rather than
    /// authenticating with one, so a member who already holds a live session is not refused as
    /// <c>csrf_failed</c>; the route's own body contract is what defends it.
    /// </summary>
    [Fact]
    public async Task The_Invite_Token_Route_Is_Exempt()
    {
        await using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/invite-token");
        request.Headers.Add("Cookie", SessionTestHost.CookieHeaderFor(member.SessionCookie));
        request.Content = new StringContent("""{"invite_token":"not-a-shaped-token"}""", Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, Token);

        // The exemption is proven by the handler having judged the body: a 422 from the route's own contract,
        // never the antiforgery refusal that stands in front of it.
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.RequestInvalid);
    }

    /// <summary>Asserts the 00 §9.2 antiforgery refusal: 403 with <c>error.code = csrf_failed</c>.</summary>
    /// <param name="response">The refusal.</param>
    private static async Task AssertCsrfRefusedAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.CsrfFailed);
    }

    /// <summary>One byte of the token replaced, so the token is well-formed text but fails its own integrity check.</summary>
    /// <param name="token">The minted request token.</param>
    private static string Mangle(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);

        var replacement = token[0] == 'A' ? 'B' : 'A';

        return replacement + token[1..];
    }
}
