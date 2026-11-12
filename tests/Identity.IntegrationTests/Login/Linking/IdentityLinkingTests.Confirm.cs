using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Audit;
using Evidgate.Modules.Identity.Login.Linking;
using Evidgate.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Linking;

/// <summary>The staged cookie’s guards and the database’s own decision, over HTTP.</summary>
public sealed partial class IdentityLinkingTests : TenantTest
{

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
            session);

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
            session, linkCookie))
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
            sessionA))
        {
            nothing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await LinkTestHost.ErrorCodeAsync(nothing)).ShouldBe(ApiErrorCode.NotFound);
        }

        using var foreign = await LinkTestHost.SendAsync(
            client,
            HttpMethod.Get,
            "/link/pending",
            sessionB, linkCookie);

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
        sessionB.Session.ShouldNotBeNullOrEmpty();

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
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, MovableEmail));
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
        rows[0].MetadataJson.ShouldNotBeNull().ShouldContain(LinkTestHost.GitHub, Case.Sensitive);
        rows[1].Action.ShouldBe(IdentityAuditActions.LinkMove);

        var moveMetadata = rows[1].MetadataJson.ShouldNotBeNull();
        moveMetadata.ShouldContain(LinkTestHost.GitHub, Case.Sensitive);
        moveMetadata.ShouldContain("\"previous_id\"", Case.Sensitive);
        moveMetadata.ShouldContain(movedFrom.ToString("D"), Case.Insensitive);

        (await host.VerifyChainAsync(orgA)).ShouldBeNull("both rows are valid entries of the org's chain");
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
    [InlineData((int)LinkResult.Unauthenticated, HttpStatusCode.Unauthorized, ApiErrorCode.Unauthenticated, true)]
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
            session,
            origin: Foreign))
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
                session, linkCookie,
                origin: Foreign);

            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await LinkTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.Forbidden);
        }

        // Nothing was confirmed by the refused calls.
        (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject)).ShouldBeNull();

        using var accepted = await LinkTestHost.SendAsync(
            client,
            HttpMethod.Post,
            "/link/github",
            session,
            origin: LinkTestHost.PublicBaseUrl);

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
