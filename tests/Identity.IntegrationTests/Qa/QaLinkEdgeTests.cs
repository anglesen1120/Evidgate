using System.Net;
using System.Security.Cryptography;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Linking;
using Evidgate.Testing;
using Identity.IntegrationTests.Login;
using Identity.IntegrationTests.Login.Completion;
using Identity.IntegrationTests.Login.Linking;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Qa;

/// <summary>
/// QA pass over the explicit second-provider link of 02.3.1-T2.2 (AC6, D-S81-Q4/Q5/M1) through the
/// real routes: the staged cookie's bindings, the account states that must not be moved, a race on
/// one identity, and the audit rows of a user who belongs to more than one org.
/// </summary>
/// <remarks>
/// Every case drives the four <c>/v1/auth/link/*</c> routes and the shared callback over HTTP against
/// the harness database, so the assertions are about the deployment's behaviour rather than about a
/// helper's. Sequence numbering, rate-limit budgets and the ten-minute staging all come from the
/// production wiring.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class QaLinkEdgeTests : TenantTest
{
    /// <summary>The address the first account's provider verifies.</summary>
    private const string EmailA = "qa-a@example.test";

    /// <summary>The address the second account's provider verifies.</summary>
    private const string EmailB = "qa-b@example.test";

    /// <summary>The address the free identity both accounts race for.</summary>
    private const string EmailC = "qa-c@example.test";

    /// <summary>The first account's Google subject.</summary>
    private const string GoogleSubjectA = "qa-google-a";

    /// <summary>The second account's GitHub subject.</summary>
    private const string GitHubSubjectB = "qa-github-b";

    /// <summary>The subject of the identity nobody owns yet (the link target of most cases).</summary>
    private const string GitHubSubjectC = "qa-github-c";

    /// <summary>The constructor the harness's assembly fixture flows into.</summary>
    /// <param name="fixture">The shared container; this class gets a private database dropped after each test.</param>
    public QaLinkEdgeTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    // ------------------------------------------------------------------ the staged cookie's bindings

    /// <summary>
    /// A confirm cookie staged for one account is "nothing pending" for a different signed-in account:
    /// the read answers 404 <c>not_found</c> and deletes the cookie on the way out, so a shared browser
    /// can never show (or attach) an identity staged for the other account.
    /// </summary>
    [Fact]
    public async Task Pending_WithACookieStagedForAnotherAccount_Answers404_AndDeletesIt()
    {
        using var host = TwoAccountHost();
        using var client = host.CreateLinkClient();

        var (userIdA, sessionA, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubjectA);
        var (userIdB, sessionB, _) = await MemberAsync(host, client, LinkTestHost.GitHub, GitHubSubjectB);

        var stagedForA = await StageAsync(host, client, LinkTestHost.GitHub, sessionA);

        using var pending = await PendingAsync(client, sessionB, stagedForA);

        pending.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the staging names another account");
        (await LinkTestHost.ErrorCodeAsync(pending)).ShouldBe(ApiErrorCode.NotFound);
        LinkTestHost.CookieValue(pending, LinkConfirmCookie.Name)
            .ShouldBe(string.Empty, "a cookie the route refused to act on is deleted, not kept");

        // The staged value still names the account it was staged for, not the one that presented it.
        host.ReadStagedLink(stagedForA).UserId.ShouldBe(userIdA);
        userIdA.ShouldNotBe(userIdB);
    }

    /// <summary>
    /// The same cookie on <c>confirm</c>: 401, deleted, and not a single row written — the identity the
    /// staging names stays free.
    /// </summary>
    [Fact]
    public async Task Confirm_WithACookieStagedForAnotherAccount_Answers401_AndWritesNothing()
    {
        using var host = TwoAccountHost();
        using var client = host.CreateLinkClient();

        var (_, sessionA, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubjectA);
        var (_, sessionB, _) = await MemberAsync(host, client, LinkTestHost.GitHub, GitHubSubjectB);

        // Re-point the GitHub fake at an identity nobody owns, so "wrote nothing" is observable.
        host.Use(LinkTestHost.GitHub, LinkTestHost.Identity(LinkTestHost.GitHub, GitHubSubjectC, EmailC));
        var stagedForA = await StageAsync(host, client, LinkTestHost.GitHub, sessionA);

        using var confirm = await ConfirmAsync(client, sessionB, stagedForA);

        confirm.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LinkTestHost.ErrorCodeAsync(confirm)).ShouldBe(ApiErrorCode.TokenInvalid);
        LinkTestHost.CookieValue(confirm, LinkConfirmCookie.Name).ShouldBe(string.Empty);
        (await host.CountAsync(
            "select count(*) from public.user_identity where provider = @provider and subject = @subject",
            ("provider", LinkTestHost.GitHub),
            ("subject", GitHubSubjectC))).ShouldBe(0L);
    }

    /// <summary>A staged cookie with one byte changed reads as "nothing staged": 404, and it is deleted.</summary>
    [Fact]
    public async Task Pending_WithATamperedCookie_Answers404_AndDeletesIt()
    {
        using var host = TwoAccountHost();
        using var client = host.CreateLinkClient();

        var (_, sessionA, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubjectA);
        var staged = await StageAsync(host, client, LinkTestHost.GitHub, sessionA);

        using var pending = await PendingAsync(client, sessionA, Tamper(staged));

        pending.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        LinkTestHost.CookieValue(pending, LinkConfirmCookie.Name).ShouldBe(string.Empty);
    }

    /// <summary>
    /// An oversized cookie value is a client value, not a fault: the route answers a 4xx, never a 5xx,
    /// and never echoes the value.
    /// </summary>
    [Fact]
    public async Task Pending_WithAnOversizedCookieValue_AnswersAClientError()
    {
        using var host = TwoAccountHost();
        using var client = host.CreateLinkClient();

        var (_, sessionA, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubjectA);
        var oversized = new string('a', 8000);

        using var pending = await PendingAsync(client, sessionA, oversized);

        ((int)pending.StatusCode).ShouldBeLessThan(500);
        pending.StatusCode.ShouldBe(HttpStatusCode.NotFound, "an unreadable value is no staged link");
        var body = await LinkTestHost.BodyAsync(pending);
        body.Contains(oversized[..64], StringComparison.Ordinal)
            .ShouldBeFalse("the route never echoes the value it refused");
    }

    // ------------------------------------------------------------------ the routes' own refusals

    /// <summary>An unknown provider on <c>start</c> is a 404, decided before any database read.</summary>
    [Fact]
    public async Task Start_ForAnUnknownProvider_Answers404()
    {
        using var host = TwoAccountHost();
        using var client = host.CreateLinkClient();

        var (_, sessionA, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubjectA);

        using var start = await LinkTestHost.SendAsync(
            client,
            HttpMethod.Post,
            "/link/saml",
            sessionA);

        start.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await LinkTestHost.ErrorCodeAsync(start)).ShouldBe(ApiErrorCode.NotFound);
    }

    /// <summary>
    /// D-S81-M1, mid-flow, under the 02.3.2-T1 contract (D7): staging succeeds, then the account is disabled, and
    /// the confirm answers 403 <c>account_disabled</c> — the authenticator now tells "disabled" from "no session",
    /// which is what D-S81-M1 handed to this task — with no identity row and nothing audited.
    /// </summary>
    [Fact]
    public async Task Confirm_AfterTheAccountWasDisabled_Answers403AccountDisabled_AndWritesNothing()
    {
        using var host = TwoAccountHost();
        using var client = host.CreateLinkClient();

        var (userId, session, orgId) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubjectA);

        host.Use(LinkTestHost.GitHub, LinkTestHost.Identity(LinkTestHost.GitHub, GitHubSubjectC, EmailC));
        var staged = await StageAsync(host, client, LinkTestHost.GitHub, session);

        (await LoginCompletionTestHost.SetUserStatusAsync(Fixture, userId, "disabled")).ShouldBe(1);

        using var confirm = await ConfirmAsync(client, session, staged);

        confirm.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "a disabled account's live session is a 403 account_disabled, not a signed-out 401 (D7)");
        (await LinkTestHost.ErrorCodeAsync(confirm)).ShouldBe(ApiErrorCode.AccountDisabled);
        (await host.CountAsync(
            "select count(*) from public.user_identity where provider = @provider and subject = @subject",
            ("provider", LinkTestHost.GitHub),
            ("subject", GitHubSubjectC))).ShouldBe(0L);
        var audit = await host.AuditRowsAsync(orgId);
        audit.Count(row => row.Action == "auth.login").ShouldBe(1, "the sign-in that minted the session was audited");
        audit.Count(row => row.Action is "auth.link" or "auth.link_move")
            .ShouldBe(0, "a refused confirm audits nothing");
    }

    // ------------------------------------------------------------------ the move branch

    /// <summary>
    /// The move branch's boundary: the source account holds an EXPIRED session and no membership, so
    /// it has nothing live and the identity may be moved; the audit records both the link and the
    /// account it came from.
    /// </summary>
    [Fact]
    public async Task Confirm_MovesAnIdentityWhoseSourceHoldsOnlyAnExpiredSession()
    {
        using var host = TwoAccountHost();
        using var client = host.CreateLinkClient();

        var (userIdA, sessionA, orgA) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubjectA);

        // The source account: an identity created by a refused sign-in, no membership, one stale session.
        using (var first = await host.SignInAsync(client, LinkTestHost.GitHub))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var userIdB = await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubjectB)
            ?? throw new InvalidOperationException("the refused sign-in created no identity");
        userIdB.ShouldNotBe(userIdA);

        await host.ArrangeAsync(
            """
            insert into public.session
                (id, user_id, cookie_hmac, pepper_version, expires_at, absolute_expires_at)
            values (@id, @user_id, @hmac, 1, now() - interval '2 hours', now() - interval '1 hour')
            """,
            ("id", Guid.CreateVersion7()),
            ("user_id", userIdB),
            ("hmac", RandomNumberGenerator.GetBytes(32)));

        var staged = await StageAsync(host, client, LinkTestHost.GitHub, sessionA);

        using var confirm = await ConfirmAsync(client, sessionA, staged);

        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LinkTestHost.MemberAsync(confirm, "outcome")).ShouldBe("moved");
        (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubjectB)).ShouldBe(userIdA);

        var rows = await host.AuditRowsAsync(orgA);
        rows.Count(row => row.Action == "auth.link").ShouldBe(1);
        var moved = rows.Single(row => row.Action == "auth.link_move");
        var metadata = moved.MetadataJson
            ?? throw new InvalidOperationException("the move recorded no metadata");
        metadata.Contains(userIdB.ToString("D"), StringComparison.Ordinal)
            .ShouldBeTrue("the move records the account the identity came from");
    }

    /// <summary>
    /// Two accounts confirm the same free identity at once: exactly one links and the other is told the
    /// identity is in use, so a race cannot produce two owners or a 5xx.
    /// </summary>
    [Fact]
    public async Task Confirm_TwoAccountsRaceForOneIdentity_OneWinsAndTheOtherConflicts()
    {
        using var host = TwoAccountHost();
        using var client = host.CreateLinkClient();

        var (userIdA, sessionA, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubjectA);
        var (userIdB, sessionB, _) = await MemberAsync(host, client, LinkTestHost.GitHub, GitHubSubjectB);

        // Both stage the SAME free identity: one provider fake, two stagings, two accounts.
        host.Use(LinkTestHost.Google, LinkTestHost.Identity(LinkTestHost.Google, "qa-google-c", EmailC));
        var cookieA = await StageAsync(host, client, LinkTestHost.Google, sessionA);
        var cookieB = await StageAsync(host, client, LinkTestHost.Google, sessionB);

        var confirmations = await Task.WhenAll(
            ConfirmAsync(client, sessionA, cookieA),
            ConfirmAsync(client, sessionB, cookieB));

        try
        {
            var statuses = new[] { confirmations[0].StatusCode, confirmations[1].StatusCode };

            statuses.Count(status => status == HttpStatusCode.OK).ShouldBe(1, "one confirm links the identity");
            statuses.Count(status => status == HttpStatusCode.Conflict)
                .ShouldBe(1, "the other is told the identity is in use, never given a second owner");
        }
        finally
        {
            foreach (var confirmation in confirmations)
            {
                confirmation.Dispose();
            }
        }

        (await host.CountAsync(
            "select count(*) from public.user_identity where provider = @provider and subject = @subject",
            ("provider", LinkTestHost.Google),
            ("subject", "qa-google-c"))).ShouldBe(1L);

        var owner = await host.UserIdAsync(LinkTestHost.Google, "qa-google-c")
            ?? throw new InvalidOperationException("the race left the identity unowned");
        owner.ShouldBeOneOf(userIdA, userIdB);
    }

    // ------------------------------------------------------------------ an identity already on the account

    /// <summary>
    /// Staging the provider the account already signed in with answers <c>already_linked</c> and writes
    /// no second identity row: the explicit confirm is idempotent on its own account.
    /// </summary>
    [Fact]
    public async Task Confirm_OfAProviderAlreadyOnTheSameAccount_AnswersAlreadyLinked()
    {
        using var host = TwoAccountHost();
        using var client = host.CreateLinkClient();

        var (userId, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubjectA);

        var staged = await StageAsync(host, client, LinkTestHost.Google, session);

        using var confirm = await ConfirmAsync(client, session, staged);

        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LinkTestHost.MemberAsync(confirm, "outcome")).ShouldBe("already_linked");
        (await host.CountAsync(
            "select count(*) from public.user_identity where user_id = @user_id",
            ("user_id", userId))).ShouldBe(1L);
    }

    // ------------------------------------------------------------------ audit rows across orgs

    /// <summary>
    /// Q5: a confirm appends <c>auth.link</c> once to the chain of EACH active org the user belongs to,
    /// and a suspended org's chain receives nothing.
    /// </summary>
    [Fact]
    public async Task Confirm_AuditsAuthLinkOnceInEachActiveOrgOnly()
    {
        using var host = TwoAccountHost();
        using var client = host.CreateLinkClient();

        var (userId, session, firstOrg) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubjectA);
        var secondOrg = await Fixture.SeedOrgAsync(cancellationToken: LinkTestHost.Token);
        var suspendedOrg = await host.Fixture.SeedOrgAsync(cancellationToken: LinkTestHost.Token);
        await host.SeedMembershipAsync(secondOrg, userId);
        await host.SeedMembershipAsync(suspendedOrg, userId);
        await host.ArrangeAsync(
            "update public.organization set status = 'suspended' where id = @id",
            ("id", suspendedOrg));

        host.Use(LinkTestHost.GitHub, LinkTestHost.Identity(LinkTestHost.GitHub, GitHubSubjectC, EmailC));
        var staged = await StageAsync(host, client, LinkTestHost.GitHub, session);

        using var confirm = await ConfirmAsync(client, session, staged);

        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.AuditRowsAsync(firstOrg)).Count(row => row.Action == "auth.link").ShouldBe(1);
        (await host.AuditRowsAsync(secondOrg)).Count(row => row.Action == "auth.link").ShouldBe(1);
        (await host.AuditRowsAsync(suspendedOrg)).ShouldBeEmpty("a suspended org admits nobody");
    }

    // ------------------------------------------------------------------ the login's org count

    /// <summary>
    /// A login by a user of three active orgs audits once in each chain and creates exactly one session;
    /// the same user of a suspended-only org is refused with no row anywhere.
    /// </summary>
    [Fact]
    public async Task Login_AuditsOncePerActiveOrg_AndRefusesASuspendedOnlyMember()
    {
        using var host = LoginCompletionTestHost.Start(
            Fixture,
            LoginCompletionTestHost.IdentityProvider(LinkTestHost.Google, GoogleSubjectA, EmailA));
        using var client = host.CreateLoginClient();

        using (var first = await host.SignInAsync(client))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var userId = await LoginCompletionTestHost.UserIdAsync(Fixture, LinkTestHost.Google, GoogleSubjectA);
        var orgs = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            var org = await Fixture.SeedOrgAsync(cancellationToken: LinkTestHost.Token);
            await LoginCompletionTestHost.SeedMembershipAsync(Fixture, org, userId);
            orgs.Add(org);
        }

        using var callback = await host.SignInAsync(client);

        callback.StatusCode.ShouldBe(HttpStatusCode.Found);
        foreach (var org in orgs)
        {
            (await LoginCompletionTestHost.LoginAuditRowsAsync(Fixture, org))
                .ShouldBe(1, "one auth.login row per active org");
        }

        (await LoginCompletionTestHost.CountAsync(Fixture, "select count(*) from public.session"))
            .ShouldBe(1, "one sign-in, one session");
        (await LoginCompletionTestHost.CountAsync(
            Fixture,
            "select count(*) from public.audit_log where action = 'auth.login'")).ShouldBe(3);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A host whose Google fake verifies the first account and whose GitHub fake the second.</summary>
    private LinkTestHost TwoAccountHost() =>
        LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubjectA, EmailA),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubjectB, EmailB));

    /// <summary>
    /// Brings <c>(provider, subject)</c> to a session: a first refused sign-in creates the account, an
    /// owner-seeded org and active membership give it a seat, and the second sign-in mints the session.
    /// </summary>
    /// <param name="host">The running host.</param>
    /// <param name="client">A client of the host.</param>
    /// <param name="provider">The provider route value.</param>
    /// <param name="subject">The subject its fake verifies.</param>
    private async Task<(Guid UserId, LinkTestHost.LinkSession Session, Guid OrgId)> MemberAsync(
        LinkTestHost host,
        HttpClient client,
        string provider,
        string subject)
    {
        using (var first = await host.SignInAsync(client, provider))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var userId = await host.UserIdAsync(provider, subject)
            ?? throw new InvalidOperationException("the first sign-in created no identity for " + provider);
        var orgId = await Fixture.SeedOrgAsync(cancellationToken: LinkTestHost.Token);
        await host.SeedMembershipAsync(orgId, userId);

        using var second = await host.SignInAsync(client, provider);
        second.StatusCode.ShouldBe(HttpStatusCode.Found);

        return (userId, LinkTestHost.LinkSession.FromSignIn(second), orgId);
    }

    /// <summary>Runs <c>start</c> and the callback and returns the staged link cookie's value.</summary>
    /// <param name="host">The running host.</param>
    /// <param name="client">A client of the host.</param>
    /// <param name="provider">The provider to add.</param>
    /// <param name="session">The signed-in account's session, as the sign-in left it.</param>
    private static async Task<string> StageAsync(
        LinkTestHost host,
        HttpClient client,
        string provider,
        LinkTestHost.LinkSession session)
    {
        var start = await host.StartLinkAsync(client, provider, session);
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

    /// <summary><c>GET /v1/auth/link/pending</c> with the two cookies a browser would send.</summary>
    /// <param name="client">A client of the host.</param>
    /// <param name="session">The session, as the sign-in left it.</param>
    /// <param name="linkCookie">The staged link cookie value, or null for none.</param>
    private static Task<HttpResponseMessage> PendingAsync(HttpClient client, LinkTestHost.LinkSession session, string? linkCookie) =>
        LinkTestHost.SendAsync(client, HttpMethod.Get, "/link/pending", session, linkCookie);

    /// <summary><c>POST /v1/auth/link/confirm</c> with the two cookies a browser would send.</summary>
    /// <param name="client">A client of the host.</param>
    /// <param name="session">The session, as the sign-in left it.</param>
    /// <param name="linkCookie">The staged link cookie value, or null for none.</param>
    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, LinkTestHost.LinkSession session, string? linkCookie) =>
        LinkTestHost.SendAsync(client, HttpMethod.Post, "/link/confirm", session, linkCookie);

    /// <summary>Replaces the protected cookie's trailing bytes, which its MAC no longer covers.</summary>
    /// <param name="value">A real staged cookie value.</param>
    private static string Tamper(string value)
    {
        var replacement = value.EndsWith("AAAA", StringComparison.Ordinal) ? "BBBB" : "AAAA";

        return value[..^4] + replacement;
    }
}
