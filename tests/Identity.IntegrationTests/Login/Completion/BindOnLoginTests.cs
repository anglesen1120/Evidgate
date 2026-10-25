using System.Net;
using System.Security.Cryptography;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Audit;
using Evidgate.Testing;
using Identity.IntegrationTests.Invites;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Completion;

/// <summary>
/// 02.3.1-T2.1 / AC4 / AC5 / 02.3.1 §5: the login binds a pending invite. Every case goes through the real
/// routes, the real completion and the real binder against the harness's migrated database — the invite
/// token staged on <c>POST /v1/auth/invite-token</c>, the verified address, and the org's own rules — so what
/// is asserted is the deployment's behaviour, not a memento of it.
/// </summary>
/// <remarks>
/// <para>
/// The invite rows come from <see cref="BindTestBase"/>'s seeding convention (owner connection, the pepper of
/// this suite), and the host is configured with that same pepper, so the deployment's own
/// <c>MembershipBinder</c> verifies the hash the test minted — the happy path proves the mint and the login's
/// verify agree.
/// </para>
/// <para>
/// The two doubles are used only where the question is HOW the login calls the seam:
/// <see cref="RecordingMembershipBinder"/> to prove the ambient tenant is absent and that an unverified or
/// malformed address never reaches the binder, and <see cref="FaultingAuditWriter"/> to prove the login is
/// fail-closed when the audit row cannot be written.
/// </para>
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class BindOnLoginTests : BindTestBase
{
    /// <summary>The org of the seat-limit cases that still has a seat; its id sorts before <see cref="FullOrgId"/>.</summary>
    private static readonly Guid OpenOrgId = Guid.Parse("0190f000-0000-7000-8000-000000000001");

    /// <summary>The org of the seat-limit cases whose one seat is taken (the auto-bind runs in id order).</summary>
    private static readonly Guid FullOrgId = Guid.Parse("0190f000-0000-7000-8000-000000000002");

    /// <summary>The constructor the harness's assembly fixture flows into.</summary>
    /// <param name="fixture">The shared container, migrated database and reset seam.</param>
    public BindOnLoginTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// AC4: the staged token plus the provider-verified address the invite names binds the pending membership
    /// and the login completes — session row, cookie and the 302.
    /// </summary>
    [Fact]
    public async Task StagedToken_WithTheVerifiedInvitedAddress_BindsAndCompletesTheLogin()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.test");

        using var host = StartHost(
            LoginCompletionTestHost.IdentityProvider(
                LoginCompletionTestHost.Google,
                "google-subject-invited",
                invite.Email));
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInWithInviteAsync(client, invite.Token);

        callback.StatusCode.ShouldBe(HttpStatusCode.Found, "the bind admits the account and the session is issued");
        LoginCompletionTestHost.SessionCookieValue(callback).ShouldNotBeNull();

        var userId = await LoginCompletionTestHost
            .UserIdAsync(Fixture, LoginCompletionTestHost.Google, "google-subject-invited");

        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, invite.MembershipId))
            .ShouldBe(MembershipStatus.Active);
        (await LoginCompletionTestHost.MembershipUserIdAsync(Fixture, invite.MembershipId))
            .ShouldBe(userId, "the bind wrote the user the login's own upsert created");
        (await SessionRowsAsync(userId)).ShouldBe(1);
    }

    /// <summary>
    /// AC4, the negative half: the token belongs to an invite addressed to somebody else, so nothing binds and
    /// the account with no other membership is refused — the invite stays <c>pending</c> and reusable by its
    /// addressee.
    /// </summary>
    [Fact]
    public async Task VerifiedAddressOfAnotherPerson_LeavesTheInvitePending_AndAnswersNoMembership()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.test");

        using var host = StartHost(
            LoginCompletionTestHost.IdentityProvider(
                LoginCompletionTestHost.Google,
                "google-subject-other",
                "other-person@example.test"));
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInWithInviteAsync(client, invite.Token);

        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LoginCompletionTestHost.ErrorCodeAsync(callback))
            .ShouldBe(ApiErrorCode.NoMembership);
        LoginCompletionTestHost.SessionCookies(callback).ShouldBeEmpty();
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, invite.MembershipId))
            .ShouldBe(MembershipStatus.Pending, "AC4: an address that is not the invited one binds nothing");
        (await SessionCountAsync()).ShouldBe(0);
    }

    /// <summary>
    /// A well-formed token with the right prefix but another secret verifies nothing: the invite stays
    /// <c>pending</c> and the login answers 403 <c>no_membership</c>.
    /// </summary>
    [Fact]
    public async Task WrongSecret_LeavesTheInvitePending_AndAnswersNoMembership()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.test");

        using var host = StartHost(
            LoginCompletionTestHost.IdentityProvider(
                LoginCompletionTestHost.Google,
                "google-subject-wrong-secret",
                invite.Email));
        using var client = host.CreateLoginClient();

        using var callback = await host
            .SignInWithInviteAsync(client, WithAnotherSecret(invite.Token));

        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LoginCompletionTestHost.ErrorCodeAsync(callback))
            .ShouldBe(ApiErrorCode.NoMembership);
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, invite.MembershipId))
            .ShouldBe(MembershipStatus.Pending, "the single-use secret is what the invite verifies");
    }

    /// <summary>
    /// AC4: an address the provider calls unverified binds nothing, even with the token of that very address —
    /// the login never asks the binder at all.
    /// </summary>
    [Fact]
    public async Task UnverifiedProviderAddress_NeverBinds_EvenWithItsOwnToken()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.test");
        var binder = new RecordingMembershipBinder();

        using var host = StartHost(
            [
                LoginCompletionTestHost.IdentityProvider(
                    LoginCompletionTestHost.Google,
                    "google-subject-unverified",
                    invite.Email,
                    emailVerified: false),
            ],
            binder: binder);
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInWithInviteAsync(client, invite.Token);

        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LoginCompletionTestHost.ErrorCodeAsync(callback))
            .ShouldBe(ApiErrorCode.NoMembership);
        binder.Calls.ShouldBeEmpty("an unverified address is never offered to the binder");
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, invite.MembershipId))
            .ShouldBe(MembershipStatus.Pending);
    }

    /// <summary>
    /// AC4: GitHub's primary-but-unverified entry never binds — the provider reports the address it selected,
    /// and the login passes the provider's own verdict through instead of assuming it.
    /// </summary>
    [Fact]
    public async Task PrimaryButUnverifiedGitHubAddress_NeverBinds()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, LoginCompletionTestHost.GitHubEmail);
        var binder = new RecordingMembershipBinder();

        using var host = StartHost(
            [
                LoginCompletionTestHost.IdentityProvider(
                    LoginCompletionTestHost.GitHub,
                    LoginCompletionTestHost.GitHubSubject,
                    invite.Email,
                    emailVerified: false),
            ],
            binder: binder);
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInWithInviteAsync(
            client,
            invite.Token,
            provider: LoginCompletionTestHost.GitHub);

        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        binder.Calls.ShouldBeEmpty("GitHub's primary-but-unverified entry never reaches the binder");
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, invite.MembershipId))
            .ShouldBe(MembershipStatus.Pending);
    }

    /// <summary>
    /// An address without the shape invite creation accepts skips the bind instead of failing the login: the
    /// binder is never asked, and the answer is the ordinary 403 <c>no_membership</c>.
    /// </summary>
    [Fact]
    public async Task AddressWithoutTheInviteShape_SkipsTheBindWithoutAnError()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.test");
        var binder = new RecordingMembershipBinder();

        using var host = StartHost(
            [
                LoginCompletionTestHost.IdentityProvider(
                    LoginCompletionTestHost.Google,
                    "google-subject-shape",
                    "not-an-address"),
            ],
            binder: binder);
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInWithInviteAsync(client, invite.Token);

        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "an unusable address is not a 422 and not a 500");
        (await LoginCompletionTestHost.ErrorCodeAsync(callback))
            .ShouldBe(ApiErrorCode.NoMembership);
        binder.Calls.ShouldBeEmpty();
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, invite.MembershipId))
            .ShouldBe(MembershipStatus.Pending);
    }

    /// <summary>
    /// AC5's opt-in path end to end: the org enabled <c>invite_autobind_verified_domain</c>, the identity is a
    /// Google Workspace account whose <c>hd</c> names its verified address's domain, and the login binds with
    /// no token at all.
    /// </summary>
    [Fact]
    public async Task GoogleWorkspaceDomain_BindsAnOptedInOrg_WithoutAToken()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: true);
        var invite = await SeedInviteAsync(org, "member@example.com");

        using var host = StartHost(
            LoginCompletionTestHost.IdentityProvider(
                LoginCompletionTestHost.Google,
                "google-subject-workspace",
                invite.Email,
                hostedDomain: "example.com"));
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInAsync(client);

        callback.StatusCode.ShouldBe(HttpStatusCode.Found, "the verified-domain bind admits the account");
        LoginCompletionTestHost.SessionCookieValue(callback).ShouldNotBeNull();
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, invite.MembershipId))
            .ShouldBe(MembershipStatus.Active);
    }

    /// <summary>
    /// The same hosted domain on another provider binds nothing: the auto-bind path is Google's alone (the
    /// binder refuses <c>hd</c> for anyone else), so the login answers 403 <c>no_membership</c>.
    /// </summary>
    [Fact]
    public async Task NonGoogleProvider_WithTheSameHostedDomain_DoesNotBind()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: true);
        var invite = await SeedInviteAsync(org, "member@example.com");

        using var host = StartHost(
            LoginCompletionTestHost.IdentityProvider(
                LoginCompletionTestHost.GitHub,
                LoginCompletionTestHost.GitHubSubject,
                invite.Email,
                hostedDomain: "example.com"));
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInAsync(client, LoginCompletionTestHost.GitHub);

        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LoginCompletionTestHost.ErrorCodeAsync(callback))
            .ShouldBe(ApiErrorCode.NoMembership);
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, invite.MembershipId))
            .ShouldBe(MembershipStatus.Pending);
    }

    /// <summary>
    /// AC5: the restricted org stays <c>pending</c> for an off-list verified domain while the unrestricted org
    /// of the same login binds — and the login is admitted by that bind.
    /// </summary>
    [Fact]
    public async Task GoogleOffTheOrgsAllowlist_StaysPendingThere_WhileAnUnrestrictedOrgBinds()
    {
        var unrestricted = await SeedLoginOrgAsync(OpenOrgId, autobind: true);
        var restricted = await SeedLoginOrgAsync(FullOrgId, autobind: true, allowedDomains: ["partner.example"]);
        var unrestrictedInvite = await SeedInviteAsync(unrestricted, "member@example.com");
        var restrictedInvite = await SeedInviteAsync(restricted, "member@example.com");

        using var host = StartHost(
            LoginCompletionTestHost.IdentityProvider(
                LoginCompletionTestHost.Google,
                "google-subject-allowlist",
                "member@example.com",
                hostedDomain: "example.com"));
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInAsync(client);

        callback.StatusCode.ShouldBe(HttpStatusCode.Found);
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, unrestrictedInvite.MembershipId))
            .ShouldBe(MembershipStatus.Active);
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, restrictedInvite.MembershipId))
            .ShouldBe(MembershipStatus.Pending, "AC5: the off-list domain binds nothing in that org");
    }

    /// <summary>
    /// AC5's refusal on its own: the org restricts its domains and the account is off-list, so nothing binds
    /// and the login answers 403 <c>no_membership</c> with no session — the invitation itself stays
    /// <c>pending</c>.
    /// </summary>
    [Fact]
    public async Task GoogleOffTheOnlyOrgsAllowlist_AnswersNoMembership()
    {
        var restricted = await SeedLoginOrgAsync(FullOrgId, autobind: true, allowedDomains: ["partner.example"]);
        var invite = await SeedInviteAsync(restricted, "member@example.com");

        using var host = StartHost(
            LoginCompletionTestHost.IdentityProvider(
                LoginCompletionTestHost.Google,
                "google-subject-restricted",
                "member@example.com",
                hostedDomain: "example.com"));
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInAsync(client);

        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LoginCompletionTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.NoMembership);
        LoginCompletionTestHost.SessionCookies(callback).ShouldBeEmpty();
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, invite.MembershipId))
            .ShouldBe(MembershipStatus.Pending);
    }

    /// <summary>
    /// A 429 is not fatal: the first org of the call binds and commits, the second refuses for its seat, and
    /// the login continues to the membership check — which the first bind already satisfies.
    /// </summary>
    [Fact]
    public async Task SeatLimitOfOneOrg_DoesNotBlockTheBindAnEarlierOrgOfTheSameLoginMade()
    {
        var open = await SeedLoginOrgAsync(OpenOrgId, autobind: true);
        var full = await SeedLoginOrgAsync(FullOrgId, autobind: true, maxSeats: 1);
        var openInvite = await SeedInviteAsync(open, "member@example.com");
        var fullInvite = await SeedInviteAsync(full, "member@example.com");
        await SeedActiveMemberAsync(full, "already-seated@example.com");

        using var host = StartHost(
            LoginCompletionTestHost.IdentityProvider(
                LoginCompletionTestHost.Google,
                "google-subject-seats",
                "member@example.com",
                hostedDomain: "example.com"));
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInAsync(client);

        callback.StatusCode.ShouldBe(
            HttpStatusCode.Found,
            "the second org's 429 quota_exceeded never undoes the first org's committed bind");
        LoginCompletionTestHost.SessionCookieValue(callback).ShouldNotBeNull();
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, openInvite.MembershipId))
            .ShouldBe(MembershipStatus.Active);
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, fullInvite.MembershipId))
            .ShouldBe(MembershipStatus.Pending);
    }

    /// <summary>
    /// The other half of the same rule: when the login's only org is full, the 429 leaves it with no
    /// membership at all — 403 <c>no_membership</c>, no session, and the invite still <c>pending</c>.
    /// </summary>
    [Fact]
    public async Task SeatLimit_WhenTheOnlyOrgOfTheLoginIsFull_AnswersNoMembership()
    {
        var full = await SeedLoginOrgAsync(FullOrgId, autobind: true, maxSeats: 1);
        var invite = await SeedInviteAsync(full, "member@example.com");
        await SeedActiveMemberAsync(full, "already-seated@example.com");

        using var host = StartHost(
            LoginCompletionTestHost.IdentityProvider(
                LoginCompletionTestHost.Google,
                "google-subject-full",
                "member@example.com",
                hostedDomain: "example.com"));
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInAsync(client);

        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LoginCompletionTestHost.ErrorCodeAsync(callback))
            .ShouldBe(ApiErrorCode.NoMembership);
        LoginCompletionTestHost.SessionCookies(callback).ShouldBeEmpty();
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, invite.MembershipId))
            .ShouldBe(MembershipStatus.Pending);
        (await SessionCountAsync()).ShouldBe(0);
    }

    /// <summary>
    /// The binder opens and commits one tenant transaction per org itself, so the login must call it with no
    /// scope of its own open: the double asserts the ambient scope is absent and records exactly what it was
    /// handed.
    /// </summary>
    [Fact]
    public async Task BindRunsOutsideAnyTenantScope_WithTheVerifiedEmailAndTheStagedToken()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.test");
        var binder = new RecordingMembershipBinder();

        using var host = StartHost(
            [
                LoginCompletionTestHost.IdentityProvider(
                    LoginCompletionTestHost.Google,
                    "google-subject-scope",
                    invite.Email),
            ],
            binder: binder);

        // The double observes the ambient scope of the flow it is called on; the accessor is a singleton of
        // the host's own composition, so it is taken from the running services.
        binder.Scopes = host.Services.GetRequiredService<ITenantScopeAccessor>();

        using var client = host.CreateLoginClient();
        using var callback = await host.SignInWithInviteAsync(client, invite.Token);

        binder.Calls.Count.ShouldBe(1);
        var call = binder.Calls[0];
        call.AmbientTenantScope.ShouldBeFalse("the binder opens its own per-org transactions");
        call.Request.UserId.ShouldNotBe(Guid.Empty);
        call.Request.Provider.ShouldBe(LoginCompletionTestHost.Google);
        call.Request.VerifiedEmail.ShouldBe(invite.Email);
        call.Request.EmailVerified.ShouldBeTrue();
        call.Request.InviteToken.ShouldBe(invite.Token);
        call.Request.HostedDomain.ShouldBeNull();

        // The double binds nothing, so the account still has no membership and the login is refused.
        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The alias ceiling (D-S81-Q2) holds on the login path too: an invite addressed to a <c>+tag</c> form of
    /// the inviter's own mailbox binds nothing, so the inviter cannot take the seat and roles of its own
    /// invite through a second provider identity.
    /// </summary>
    [Fact]
    public async Task AliasOfTheInvitersOwnAddress_DoesNotBind()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var (_, inviterUserId) = await SeedActiveMemberAsync(org, "boss@example.test");
        var invite = await SeedInviteAsync(org, "boss+alias@example.test");
        (await Fixture.SeedAsync(
            "update public.membership set invited_by = @invited_by where id = @id",
            [("invited_by", inviterUserId), ("id", invite.MembershipId)],
            Token)).ShouldBe(1);

        using var host = StartHost(
            LoginCompletionTestHost.IdentityProvider(
                LoginCompletionTestHost.Google,
                "google-subject-alias",
                invite.Email));
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInWithInviteAsync(client, invite.Token);

        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, invite.MembershipId))
            .ShouldBe(MembershipStatus.Pending, "the inviter's own alias is still the inviter");
    }

    /// <summary>
    /// Q5: a completed login appends <c>auth.login</c> once to the chain of every ACTIVE org of the user, and
    /// nothing to a suspended org's chain.
    /// </summary>
    [Fact]
    public async Task LoginIsAuditedOncePerActiveOrg_AndNeverForASuspendedOne()
    {
        var activeOrg = await SeedOrgAsync(autobindVerifiedDomain: false);
        var suspendedOrg = await SeedOrgAsync(autobindVerifiedDomain: false, status: "suspended");

        using var host = StartHost(
            LoginCompletionTestHost.IdentityProvider(
                LoginCompletionTestHost.Google,
                "google-subject-audit",
                "auditor@example.test"));
        using var client = host.CreateLoginClient();

        // The first attempt creates the user row and is refused: it holds no membership yet.
        using (var first = await host.SignInAsync(client))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var userId = await LoginCompletionTestHost
            .UserIdAsync(Fixture, LoginCompletionTestHost.Google, "google-subject-audit");

        await LoginCompletionTestHost.SeedMembershipAsync(Fixture, activeOrg, userId);
        await LoginCompletionTestHost.SeedMembershipAsync(Fixture, suspendedOrg, userId);

        using var callback = await host.SignInAsync(client);

        callback.StatusCode.ShouldBe(HttpStatusCode.Found);
        (await LoginCompletionTestHost.LoginAuditRowsAsync(Fixture, activeOrg)).ShouldBe(1);
        (await LoginCompletionTestHost.LoginAuditRowsAsync(Fixture, suspendedOrg))
            .ShouldBe(0, "a suspended org admits nobody, so it has no login to record");
    }

    /// <summary>
    /// 02.3.1-T2.2 / Q5, fail-closed: an audit write that fails gives 503 <c>unavailable</c> with
    /// <c>Retry-After</c>, and no session row or cookie — a login nothing recorded does not happen. Both
    /// faults the writer can raise are covered: the database's own exception and the busy chain.
    /// </summary>
    /// <param name="chainBusy">True to raise <see cref="AuditBusyException"/>, false for an <see cref="NpgsqlException"/>.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditWriterFault_Answers503WithRetryAfter_AndNoSession(bool chainBusy)
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var subject = chainBusy ? "google-subject-busy" : "google-subject-fault";
        var provider = LoginCompletionTestHost.IdentityProvider(
            LoginCompletionTestHost.Google,
            subject,
            "audited@example.test");

        Guid userId;
        using (var healthy = StartHost([provider]))
        using (var client = healthy.CreateLoginClient())
        {
            using var first = await healthy.SignInAsync(client);
            first.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the first attempt creates the user row");

            userId = await LoginCompletionTestHost
                .UserIdAsync(Fixture, LoginCompletionTestHost.Google, subject);
        }

        await LoginCompletionTestHost.SeedMembershipAsync(Fixture, org, userId);

        Exception fault = chainBusy
            ? new AuditBusyException(new TimeoutException("the chain lock"))
            : new NpgsqlException("the audit write failed");

        using var host = StartHost([provider], auditWriter: new FaultingAuditWriter(fault));
        using var faultyClient = host.CreateLoginClient();

        using var callback = await host.SignInAsync(faultyClient);

        callback.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await LoginCompletionTestHost.ErrorCodeAsync(callback))
            .ShouldBe(ApiErrorCode.Unavailable);
        callback.Headers.RetryAfter.ShouldNotBeNull("the 503 of 00 §9.2 says when to sign in again");
        LoginCompletionTestHost.SessionCookies(callback).ShouldBeEmpty();
        (await SessionCountAsync()).ShouldBe(0);
    }

    /// <summary>The host this suite drives: the harness database, this suite's invite pepper and the given seams.</summary>
    /// <param name="provider">The one provider the case scripts.</param>
    /// <returns>The running host, which the case disposes.</returns>
    private LoginCompletionTestHost StartHost(ILoginProvider provider) => StartHost([provider]);

    /// <summary>The host this suite drives: the harness database, this suite's invite pepper and the given seams.</summary>
    /// <param name="providers">Exactly the providers the case scripts.</param>
    /// <param name="binder">A double of the invite binder, or null for the deployment's own.</param>
    /// <param name="auditWriter">A double of the audit writer, or null for the deployment's own.</param>
    /// <returns>The running host, which the case disposes.</returns>
    private LoginCompletionTestHost StartHost(
        ILoginProvider[] providers,
        RecordingMembershipBinder? binder = null,
        IIdentityAuditWriter? auditWriter = null) =>
        LoginCompletionTestHost.Start(
            Fixture,
            new LoginHostSettings
            {
                InvitePepper = Pepper,
                InvitePepperVersion = PepperVersion,
                Binder = binder,
                AuditWriter = auditWriter,
                Providers = providers,
            });

    /// <summary>
    /// Seeds an org with the columns the AC5 and seat cases need, at a chosen id: the auto-bind candidates are
    /// ordered by it, so a case can fix which org the bind visits first.
    /// </summary>
    /// <param name="id">The org id (and the bind order).</param>
    /// <param name="autobind">Whether the org enabled verified-domain auto-bind.</param>
    /// <param name="allowedDomains">Its <c>allowed_email_domains</c>, or null for no restriction.</param>
    /// <param name="maxSeats">Its <c>max_seats</c>, or null for unlimited.</param>
    /// <returns>The org id.</returns>
    private async Task<Guid> SeedLoginOrgAsync(
        Guid id,
        bool autobind,
        string[]? allowedDomains = null,
        int? maxSeats = null)
    {
        await Fixture.SeedAsync(
            """
            insert into public.organization
                (id, name, slug, status, invite_autobind_verified_domain, allowed_email_domains, max_seats)
            values (@id, @name, @slug, 'active', @autobind, @domains::text[], @max_seats)
            """,
            [
                ("id", id),
                ("name", "Seeded login org"),
                ("slug", "login-" + id.ToString("N")[^12..]),
                ("autobind", autobind),
                ("domains", (object?)allowedDomains ?? DBNull.Value),
                ("max_seats", (object?)maxSeats ?? DBNull.Value),
            ],
            Token);

        return id;
    }

    /// <summary>
    /// The same public prefix with a freshly minted secret: a token that is well-formed and verifies nothing.
    /// </summary>
    /// <param name="token">The minted token of a seeded invite.</param>
    private static string WithAnotherSecret(string token)
    {
        var separator = token.LastIndexOf('_');
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return token[..(separator + 1)] + secret;
    }

    /// <summary>How many session rows the whole database holds (the fixture resets between tests).</summary>
    private Task<int> SessionCountAsync() =>
        LoginCompletionTestHost.CountAsync(Fixture, "select count(*) from public.session");

    /// <summary>How many session rows one user holds.</summary>
    /// <param name="userId">The user.</param>
    private Task<long> SessionRowsAsync(Guid userId) =>
        LoginCompletionTestHost.OwnerScalarAsync<long>(
            Fixture,
            "select count(*) from public.session where user_id = @user_id",
            [("user_id", userId)]);
}
