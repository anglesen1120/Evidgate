using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Login;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Completion;

/// <summary>
/// 02.3.1-T1.4 / AC1 / AC3 / 02.3.1 §5 (<c>LoginFlowTests</c>): the whole callback against the real host and a
/// real database — the user/identity upsert, the active-membership check, the session row, the
/// <c>__Host-evidgate_session</c> cookie and the 302 to the allowlisted <c>returnUrl</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every case goes through the real routes and the real completion: only the provider is a fake (the real
/// provider's token validation against mocked provider endpoints is <c>GoogleProviderFlowTests</c>'),
/// so what is asserted is the deployment's own behaviour, not a memento of it.
/// </para>
/// <para>
/// The class is <c>partial</c> and <c>internal</c> on purpose: the orchestrator adds the GitHub cases in a
/// second file (<c>LoginFlowTests.GitHub.cs</c>) over the same host helper, without touching this one.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed partial class LoginFlowTests : CatalogueTestBase
{
    /// <summary>The constructor the harness's assembly fixture flows into.</summary>
    /// <param name="fixture">The shared container and migrated template.</param>
    public LoginFlowTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>
    /// AC1: a verified identity with an active membership ends with the session row, the
    /// <c>__Host-evidgate_session</c> cookie and the 302 to the allowlisted destination.
    /// </summary>
    /// <remarks>
    /// The host reports <c>127.0.0.1</c> as the client address, so the stored <c>session.ip</c> is a known
    /// value rather than one of several possibilities.
    /// </remarks>
    [Fact]
    public async Task GoogleHappyPath_EndsWith_A_Session_Row_A_Cookie_And_A_302()
    {
        using var host = LoginCompletionTestHost.Start(
            Fixture,
            IPAddress.Loopback,
            LoginCompletionTestHost.GoogleProvider());
        using var client = host.CreateLoginClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(LoginCompletionTestHost.UserAgent);

        await LoginCompletionTestHost.EstablishMemberAsync(
            Fixture,
            host,
            client,
            LoginCompletionTestHost.Google,
            LoginCompletionTestHost.GoogleSubject);

        using var completed = await host
            .SignInAsync(client, returnUrl: "/runs/42");

        completed.StatusCode.ShouldBe(HttpStatusCode.Found);
        completed.Headers.Location!.OriginalString.ShouldBe("/runs/42");

        var cookieValue = LoginCompletionTestHost.SessionCookieValue(completed);
        cookieValue.ShouldNotBeNullOrEmpty("AC1: the completed login sets the session cookie");

        var userId = await LoginCompletionTestHost
            .UserIdAsync(Fixture, LoginCompletionTestHost.Google, LoginCompletionTestHost.GoogleSubject);

        (await LoginCompletionTestHost.CountAsync(Fixture, "select count(*) from public.\"user\""))
            .ShouldBe(1);
        (await LoginCompletionTestHost.CountAsync(Fixture, "select count(*) from public.user_identity"))
            .ShouldBe(1);
        (await LoginCompletionTestHost.CountAsync(Fixture, "select count(*) from public.session"))
            .ShouldBe(1);

        var session = await LoginCompletionTestHost
            .SessionAsync(
                Fixture,
                await SessionIdAsync(userId));

        session.UserId.ShouldBe(userId);
        session.CookieHmac.Length.ShouldBe(32, "cookie_hmac is HMAC-SHA256, so exactly 32 bytes");
        session.CookieHmac.ShouldBe(
            host.CookieHmac(cookieValue!),
            "the row stores the HMAC of the cookie value under the configured pepper, never the value itself");
        session.Ip.ShouldBe("127.0.0.1", "the row stores the address the transport reported");
        session.UserAgent.ShouldBe(LoginCompletionTestHost.UserAgent);
        session.RevokedAt.ShouldBeNull();
        session.ExpiresAt.ShouldBeLessThanOrEqualTo(session.AbsoluteExpiresAt, "session_idle_deadline_check");
    }

    /// <summary>
    /// AC3: an account with no membership in any active organization is refused with 403
    /// <c>no_membership</c>, no session row and no cookie — while the user row it just created stays (§5 order).
    /// </summary>
    [Fact]
    public async Task NoMembership_Answers_403_With_No_Cookie_And_No_Session_Row()
    {
        using var host = LoginCompletionTestHost.Start(Fixture, LoginCompletionTestHost.GoogleProvider());
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInAsync(client);

        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LoginCompletionTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.NoMembership);
        LoginCompletionTestHost.SessionCookies(callback).ShouldBeEmpty("no membership means no session, so no cookie");
        (await LoginCompletionTestHost.CountAsync(Fixture, "select count(*) from public.session"))
            .ShouldBe(0);
        (await LoginCompletionTestHost.CountAsync(Fixture, "select count(*) from public.\"user\""))
            .ShouldBe(1, "02.3.1 §5: the user/identity rows written before the membership check stay");
        (await LoginCompletionTestHost.CountAsync(Fixture, "select count(*) from public.user_identity"))
            .ShouldBe(1);
    }

    /// <summary>AC3: a disabled account is refused with 403 <c>account_disabled</c>, no session and no cookie.</summary>
    [Fact]
    public async Task Disabled_User_Answers_403_With_Account_Disabled_And_No_Session()
    {
        using var host = LoginCompletionTestHost.Start(Fixture, LoginCompletionTestHost.GoogleProvider());
        using var client = host.CreateLoginClient();

        await LoginCompletionTestHost.EstablishMemberAsync(
            Fixture,
            host,
            client,
            LoginCompletionTestHost.Google,
            LoginCompletionTestHost.GoogleSubject);

        var userId = await LoginCompletionTestHost
            .UserIdAsync(Fixture, LoginCompletionTestHost.Google, LoginCompletionTestHost.GoogleSubject);

        // Disabling is the owner's act (02.3.1 §7 later stories); the harness owner connection is that operator.
        (await Fixture.SeedAsync(
            "update public.\"user\" set status = 'disabled', updated_at = now() where id = @id",
            [("id", userId)],
            LoginCompletionTestHost.Token)).ShouldBe(1);

        using var callback = await host.SignInAsync(client);

        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LoginCompletionTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.AccountDisabled);
        LoginCompletionTestHost.SessionCookies(callback).ShouldBeEmpty();
        (await LoginCompletionTestHost.CountAsync(Fixture, "select count(*) from public.session"))
            .ShouldBe(0);
    }

    /// <summary>AC3: a membership only in a suspended organization is no membership at all.</summary>
    [Fact]
    public async Task Membership_In_A_Suspended_Org_Answers_No_Membership()
    {
        using var host = LoginCompletionTestHost.Start(Fixture, LoginCompletionTestHost.GoogleProvider());
        using var client = host.CreateLoginClient();

        var orgId = await LoginCompletionTestHost.EstablishMemberAsync(
            Fixture,
            host,
            client,
            LoginCompletionTestHost.Google,
            LoginCompletionTestHost.GoogleSubject);

        (await Fixture.SeedAsync(
            "update public.organization set status = 'suspended', updated_at = now() where id = @id",
            [("id", orgId)],
            LoginCompletionTestHost.Token)).ShouldBe(1);

        using var callback = await host.SignInAsync(client);

        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LoginCompletionTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.NoMembership);
        LoginCompletionTestHost.SessionCookies(callback).ShouldBeEmpty();
        (await LoginCompletionTestHost.CountAsync(Fixture, "select count(*) from public.session"))
            .ShouldBe(0);
    }

    /// <summary>
    /// 02.3.1 §8: the one-time <c>state</c> cannot be replayed — the second presentation of the very same
    /// callback is 401 <c>token_invalid</c> and the account still holds exactly one session.
    /// </summary>
    [Fact]
    public async Task Replayed_Callback_Answers_401_And_Keeps_One_Session()
    {
        using var host = LoginCompletionTestHost.Start(Fixture, LoginCompletionTestHost.GoogleProvider());
        using var client = host.CreateLoginClient();

        await LoginCompletionTestHost.EstablishMemberAsync(
            Fixture,
            host,
            client,
            LoginCompletionTestHost.Google,
            LoginCompletionTestHost.GoogleSubject);

        using var start = await client
            .SendAsync(
                LoginCompletionTestHost.StartRequest(LoginCompletionTestHost.Google),
                LoginCompletionTestHost.Token);
        var (payload, cookieValue) = host.PendingLogin(start);

        using var first = await client
            .SendAsync(
                LoginCompletionTestHost.CallbackRequest(
                    LoginCompletionTestHost.Google,
                    cookieValue,
                    payload.State),
                LoginCompletionTestHost.Token);
        first.StatusCode.ShouldBe(HttpStatusCode.Found);

        using var replay = await client
            .SendAsync(
                LoginCompletionTestHost.CallbackRequest(
                    LoginCompletionTestHost.Google,
                    cookieValue,
                    payload.State),
                LoginCompletionTestHost.Token);

        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginCompletionTestHost.ErrorCodeAsync(replay)).ShouldBe(ApiErrorCode.TokenInvalid);
        LoginCompletionTestHost.SessionCookies(replay).ShouldBeEmpty();
        (await LoginCompletionTestHost.CountAsync(Fixture, "select count(*) from public.session"))
            .ShouldBe(1);
    }

    /// <summary>
    /// Hand-over (vi) of #160: <see cref="ReturnUrlPolicy"/> accepts non-ASCII path characters, so the redirect
    /// must encode them — a valid ASCII <c>Location</c>, never a throw out of Kestrel.
    /// </summary>
    [Fact]
    public async Task NonAscii_ReturnUrl_Redirects_With_An_Ascii_Location()
    {
        using var host = LoginCompletionTestHost.Start(Fixture, LoginCompletionTestHost.GoogleProvider());
        using var client = host.CreateLoginClient();

        await LoginCompletionTestHost.EstablishMemberAsync(
            Fixture,
            host,
            client,
            LoginCompletionTestHost.Google,
            LoginCompletionTestHost.GoogleSubject);

        using var completed = await host.SignInAsync(client, returnUrl: "/runs/café");

        completed.StatusCode.ShouldBe(HttpStatusCode.Found);
        var location = completed.Headers.Location!.OriginalString;
        location.ShouldBe("/runs/caf%C3%A9");
        location.All(character => character < 128).ShouldBeTrue("a header value must be ASCII");
        LoginCompletionTestHost.SessionCookies(completed).Count.ShouldBe(1);
    }

    /// <summary>
    /// Hand-over (vi) of #160: an IPv6 client address reaches <c>session.ip</c> without its zone id, so the
    /// <c>inet</c> column never rejects the row.
    /// </summary>
    [Fact]
    public async Task Ipv6_Client_Address_Is_Stored_Without_The_Zone_Id()
    {
        using var host = LoginCompletionTestHost.Start(
            Fixture,
            IPAddress.Parse("fe80::1%6"),
            LoginCompletionTestHost.GoogleProvider());
        using var client = host.CreateLoginClient();

        await LoginCompletionTestHost.EstablishMemberAsync(
            Fixture,
            host,
            client,
            LoginCompletionTestHost.Google,
            LoginCompletionTestHost.GoogleSubject);

        using var completed = await host.SignInAsync(client);
        completed.StatusCode.ShouldBe(HttpStatusCode.Found);

        var userId = await LoginCompletionTestHost
            .UserIdAsync(Fixture, LoginCompletionTestHost.Google, LoginCompletionTestHost.GoogleSubject);
        var session = await LoginCompletionTestHost
            .SessionAsync(Fixture, await SessionIdAsync(userId));

        session.Ip.ShouldBe("fe80::1");
    }

    /// <summary>
    /// 02.3.1 §5/P5: a database fault in the completion is 503 <c>unavailable</c> with <c>Retry-After</c>, and
    /// nothing of the database's own text reaches the body.
    /// </summary>
    [Fact]
    public async Task Database_Fault_Answers_503_With_Retry_After_And_No_Database_Text()
    {
        using var host = LoginCompletionTestHost.StartWithoutDatabase(LoginCompletionTestHost.GoogleProvider());
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInAsync(client);

        callback.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await LoginCompletionTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.Unavailable);
        callback.Headers.RetryAfter.ShouldNotBeNull();
        callback.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBe(30);

        var body = await LoginCompletionTestHost.BodyAsync(callback);
        body.ShouldNotContain("Npgsql", Case.Insensitive);
        body.ShouldNotContain("Failed to connect", Case.Insensitive);
        body.ShouldNotContain("127.0.0.1");
        body.ShouldNotContain("evidgate_app");
    }

    /// <summary>AC1: a second login of the same identity reuses the same user and adds a second session.</summary>
    [Fact]
    public async Task Second_Login_Of_The_Same_Identity_Reuses_The_User()
    {
        using var host = LoginCompletionTestHost.Start(Fixture, LoginCompletionTestHost.GoogleProvider());
        using var client = host.CreateLoginClient();

        await LoginCompletionTestHost.EstablishMemberAsync(
            Fixture,
            host,
            client,
            LoginCompletionTestHost.Google,
            LoginCompletionTestHost.GoogleSubject);

        using var first = await host.SignInAsync(client);
        first.StatusCode.ShouldBe(HttpStatusCode.Found);

        var userId = await LoginCompletionTestHost
            .UserIdAsync(Fixture, LoginCompletionTestHost.Google, LoginCompletionTestHost.GoogleSubject);

        using var second = await host.SignInAsync(client);
        second.StatusCode.ShouldBe(HttpStatusCode.Found);

        (await LoginCompletionTestHost
                .UserIdAsync(Fixture, LoginCompletionTestHost.Google, LoginCompletionTestHost.GoogleSubject))
            .ShouldBe(userId, "the (provider, subject) binding is the identity, so the same person is one row");
        (await LoginCompletionTestHost.CountAsync(Fixture, "select count(*) from public.\"user\""))
            .ShouldBe(1);
        (await LoginCompletionTestHost.CountAsync(Fixture, "select count(*) from public.user_identity"))
            .ShouldBe(1);
        (await LoginCompletionTestHost.CountAsync(
                Fixture,
                "select count(*) from public.session where revoked_at is null"))
            .ShouldBe(2);

        LoginCompletionTestHost.SessionCookieValue(first).ShouldNotBe(LoginCompletionTestHost.SessionCookieValue(second));
    }

    /// <summary>The single session row's id for <paramref name="userId"/>, read as the owner.</summary>
    private Task<Guid> SessionIdAsync(Guid userId) =>
        LoginCompletionTestHost.OwnerScalarAsync<Guid>(
            Fixture,
            "select id from public.session where user_id = @user_id order by created_at desc limit 1",
            [("user_id", userId)]);
}
