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
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class IdentityLinkingTests : HarnessTest
{
    /// <summary>The address both providers verify, which is the whole point of AC6: it never merges.</summary>
    private const string Email = "linked-member@example.test";

    /// <summary>Google's <c>sub</c> for the primary account.</summary>
    private const string GoogleSubject = "google-subject-0001";

    /// <summary>GitHub's numeric user id for the second provider.</summary>
    private const string GitHubSubject = "149272102";

    public IdentityLinkingTests(DatabaseFixture fixture)
        : base(fixture)
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

    /// <summary>A revoked session resolves to no user, so the start is refused and no state is written.</summary>
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
            [(LinkTestHost.SessionCookieName, session)]);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LinkTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.Unauthenticated);
        LinkTestHost.CookieValue(response, LoginStateCookie.Name).ShouldBeNull();
    }

    /// <summary>An expired session is refused the same way (the resolver filters on the deadline).</summary>
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
            [(LinkTestHost.SessionCookieName, session)]);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LinkTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.Unauthenticated);
    }

    /// <summary>
    /// A disabled account cannot start a link. The answer is the session's own 401 rather than
    /// <c>account_disabled</c>: <c>evidgate_resolve.session_user</c> filters on <c>user.status = 'active'</c>, so a
    /// disabled user is indistinguishable from no session, which is also the account-enumeration-safe answer.
    /// </summary>
    [Fact]
    public async Task Start_ForADisabledAccount_Answers401LikeNoSession()
    {
        using var host = MinimalHost();
        using var client = host.CreateLinkClient();

        var (userId, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);
        await host.ArrangeAsync("update public.\"user\" set status = 'disabled' where id = @id", ("id", userId));

        using var response = await LinkTestHost.SendAsync(
            client,
            HttpMethod.Post,
            "/link/github",
            [(LinkTestHost.SessionCookieName, session)]);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LinkTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.Unauthenticated);
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
            [(LinkTestHost.SessionCookieName, session)]);

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
            sessionCookie: null);

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

    // ------------------------------------------------------------------ pending / confirm guards

    /// <summary>Confirm without a staged cookie is the 401 <c>token_invalid</c>: the link has to start again.</summary>
    [Fact]
    public async Task Confirm_WithoutAPendingCookie_Answers401()
    {
        using var host = MinimalHost();
        using var client = host.CreateLinkClient();

        var (_, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);

        using var response = await LinkTestHost.SendAsync(
            client,
            HttpMethod.Post,
            "/link/confirm",
            [(LinkTestHost.SessionCookieName, session)]);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LinkTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.TokenInvalid);
    }

    /// <summary>A staged link presented by another account is refused and writes no row.</summary>
    [Fact]
    public async Task Confirm_WithAnotherUsersStagedCookie_Answers401AndWritesNothing()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, "second@example.test"));
        using var client = host.CreateLinkClient();

        var (_, sessionA, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);
        var (_, sessionB, _) = await MemberAsync(host, client, LinkTestHost.GitHub, GitHubSubject);

        const string CandidateSubject = "candidate-for-a-only";
        host.Use(LinkTestHost.GitHub, LinkTestHost.Identity(LinkTestHost.GitHub, CandidateSubject, "candidate@example.test"));

        var linkCookie = await StageAsync(host, client, LinkTestHost.GitHub, sessionA);

        using var response = await ConfirmAsync(client, sessionB, linkCookie);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LinkTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.TokenInvalid);
        (await host.UserIdAsync(LinkTestHost.GitHub, CandidateSubject)).ShouldBeNull();
    }

    /// <summary>
    /// After a successful confirm the staging is spent for the browser that made it: the response deletes the
    /// cookie, so the framework answers 401 <c>token_invalid</c> to the confirmation that follows.
    /// </summary>
    /// <remarks>
    /// A client that KEEPS the deleted value can still present it, and that is not a replay hole for this story: the
    /// staging is a protected cookie rather than a row (decision Q4), so it has no server-side spent marker, and the
    /// database's own answer — <c>already_linked</c> — writes nothing new. The second case below pins that.
    /// </remarks>
    [Fact]
    public async Task Confirm_AfterASuccessfulConfirm_TheBrowserHasNoStagingLeft()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, Email));
        using var client = host.CreateLinkClient();

        var (userId, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);
        var linkCookie = await StageAsync(host, client, LinkTestHost.GitHub, session);

        using (var first = await ConfirmAsync(client, session, linkCookie))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await LinkTestHost.MemberAsync(first, "outcome")).ShouldBe("linked");
            LinkTestHost.CookieValue(first, LinkConfirmCookie.Name).ShouldBe(string.Empty);
        }

        // The browser that honoured the deletion sends no staged cookie.
        using (var replay = await ConfirmAsync(client, session, linkCookie: null))
        {
            replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await LinkTestHost.ErrorCodeAsync(replay)).ShouldBe(ApiErrorCode.TokenInvalid);
        }

        // A client that kept the value reaches the database, which answers idempotently and writes nothing new.
        using (var stale = await ConfirmAsync(client, session, linkCookie))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await LinkTestHost.MemberAsync(stale, "outcome")).ShouldBe("already_linked");
        }

        (await host.CountAsync("select count(*) from public.user_identity where user_id = @id", ("id", userId))).ShouldBe(2L);
    }

    /// <summary>A staged link older than its ten-minute window is refused, even though its protection is intact.</summary>
    [Fact]
    public async Task Confirm_WithAnExpiredCookie_Answers401()
    {
        using var host = MinimalHost();
        using var client = host.CreateLinkClient();

        var (userId, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);

        const string Subject = "expired-candidate";
        var expired = host.Protect(new LinkConfirmPayload(
            LinkConfirmCookie.PayloadVersion,
            userId,
            LinkTestHost.GitHub,
            Subject,
            "expired@example.test",
            "Test Member",
            DateTimeOffset.UtcNow.AddMinutes(-11),
            "AAAAAAAAAAAAAAAAAAAAAA"));

        using var response = await ConfirmAsync(client, session, expired);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LinkTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.TokenInvalid);
        (await host.UserIdAsync(LinkTestHost.GitHub, Subject)).ShouldBeNull();
    }

    /// <summary>
    /// <c>DELETE pending</c> answers 204 and deletes the staged cookie, so the confirm that follows from that browser
    /// is the 401 <c>token_invalid</c> and writes nothing.
    /// </summary>
    /// <remarks>
    /// The deletion is the browser's staging ending, not a revocation list: the staged value is a protected cookie
    /// with no server-side record (decision Q4), so a client that kept the deleted value could still complete the
    /// link within its ten minutes. Nothing is written by the confirm the browser actually makes.
    /// </remarks>
    [Fact]
    public async Task DeletePending_ThenConfirmFromThatBrowser_Answers401AndWritesNothing()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, Email));
        using var client = host.CreateLinkClient();

        var (_, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);

        const string CandidateSubject = "deleted-candidate";
        host.Use(LinkTestHost.GitHub, LinkTestHost.Identity(LinkTestHost.GitHub, CandidateSubject, "deleted@example.test"));
        var linkCookie = await StageAsync(host, client, LinkTestHost.GitHub, session);

        using (var delete = await LinkTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            "/link/pending",
            [(LinkTestHost.SessionCookieName, session), (LinkConfirmCookie.Name, linkCookie)]))
        {
            delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            LinkTestHost.CookieValue(delete, LinkConfirmCookie.Name).ShouldBe(string.Empty);
        }

        using var confirm = await ConfirmAsync(client, session, linkCookie: null);

        confirm.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LinkTestHost.ErrorCodeAsync(confirm)).ShouldBe(ApiErrorCode.TokenInvalid);
        (await host.UserIdAsync(LinkTestHost.GitHub, CandidateSubject)).ShouldBeNull();
    }

    /// <summary><c>GET pending</c> with nothing staged is the 404 <c>not_found</c>, and a foreign cookie is deleted.</summary>
    [Fact]
    public async Task Pending_WithNothingStaged_Answers404AndDeletesAForeignCookie()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, "second@example.test"));
        using var client = host.CreateLinkClient();

        var (_, sessionA, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);
        var (_, sessionB, _) = await MemberAsync(host, client, LinkTestHost.GitHub, GitHubSubject);

        const string CandidateSubject = "pending-candidate";
        host.Use(LinkTestHost.GitHub, LinkTestHost.Identity(LinkTestHost.GitHub, CandidateSubject, "pending@example.test"));
        var linkCookie = await StageAsync(host, client, LinkTestHost.GitHub, sessionA);

        using (var nothing = await LinkTestHost.SendAsync(
            client,
            HttpMethod.Get,
            "/link/pending",
            [(LinkTestHost.SessionCookieName, sessionA)]))
        {
            nothing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await LinkTestHost.ErrorCodeAsync(nothing)).ShouldBe(ApiErrorCode.NotFound);
        }

        using var foreign = await LinkTestHost.SendAsync(
            client,
            HttpMethod.Get,
            "/link/pending",
            [(LinkTestHost.SessionCookieName, sessionB), (LinkConfirmCookie.Name, linkCookie)]);

        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        LinkTestHost.CookieValue(foreign, LinkConfirmCookie.Name).ShouldBe(string.Empty);
    }

    // ------------------------------------------------------------------ the database's decision

    /// <summary>
    /// The identity already belongs to an account that is in use: 409 <c>binding_conflict</c>, the row stays
    /// where it is, and the route deletes the staged cookie (AC6: never steal, never merge).
    /// </summary>
    [Fact]
    public async Task Confirm_WhenTheIdentityBelongsToAnAccountInUse_Answers409AndKeepsTheRow()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, "second@example.test"));
        using var client = host.CreateLinkClient();

        // The github identity belongs to a second account that has a live membership.
        var (_, sessionA, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);
        var (githubUser, sessionB, _) = await MemberAsync(host, client, LinkTestHost.GitHub, GitHubSubject);
        sessionB.ShouldNotBeNullOrEmpty();

        var linkCookie = await StageAsync(host, client, LinkTestHost.GitHub, sessionA);

        using var response = await ConfirmAsync(client, sessionA, linkCookie);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await LinkTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.BindingConflict);
        LinkTestHost.CookieValue(response, LinkConfirmCookie.Name).ShouldBe(string.Empty);
        (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject)).ShouldBe(githubUser);
    }

    /// <summary>
    /// The identity belongs to an account with no membership and no live session: the confirm moves it, and the
    /// move is audited with the account it came from.
    /// </summary>
    [Fact]
    public async Task Confirm_MovesAnUnusedSourceAccount_AndAuditsTheMove()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, "movable@example.test"));
        using var client = host.CreateLinkClient();

        var (_, sessionA, orgA) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);

        // The source account exists (its first sign-in created it) and holds nothing else.
        using (var source = await host.SignInAsync(client, LinkTestHost.GitHub))
        {
            source.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var movedFrom = (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject))
            ?? throw new InvalidOperationException("the source sign-in created no identity");

        var linkCookie = await StageAsync(host, client, LinkTestHost.GitHub, sessionA);

        using (var response = await ConfirmAsync(client, sessionA, linkCookie))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await LinkTestHost.MemberAsync(response, "outcome")).ShouldBe("moved");
        }

        // The identity row is on the confirming account now, and the source account has none left.
        (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject)).ShouldBe(
            await host.UserIdAsync(LinkTestHost.Google, GoogleSubject));
        (await host.CountAsync("select count(*) from public.user_identity where user_id = @id", ("id", movedFrom))).ShouldBe(0L);

        var rows = (await host.AuditRowsAsync(orgA))
            .Where(row => row.Action is IdentityAuditActions.Link or IdentityAuditActions.LinkMove)
            .ToArray();

        rows.Length.ShouldBe(2);
        rows[0].Action.ShouldBe(IdentityAuditActions.Link);
        rows[1].Action.ShouldBe(IdentityAuditActions.LinkMove);
        rows[1].MetadataJson.ShouldNotBeNull().ShouldContain(movedFrom.ToString("D"));
    }

    /// <summary>Confirming the same provider twice is the idempotent <c>already_linked</c>, not an error.</summary>
    [Fact]
    public async Task Confirm_Twice_AnswersAlreadyLinked()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, Email));
        using var client = host.CreateLinkClient();

        var (userId, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);

        var first = await StageAsync(host, client, LinkTestHost.GitHub, session);
        using (var linked = await ConfirmAsync(client, session, first))
        {
            linked.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await LinkTestHost.MemberAsync(linked, "outcome")).ShouldBe("linked");
        }

        var second = await StageAsync(host, client, LinkTestHost.GitHub, session);
        using var again = await ConfirmAsync(client, session, second);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LinkTestHost.MemberAsync(again, "outcome")).ShouldBe("already_linked");
        (await host.CountAsync("select count(*) from public.user_identity where user_id = @id", ("id", userId))).ShouldBe(2L);
    }

    /// <summary>
    /// The two refusal branches the database would rarely produce are proven at their seam: a store that answers
    /// <c>disabled</c> renders 403 <c>account_disabled</c> and spends the staging, and an outcome this build does
    /// not know fails closed as 503 while keeping it.
    /// </summary>
    /// <param name="outcome">The <see cref="LinkResult"/> the scripted store answers, by value (the enum is module-internal).</param>
    /// <param name="expectedStatus">The status the route must answer.</param>
    /// <param name="expectedCode">The 00 §9.2 <c>error.code</c> it must carry.</param>
    /// <param name="stagingSpent">Whether the staged cookie must be spent by that answer.</param>
    [Theory]
    [InlineData((int)LinkResult.Disabled, HttpStatusCode.Forbidden, ApiErrorCode.AccountDisabled, true)]
    [InlineData((int)LinkResult.Unrecognised, HttpStatusCode.ServiceUnavailable, ApiErrorCode.Unavailable, false)]
    public async Task Confirm_StoreOutcome_IsMappedAndTheStagingFollowsTheAnswer(
        int outcome,
        HttpStatusCode expectedStatus,
        string expectedCode,
        bool stagingSpent)
    {
        var store = new ScriptedLinkStore((LinkResult)outcome);

        using var host = LinkTestHost.Start(
            Fixture,
            services =>
            {
                services.RemoveAll<ILinkIdentityStore>();
                services.AddSingleton<ILinkIdentityStore>(store);
            },
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, Email));
        using var client = host.CreateLinkClient();

        var (_, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);
        var linkCookie = await StageAsync(host, client, LinkTestHost.GitHub, session);

        using var response = await ConfirmAsync(client, session, linkCookie);

        response.StatusCode.ShouldBe(expectedStatus);
        (await LinkTestHost.ErrorCodeAsync(response)).ShouldBe(expectedCode);

        if (stagingSpent)
        {
            LinkTestHost.CookieValue(response, LinkConfirmCookie.Name).ShouldBe(string.Empty);
        }
        else
        {
            // Kept, so a retry after the deployment is fixed still has the verified identity.
            LinkTestHost.CookieValue(response, LinkConfirmCookie.Name).ShouldBeNull();
            response.Headers.RetryAfter.ShouldNotBeNull();
        }
    }

    // ------------------------------------------------------------------ origin

    /// <summary>
    /// A request that names a foreign <c>Origin</c> is refused on every linking route, on top of same-site
    /// cookies; the deployment's own origin is accepted.
    /// </summary>
    [Fact]
    public async Task Origin_ForeignIsRefusedOnEveryRoute_ThePublicOriginIsAccepted()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, Email));
        using var client = host.CreateLinkClient();

        var (_, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);

        const string Foreign = "https://evil.example";

        using (var refusedStart = await LinkTestHost.SendAsync(
            client,
            HttpMethod.Post,
            "/link/github",
            [(LinkTestHost.SessionCookieName, session)],
            Foreign))
        {
            refusedStart.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await LinkTestHost.ErrorCodeAsync(refusedStart)).ShouldBe(ApiErrorCode.Forbidden);
            LinkTestHost.CookieValue(refusedStart, LoginStateCookie.Name).ShouldBeNull();
        }

        var linkCookie = await StageAsync(host, client, LinkTestHost.GitHub, session);

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, "/link/pending"),
                     (HttpMethod.Post, "/link/confirm"),
                     (HttpMethod.Delete, "/link/pending"),
                 })
        {
            using var refused = await LinkTestHost.SendAsync(
                client,
                method,
                path,
                [(LinkTestHost.SessionCookieName, session), (LinkConfirmCookie.Name, linkCookie)],
                Foreign);

            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await LinkTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.Forbidden);
        }

        // Nothing was confirmed by the refused calls.
        (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject)).ShouldBeNull();

        using var accepted = await LinkTestHost.SendAsync(
            client,
            HttpMethod.Post,
            "/link/github",
            [(LinkTestHost.SessionCookieName, session)],
            LinkTestHost.PublicBaseUrl);

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------ the staged value

    /// <summary>
    /// The staged value is confidential: it carries no plaintext address, subject or display name, only the
    /// protected payload this host's ring can read back.
    /// </summary>
    [Fact]
    public async Task StagedCookie_CarriesNoPlaintext()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, "staged@example.test"));
        using var client = host.CreateLinkClient();

        var (userId, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);
        var linkCookie = await StageAsync(host, client, LinkTestHost.GitHub, session);

        const string StagedEmail = "staged@example.test";

        linkCookie.ShouldNotContain(StagedEmail, Case.Insensitive);
        linkCookie.ShouldNotContain("staged", Case.Insensitive);
        linkCookie.ShouldNotContain(GitHubSubject, Case.Insensitive);
        linkCookie.ShouldNotContain("Test Member", Case.Insensitive);
        linkCookie.ShouldNotContain(userId.ToString("D"), Case.Insensitive);

        host.ReadStagedLink(linkCookie).UserId.ShouldBe(userId);
    }

    // ------------------------------------------------------------------ audit

    /// <summary>
    /// One <c>auth.link</c> row in the chain of every ACTIVE org of the confirming user, and none in a suspended
    /// one: the audit follows the orgs the link actually affects.
    /// </summary>
    [Fact]
    public async Task Confirm_WritesOneLinkRowInEveryActiveOrgAndNoneInASuspendedOne()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, Email));
        using var client = host.CreateLinkClient();

        var (userId, session, firstOrg) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);

        var secondOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await host.SeedMembershipAsync(secondOrg, userId);

        var suspendedOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await host.SeedMembershipAsync(suspendedOrg, userId);
        await host.ArrangeAsync(
            "update public.organization set status = 'suspended' where id = @id",
            ("id", suspendedOrg));

        var linkCookie = await StageAsync(host, client, LinkTestHost.GitHub, session);
        using var confirm = await ConfirmAsync(client, session, linkCookie);
        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);

        foreach (var orgId in new[] { firstOrg, secondOrg })
        {
            var links = (await host.AuditRowsAsync(orgId))
                .Where(row => row.Action == IdentityAuditActions.Link)
                .ToArray();

            links.Length.ShouldBe(1);
            links[0].ObjectId.ShouldBe(userId.ToString("D"));

            var metadata = links[0].MetadataJson.ShouldNotBeNull();
            metadata.ShouldContain(LinkTestHost.GitHub);
            metadata.ShouldNotContain(Email, Case.Insensitive);
        }

        (await host.AuditRowsAsync(suspendedOrg))
            .Where(row => row.Action is IdentityAuditActions.Link or IdentityAuditActions.LinkMove)
            .ShouldBeEmpty();
    }

    /// <summary>
    /// An audit chain that cannot commit fails the confirm closed (503 + <c>Retry-After</c>) while the identity
    /// stays attached and the staging is kept; the retry converges on <c>already_linked</c> and writes the row.
    /// </summary>
    [Fact]
    public async Task Confirm_AuditFault_Answers503KeepsTheLinkAndTheRetryConverges()
    {
        FaultingAuditWriter? audit = null;

        using var host = LinkTestHost.Start(
            Fixture,
            services =>
            {
                services.RemoveAll<IIdentityAuditWriter>();
                services.AddSingleton<IIdentityAuditWriter>(provider =>
                    audit = new FaultingAuditWriter(AuditWriterFactory.Create(provider), failingWrites: 0));
            },
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, Email));
        using var client = host.CreateLinkClient();

        var (userId, session, orgId) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);
        var linkCookie = await StageAsync(host, client, LinkTestHost.GitHub, session);

        // Armed only now: the member's own sign-in wrote its auth.login row through this registration, and the fault
        // must land on the confirm's audit instead.
        audit.ShouldNotBeNull().Arm(1);

        using (var failed = await ConfirmAsync(client, session, linkCookie))
        {
            failed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await LinkTestHost.ErrorCodeAsync(failed)).ShouldBe(ApiErrorCode.Unavailable);
            failed.Headers.RetryAfter.ShouldNotBeNull();

            // The link is committed, the staging is kept for the retry.
            LinkTestHost.CookieValue(failed, LinkConfirmCookie.Name).ShouldBeNull();
        }

        (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject)).ShouldBe(userId);
        (await host.AuditRowsAsync(orgId))
            .ShouldNotContain(row => row.Action == IdentityAuditActions.Link);

        using var retry = await ConfirmAsync(client, session, linkCookie);

        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LinkTestHost.MemberAsync(retry, "outcome")).ShouldBe("already_linked");
        (await host.AuditRowsAsync(orgId)).Count(row => row.Action == IdentityAuditActions.Link).ShouldBe(1);
    }

    // ------------------------------------------------------------------ nothing sensitive on the wire

    /// <summary>
    /// No cookie value of the linking flow reaches a log line or a <c>Location</c> header: the staged link and the
    /// session stay in cookies and are never echoed into a redirect or the diagnostics.
    /// </summary>
    /// <remarks>
    /// Two things the login suites already pin, restated here because the linking routes share the pipeline: the
    /// framework's own request-log category is floored above Information, and the start route's provider URL — which
    /// carries the one-time state the browser is being sent to anyway — is logged legibly. So the capture is cleared
    /// after the start and the state is asserted against the callback, <c>pending</c> and <c>confirm</c> only.
    /// </remarks>
    [Fact]
    public async Task CookieValues_NeverReachLogsOrLocationHeaders()
    {
        var logs = new LinkTestHost.CapturingLoggerProvider();

        using var host = LinkTestHost.StartWithLogs(
            Fixture,
            logs,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, Email));
        using var client = host.CreateLinkClient();

        var (_, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);

        // The deployment's logging floor: the request log cannot write the callback's query at Information.
        host.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics")
            .IsEnabled(LogLevel.Information)
            .ShouldBeFalse("ApiHost floors the request log above Information");

        // Non-vacuity: the provider under test really records a line written through it (CA1848: a LoggerMessage
        // delegate, not a bare LogWarning).
        CaptureProbe(logs.CreateLogger("capture-probe"), "code=probe state=probe", null);
        logs.Entries.ShouldContain(entry => entry.Message.Contains("code=probe", StringComparison.Ordinal));
        logs.Clear();

        var start = await StartLinkAsync(host, client, LinkTestHost.GitHub, session);
        using var startResponse = start.Response;
        var stateCookie = start.StateCookie;
        var state = start.Payload.State;

        // The start legitimately logs the provider URL; nothing after it may log any of these values.
        logs.Clear();

        using var callback = await LinkTestHost.LinkCallbackAsync(
            client,
            LinkTestHost.GitHub,
            stateCookie,
            state,
            session);
        var linkCookie = LinkTestHost.CookieValue(callback, LinkConfirmCookie.Name)
            ?? throw new InvalidOperationException("the callback staged no link cookie");

        using var pending = await PendingAsync(client, session, linkCookie);
        using var confirm = await ConfirmAsync(client, session, linkCookie);

        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);

        var sensitive = new[] { session, stateCookie, state, linkCookie };

        foreach (var response in new[] { startResponse, callback, pending, confirm })
        {
            var location = response.Headers.Location?.ToString();
            if (location is null)
            {
                continue;
            }

            foreach (var value in sensitive)
            {
                location.ShouldNotContain(value, Case.Insensitive);
            }
        }

        // The callback's query is the real leak vector: neither the code nor the state parameter may be logged, and
        // the authorization code value this host sends must not appear at any level either.
        logs.Entries.ShouldNotContain(
            entry => entry.Message.Contains("code=", StringComparison.Ordinal),
            "the provider's authorization code must never be logged");
        logs.Entries.ShouldNotContain(
            entry => entry.Message.Contains("state=", StringComparison.Ordinal),
            "the one-time state query must never be logged");
        logs.Entries.ShouldNotContain(
            entry => entry.Message.Contains("authorization-code", StringComparison.Ordinal),
            "the code value itself must never appear at any level");

        foreach (var entry in logs.Entries)
        {
            foreach (var value in sensitive)
            {
                entry.Message.ShouldNotContain(value, Case.Insensitive);
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A host with both providers and no case-specific substitution.</summary>
    private LinkTestHost MinimalHost() =>
        LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, Email));

    /// <summary>
    /// Signs <paramref name="provider"/> in once (creating the account and its identity row without a session),
    /// then seeds an org with an active membership — the harness's owner seam, which arranging rows is for — and
    /// signs in again for the session it mints.
    /// </summary>
    /// <param name="host">The running host.</param>
    /// <param name="client">A client of the host.</param>
    /// <param name="provider">The provider route value.</param>
    /// <param name="subject">The subject its fake verifies.</param>
    private static async Task<(Guid UserId, string Session, Guid OrgId)> MemberAsync(
        LinkTestHost host,
        HttpClient client,
        string provider,
        string subject)
    {
        using (var first = await host.SignInAsync(client, provider))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await LinkTestHost.ErrorCodeAsync(first)).ShouldBe(ApiErrorCode.NoMembership);
        }

        var userId = (await host.UserIdAsync(provider, subject))
            ?? throw new InvalidOperationException("the first sign-in created no identity for " + provider);

        var orgId = await host.Fixture.SeedOrgAsync(cancellationToken: LinkTestHost.Token);
        await host.SeedMembershipAsync(orgId, userId);

        using var second = await host.SignInAsync(client, provider);
        second.StatusCode.ShouldBe(HttpStatusCode.Found);

        var session = LinkTestHost.SessionCookieValue(second)
            ?? throw new InvalidOperationException("the member sign-in issued no session");

        return (userId, session, orgId);
    }

    /// <summary>Starts a link on the session and returns the state the callback needs.</summary>
    /// <param name="host">The running host.</param>
    /// <param name="client">A client of the host.</param>
    /// <param name="provider">The provider to add.</param>
    /// <param name="session">The user's session cookie value.</param>
    private static async Task<(HttpResponseMessage Response, LoginStatePayload Payload, string StateCookie)> StartLinkAsync(
        LinkTestHost host,
        HttpClient client,
        string provider,
        string session)
    {
        var started = await host.StartLinkAsync(client, provider, session);

        return (started.Response, started.Payload, started.StateCookie);
    }

    /// <summary>Runs start and callback and returns the staged link cookie value.</summary>
    /// <param name="host">The running host.</param>
    /// <param name="client">A client of the host.</param>
    /// <param name="provider">The provider to add.</param>
    /// <param name="session">The user's session cookie value.</param>
    private static async Task<string> StageAsync(
        LinkTestHost host,
        HttpClient client,
        string provider,
        string session)
    {
        var start = await StartLinkAsync(host, client, provider, session);
        using var startResponse = start.Response;

        using var callback = await LinkTestHost.LinkCallbackAsync(
            client,
            provider,
            start.StateCookie,
            start.Payload.State,
            session);

        callback.StatusCode.ShouldBe(HttpStatusCode.Found);

        return LinkTestHost.CookieValue(callback, LinkConfirmCookie.Name)
            ?? throw new InvalidOperationException("the callback staged no link cookie");
    }

    /// <summary><c>GET /v1/auth/link/pending</c> with the given cookies.</summary>
    /// <param name="client">A client of the host.</param>
    /// <param name="session">The session cookie value.</param>
    /// <param name="linkCookie">The staged link cookie value, or null for none.</param>
    private static Task<HttpResponseMessage> PendingAsync(HttpClient client, string session, string? linkCookie)
    {
        (string Name, string Value)[] cookies = linkCookie is null
            ? [(LinkTestHost.SessionCookieName, session)]
            : [(LinkTestHost.SessionCookieName, session), (LinkConfirmCookie.Name, linkCookie)];

        return LinkTestHost.SendAsync(client, HttpMethod.Get, "/link/pending", cookies);
    }

    /// <summary><c>POST /v1/auth/link/confirm</c> with the given cookies.</summary>
    /// <param name="client">A client of the host.</param>
    /// <param name="session">The session cookie value.</param>
    /// <param name="linkCookie">The staged link cookie value, or null for a browser that no longer has one.</param>
    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, string session, string? linkCookie)
    {
        (string Name, string Value)[] cookies = linkCookie is null
            ? [(LinkTestHost.SessionCookieName, session)]
            : [(LinkTestHost.SessionCookieName, session), (LinkConfirmCookie.Name, linkCookie)];

        return LinkTestHost.SendAsync(client, HttpMethod.Post, "/link/confirm", cookies);
    }

    /// <summary>
    /// A store that answers one outcome for any request, so the route's mapping of the outcomes the database
    /// would rarely produce is proven at its seam.
    /// </summary>
    private sealed class ScriptedLinkStore(LinkResult result) : ILinkIdentityStore
    {
        /// <inheritdoc />
        public ValueTask<Result<LinkOutcome>> LinkAsync(
            Guid userId,
            string provider,
            string subject,
            string email,
            string? displayName,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success(new LinkOutcome(result, null)));
    }
}
