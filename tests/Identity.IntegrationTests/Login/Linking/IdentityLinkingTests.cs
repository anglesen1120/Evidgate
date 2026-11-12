using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Audit;
using Evidgate.Modules.Identity.Login.Linking;
using Evidgate.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Linking;

/// <summary>
/// The explicit second-provider link of 02.3.1-T2.2 (decision Q4, AC6) against the real database: the four
/// <c>/v1/auth/link/*</c> routes, the staged <c>__Host-evidgate_link</c> cookie, the database's
/// <c>linked</c>/<c>already_linked</c>/<c>moved</c>/<c>conflict</c> decision, and the audit rows of the
/// confirmation.
/// </summary>
/// <remarks>
/// <para>
/// Every case drives the routes over HTTP: <c>POST /v1/auth/link/{provider}</c> on a live session, the shared
/// callback with a faked second provider, then <c>pending</c>/<c>confirm</c> with the cookies a browser would
/// send. Nothing is written before the explicit confirm, so the two halves of AC6 — "a second provider never
/// merges by email" and "a link happens only when the human confirms" — are both asserted on rows.
/// </para>
/// <para>
/// The first sign-in of a provider is the 403 <c>no_membership</c> path of 02.1.2-T2: it creates the account and
/// its identity row without minting a session, which is how a case arranges "an account with this identity
/// already exists". A membership seeded through the harness's owner seam is what then mints the session.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed partial class IdentityLinkingTests : TenantTest
{
    /// <summary>The address both providers verify, which is the whole point of AC6: it never merges.</summary>
    private const string Email = "linked-member@example.test";

    /// <summary>Google's <c>sub</c> for the primary account.</summary>
    private const string GoogleSubject = "google-subject-0001";

    /// <summary>GitHub's numeric user id for the second provider.</summary>
    private const string GitHubSubject = "149272102";

    /// <summary>
    /// The address the second provider verifies for the MOVE cases: a different mailbox from <see cref="Email"/>,
    /// so the source account is a distinct person-shaped account that holds nothing.
    /// </summary>
    private const string MovableEmail = "movable@example.test";

    // Own database: several cases count every user_identity row, which a shared database could not
    // isolate from another class running at the same time.
    public IdentityLinkingTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>
    /// The non-vacuity probe of the log-capture case: one Warning line written through the provider under test, as a
    /// <see cref="LoggerMessage"/> delegate because a bare <c>LogWarning</c> call is CA1848 at error severity.
    /// </summary>
    private static readonly Action<ILogger, string, Exception?> CaptureProbe =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(CaptureProbe)), "probe {Text}");

    // ------------------------------------------------------------------ AC6: never merge by email

    /// <summary>
    /// AC6: the same verified address through two providers is two accounts with two identity rows. Nothing
    /// merges on email, and neither account gains the other's provider.
    /// </summary>
    [Fact]
    public async Task TwoProvidersNoMerge_SameVerifiedEmail_CreatesTwoAccounts()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, Email));
        using var client = host.CreateLinkClient();

        foreach (var provider in new[] { LinkTestHost.Google, LinkTestHost.GitHub })
        {
            using var first = await host.SignInAsync(client, provider);

            // AC3 of 02.1.2: a verified sign-in that joins no org establishes no session.
            first.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await LinkTestHost.ErrorCodeAsync(first)).ShouldBe(ApiErrorCode.NoMembership);
        }

        var googleUser = (await host.UserIdAsync(LinkTestHost.Google, GoogleSubject))
            ?? throw new InvalidOperationException("the google sign-in created no identity");
        var githubUser = (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject))
            ?? throw new InvalidOperationException("the github sign-in created no identity");

        // The point of the story: one verified address, two accounts, and no merge.
        googleUser.ShouldNotBe(githubUser);
        (await host.CountAsync("select count(*) from public.\"user\" where email = @email", ("email", Email))).ShouldBe(2L);
        (await host.CountAsync("select count(*) from public.user_identity")).ShouldBe(2L);
        (await host.CountAsync(
            "select count(*) from public.user_identity where user_id = @id",
            ("id", googleUser))).ShouldBe(1L);
        (await host.CountAsync(
            "select count(*) from public.user_identity where user_id = @id",
            ("id", githubUser))).ShouldBe(1L);
    }

    // ------------------------------------------------------------------ the explicit confirm, end to end

    /// <summary>
    /// The whole confirm path: start on the session, the callback stages the verified identity, <c>pending</c>
    /// shows it, <c>confirm</c> attaches it to the session user, and the next sign-in through that provider lands
    /// on the SAME account.
    /// </summary>
    [Fact]
    public async Task Confirm_EndToEnd_AttachesTheProviderAndTheNextSignInLandsOnTheSameUser()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, Email));
        using var client = host.CreateLinkClient();

        var (userId, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);

        var start = await StartLinkAsync(host, client, LinkTestHost.GitHub, session);
        using var startResponse = start.Response;
        startResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LinkTestHost.MemberAsync(startResponse, "authorization_url")).ShouldNotBeNull();

        // The state is the link's own: a sign-in start would answer "login" and stage a session on completion.
        start.Payload.Purpose.ShouldBe(LoginStatePayload.PurposeLink);
        start.Payload.LinkUserId.ShouldBe(userId);

        using var callback = await LinkTestHost.LinkCallbackAsync(
            client,
            LinkTestHost.GitHub,
            start.StateCookie,
            start.Payload.State,
            session);
        callback.StatusCode.ShouldBe(HttpStatusCode.Found);

        var staged = host.StagedLink(callback).ShouldNotBeNull();
        staged.UserId.ShouldBe(userId);
        staged.Provider.ShouldBe(LinkTestHost.GitHub);
        staged.Subject.ShouldBe(GitHubSubject);
        staged.Email.ShouldBe(Email);

        var linkCookie = LinkTestHost.CookieValue(callback, LinkConfirmCookie.Name)
            ?? throw new InvalidOperationException("the callback staged no link cookie");

        using var pending = await PendingAsync(client, session, linkCookie);
        pending.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LinkTestHost.MemberAsync(pending, "provider")).ShouldBe(LinkTestHost.GitHub);
        (await LinkTestHost.MemberAsync(pending, "email")).ShouldBe(Email);
        (await LinkTestHost.MemberAsync(pending, "display_name")).ShouldBe("Test Member");
        (await LinkTestHost.MemberAsync(pending, "expires_at")).ShouldNotBeNullOrEmpty();

        // Nothing is written before this call: the pending read above staged nothing.
        (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject)).ShouldBeNull();

        using var confirm = await ConfirmAsync(client, session, linkCookie);
        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LinkTestHost.MemberAsync(confirm, "provider")).ShouldBe(LinkTestHost.GitHub);
        (await LinkTestHost.MemberAsync(confirm, "outcome")).ShouldBe("linked");

        // The success answers with the staged cookie deleted, so the browser has nothing left to confirm with.
        LinkTestHost.CookieValue(confirm, LinkConfirmCookie.Name).ShouldBe(string.Empty);

        (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject)).ShouldBe(userId);
        (await host.CountAsync("select count(*) from public.user_identity")).ShouldBe(2L);

        // AC6: the second sign-in through that provider is the same human.
        using var signIn = await host.SignInAsync(client, LinkTestHost.GitHub);
        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);
        (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject)).ShouldBe(userId);
        (await host.CountAsync("select count(*) from public.user_identity")).ShouldBe(2L);
        LinkTestHost.SessionCookieValue(signIn).ShouldNotBeNullOrEmpty();
    }

    /// <summary>
    /// A confirm that carries the session cookies but no <c>X-XSRF-TOKEN</c> is refused as <c>csrf_failed</c> before any
    /// link runs: the cookies alone do not authorise a mutation (00 §9.1, T38).
    /// </summary>
    [Fact]
    public async Task Confirm_WithoutTheRequestToken_Answers403CsrfFailedAndWritesNothing()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, Email));
        using var client = host.CreateLinkClient();

        var (userId, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);
        var linkCookie = await StageAsync(host, client, LinkTestHost.GitHub, session);

        // The session and its antiforgery cookie are sent; the header the page echoes is the one left out.
        using var confirm = await LinkTestHost.SendAsync(
            client,
            HttpMethod.Post,
            "/link/confirm",
            session.Cookies(linkCookie));

        confirm.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LinkTestHost.ErrorCodeAsync(confirm)).ShouldBe("csrf_failed");
        (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject)).ShouldBeNull();
        (await host.CountAsync(
            "select count(*) from public.user_identity where user_id = @id",
            ("id", userId))).ShouldBe(1L);
    }

    /// <summary>
    /// A confirm on a session a privilege change flagged for rotation (02.3.2 D5, D8): the request's own pair authorises
    /// the mutation, the authenticator rotates the session once, and the link resolves on the successor by the proof of
    /// its new cookie. The browser receives the successor's cookie and the predecessor is dead afterwards.
    /// </summary>
    [Fact]
    public async Task Confirm_OnAFlaggedSession_RotatesOnceAndLinksOnTheSuccessor()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, Email));
        using var client = host.CreateLinkClient();

        var (userId, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);
        var linkCookie = await StageAsync(host, client, LinkTestHost.GitHub, session);

        // A privilege change on this user flags the live session for rotation (migration 0045's column).
        await host.ArrangeAsync(
            "update public.session set rotation_required_at = now() where user_id = @id",
            ("id", userId));

        using var confirm = await ConfirmAsync(client, session, linkCookie);
        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LinkTestHost.MemberAsync(confirm, "outcome")).ShouldBe("linked");

        // The browser receives the successor's cookie, which is not the one it presented.
        var successor = LinkTestHost.SessionCookieValue(confirm).ShouldNotBeNull();
        successor.ShouldNotBe(session.Session);

        // Exactly one live session remains for the user: the successor. The predecessor was revoked by the rotation.
        (await host.CountAsync(
            "select count(*) from public.session where user_id = @id and revoked_at is null and expires_at > now() and absolute_expires_at > now()",
            ("id", userId))).ShouldBe(1L);
        (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject)).ShouldBe(userId);
    }

    // ------------------------------------------------------------------ start: who may begin

    /// <summary>A start without a session is the 00 §9.2 401, not a redirect to the provider.</summary>
    [Fact]
    public async Task Start_WithoutASession_Answers401()
    {
        using var host = MinimalHost();
        using var client = host.CreateLinkClient();

        using var response = await LinkTestHost.SendAsync(client, HttpMethod.Post, "/link/github", []);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LinkTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.Unauthenticated);
        LinkTestHost.CookieValue(response, LoginStateCookie.Name).ShouldBeNull();
    }

    /// <summary>A revoked session is refused as <c>session_revoked</c>, and no state is written.</summary>
    [Fact]
    public async Task Start_WithARevokedSession_Answers401()
    {
        using var host = MinimalHost();
        using var client = host.CreateLinkClient();

        var (_, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);
        await host.ArrangeAsync("update public.session set revoked_at = now()");

        using var response = await LinkTestHost.SendAsync(
            client,
            HttpMethod.Post,
            "/link/github",
            session);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LinkTestHost.ErrorCodeAsync(response)).ShouldBe("session_revoked");
        LinkTestHost.CookieValue(response, LoginStateCookie.Name).ShouldBeNull();
    }

    /// <summary>An expired session is refused as <c>session_expired</c>, and no state is written.</summary>
    [Fact]
    public async Task Start_WithAnExpiredSession_Answers401()
    {
        using var host = MinimalHost();
        using var client = host.CreateLinkClient();

        var (_, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);
        await host.ArrangeAsync(
            "update public.session set expires_at = now() - interval '1 minute'");

        using var response = await LinkTestHost.SendAsync(
            client,
            HttpMethod.Post,
            "/link/github",
            session);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LinkTestHost.ErrorCodeAsync(response)).ShouldBe("session_expired");
    }

    /// <summary>
    /// A disabled account is refused as <c>account_disabled</c> (403), the authenticator's own answer for a
    /// non-active user. It is an authorization verdict, not a credential one, so it is not hidden as a 401.
    /// </summary>
    [Fact]
    public async Task Start_ForADisabledAccount_Answers403AccountDisabled()
    {
        using var host = MinimalHost();
        using var client = host.CreateLinkClient();

        var (userId, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);
        await host.ArrangeAsync("update public.\"user\" set status = 'disabled' where id = @id", ("id", userId));

        using var response = await LinkTestHost.SendAsync(
            client,
            HttpMethod.Post,
            "/link/github",
            session);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LinkTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.AccountDisabled);
        LinkTestHost.CookieValue(response, LoginStateCookie.Name).ShouldBeNull();
    }

    /// <summary>An unknown provider is the login's 404 <c>not_found</c>, before any cookie is staged.</summary>
    [Fact]
    public async Task Start_WithAnUnknownProvider_Answers404()
    {
        using var host = MinimalHost();
        using var client = host.CreateLinkClient();

        var (_, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);

        using var response = await LinkTestHost.SendAsync(
            client,
            HttpMethod.Post,
            "/link/gitlab",
            session);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await LinkTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.NotFound);
        LinkTestHost.CookieValue(response, LoginStateCookie.Name).ShouldBeNull();
    }

    // ------------------------------------------------------------------ the callback: the session must be the same user

    /// <summary>A callback of a link state with no session stages nothing and answers 401.</summary>
    [Fact]
    public async Task Callback_OfALinkStateWithoutASession_Answers401AndStagesNothing()
    {
        using var host = MinimalHost();
        using var client = host.CreateLinkClient();

        var (userId, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);

        var start = await StartLinkAsync(host, client, LinkTestHost.GitHub, session);
        using var startResponse = start.Response;

        using var callback = await LinkTestHost.LinkCallbackAsync(
            client,
            LinkTestHost.GitHub,
            start.StateCookie,
            start.Payload.State,
            session: null);

        callback.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LinkTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.Unauthenticated);
        LinkTestHost.CookieValue(callback, LinkConfirmCookie.Name).ShouldBeNull();
        (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject)).ShouldBeNull();
        (await host.CountAsync("select count(*) from public.user_identity where user_id = @id", ("id", userId))).ShouldBe(1L);
    }

    /// <summary>
    /// A callback whose session resolves to another user than the state's <c>LinkUserId</c> stages nothing: the
    /// staged identity must belong to the account the link was started for.
    /// </summary>
    [Fact]
    public async Task Callback_WithAnotherUsersSession_Answers401AndStagesNothing()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, "second@example.test"));
        using var client = host.CreateLinkClient();

        var (_, sessionA, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);
        var (githubUser, sessionB, _) = await MemberAsync(host, client, LinkTestHost.GitHub, GitHubSubject);
        sessionB.ShouldNotBe(sessionA);

        // Started by A, completed in B's browser: the state names A, the session is B's.
        var start = await StartLinkAsync(host, client, LinkTestHost.GitHub, sessionA);
        using var startResponse = start.Response;
        start.Payload.LinkUserId.ShouldNotBe(githubUser);

        using var callback = await LinkTestHost.LinkCallbackAsync(
            client,
            LinkTestHost.GitHub,
            start.StateCookie,
            start.Payload.State,
            sessionB);

        callback.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LinkTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.Unauthenticated);
        LinkTestHost.CookieValue(callback, LinkConfirmCookie.Name).ShouldBeNull();
        (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject)).ShouldBe(githubUser);
    }

    /// <summary>
    /// An identity the <c>user_identity</c> columns cannot store is refused AT STAGING: a subject over the 255
    /// characters of <c>user_identity_subject_length_check</c> or an address over the 320 of
    /// <c>user_identity_email_at_provider_length_check</c> (0041) answers 401 <c>token_invalid</c>, stages no
    /// cookie and writes nothing — instead of staging an identity whose every confirm would be a 23514 answered
    /// 503 and retried forever (F2).
    /// </summary>
    /// <param name="subjectLength">The subject length the fake provider reports.</param>
    /// <param name="emailLength">The address length it reports.</param>
    [Theory]
    [InlineData(256, 24)]
    [InlineData(15, 321)]
    public async Task Callback_OfAnIdentityTheColumnsCannotStore_Answers401AndStagesNothing(
        int subjectLength,
        int emailLength)
    {
        var subject = new string('s', subjectLength);
        var address = new string('a', emailLength) + "@example.test";

        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, address));
        using var client = host.CreateLinkClient();

        var (userId, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);

        host.Use(LinkTestHost.GitHub, LinkTestHost.Identity(LinkTestHost.GitHub, subject, address));

        var start = await StartLinkAsync(host, client, LinkTestHost.GitHub, session);
        using var startResponse = start.Response;

        using var callback = await LinkTestHost.LinkCallbackAsync(
            client,
            LinkTestHost.GitHub,
            start.StateCookie,
            start.Payload.State,
            session);

        callback.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LinkTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.TokenInvalid);
        LinkTestHost.CookieValue(callback, LinkConfirmCookie.Name).ShouldBeNull();

        // Nothing was staged, so nothing can be confirmed: the account still holds its one identity.
        (await host.CountAsync("select count(*) from public.user_identity where user_id = @id", ("id", userId))).ShouldBe(1L);
        (await host.CountAsync(
            "select count(*) from public.user_identity where provider = @provider",
            ("provider", LinkTestHost.GitHub))).ShouldBe(0L);
    }
}
