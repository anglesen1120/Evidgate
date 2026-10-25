using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Audit;
using Evidgate.Testing;
using Identity.IntegrationTests.Invites;
using Identity.IntegrationTests.Login.Completion;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Qa;

/// <summary>
/// QA pass over the invite-secret path the login now drives (02.3.1-T2.1, AC4): the invite states the
/// author's cases do not reach (expired, replayed, unreadable staging) and the allowlist entries a real
/// admin form can produce, driven end to end through the login callback.
/// </summary>
/// <remarks>
/// The rows come from <see cref="BindTestBase"/>'s owner-side seeding convention and the host is
/// configured with the same pepper, so the deployment's own <c>MembershipBinder</c> verifies the hash the
/// case minted.
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class QaInviteEdgeTests : BindTestBase
{
    /// <summary>The constructor the harness's assembly fixture flows into.</summary>
    /// <param name="fixture">The shared container, migrated database and reset seam.</param>
    public QaInviteEdgeTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// An invite whose expiry has passed verifies nothing: the row stays <c>pending</c>, the login
    /// answers 403 <c>no_membership</c> and no session is issued.
    /// </summary>
    [Fact]
    public async Task ExpiredInvite_WithItsToken_LeavesItPending_AndAnswersNoMembership()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(
            org,
            "invitee@example.test",
            expiresIn: TimeSpan.FromMinutes(-5));

        using var host = StartHost(
            LoginCompletionTestHost.IdentityProvider(
                LoginCompletionTestHost.Google,
                "qa-google-expired",
                invite.Email));
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInWithInviteAsync(client, invite.Token);

        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LoginCompletionTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.NoMembership);
        LoginCompletionTestHost.SessionCookies(callback).ShouldBeEmpty();
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, invite.MembershipId))
            .ShouldBe(MembershipStatus.Pending, "an expired invite is not bindable");
    }

    /// <summary>
    /// The staged token presented a second time after the first login bound it does not bind again: the
    /// same person signs in normally, and the org still holds exactly one active membership.
    /// </summary>
    [Fact]
    public async Task StagedToken_ReplayedAfterTheBind_DoesNotRebind_AndTheSecondLoginSucceeds()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.test");

        using var host = StartHost(
            LoginCompletionTestHost.IdentityProvider(
                LoginCompletionTestHost.Google,
                "qa-google-replay",
                invite.Email));
        using var client = host.CreateLoginClient();

        using (var first = await host.SignInWithInviteAsync(client, invite.Token))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Found);
        }

        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, invite.MembershipId))
            .ShouldBe(MembershipStatus.Active);

        using var second = await host.SignInWithInviteAsync(client, invite.Token);

        second.StatusCode.ShouldBe(HttpStatusCode.Found, "the account is a member now, so it signs in");
        (await CountInStatusAsync(org, MembershipStatus.Active)).ShouldBe(1L);
        (await CountInStatusAsync(org, MembershipStatus.Pending)).ShouldBe(0L);
    }

    /// <summary>
    /// The staged token leaves the browser on the refusal too: the callback deletes
    /// <c>__Host-evidgate_invite</c> whatever the outcome, so the secret does not sit in the browser
    /// after a wrong-address attempt.
    /// </summary>
    [Fact]
    public async Task RefusedStagedToken_IsDeletedFromTheBrowser()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.test");

        using var host = StartHost(
            LoginCompletionTestHost.IdentityProvider(
                LoginCompletionTestHost.Google,
                "qa-google-other-address",
                "somebody-else@example.test"));
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInWithInviteAsync(client, invite.Token);

        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        LoginCompletionTestHost.InviteCookies(callback).Count.ShouldBe(1, "the callback answers with the cookie");
        LoginCompletionTestHost.InviteCookieValue(callback)
            .ShouldBe(string.Empty, "an empty value is how the browser is told to drop the staging");
    }

    /// <summary>
    /// A staged cookie the server cannot unprotect is no staged token: the login still answers the
    /// ordinary 403 and leaves the invite pending, instead of failing the callback.
    /// </summary>
    [Fact]
    public async Task UnreadableStagedInviteCookie_IsIgnored_AndTheInviteStaysPending()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.test");

        using var host = StartHost(
            LoginCompletionTestHost.IdentityProvider(
                LoginCompletionTestHost.Google,
                "qa-google-unreadable",
                invite.Email));
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInAsync(client, inviteCookieValue: "not-a-protected-value");

        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LoginCompletionTestHost.ErrorCodeAsync(callback)).ShouldBe(ApiErrorCode.NoMembership);
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, invite.MembershipId))
            .ShouldBe(MembershipStatus.Pending);
    }

    /// <summary>
    /// An allowlist entry written with a trailing DNS dot does not admit the plain domain, so the org's
    /// invites stay <c>pending</c> and the login is refused. This is the end-to-end shape of the
    /// admin-form artefact <c>EmailDomainAllowlist</c> compares exactly; the entry is a real domain in
    /// DNS, so the refusal is surprising rather than intended.
    /// </summary>
    [Fact]
    public async Task AllowlistEntryWithATrailingDot_LocksTheOrgOutOfTheBind()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        await SetAllowedDomainsAsync(org, ["example.test."]);
        var invite = await SeedInviteAsync(org, "invitee@example.test");

        using var host = StartHost(
            LoginCompletionTestHost.IdentityProvider(
                LoginCompletionTestHost.Google,
                "qa-google-trailing-dot",
                invite.Email));
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInWithInviteAsync(client, invite.Token);

        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, invite.MembershipId))
            .ShouldBe(MembershipStatus.Pending, "the dotted entry admits no address at all");
    }

    /// <summary>
    /// The allowlist gate does not depend on Google's <c>hd</c> claim: an identity that asserts no
    /// hosted domain binds through the invite secret when the org lists the verified address's domain.
    /// </summary>
    [Fact]
    public async Task BindThroughTheInviteSecret_WithoutAnHdClaim_IsAdmittedByTheList()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        await SetAllowedDomainsAsync(org, ["example.test"]);
        var invite = await SeedInviteAsync(org, "invitee@example.test");

        using var host = StartHost(
            LoginCompletionTestHost.IdentityProvider(
                LoginCompletionTestHost.Google,
                "qa-google-no-hd",
                invite.Email,
                hostedDomain: null));
        using var client = host.CreateLoginClient();

        using var callback = await host.SignInWithInviteAsync(client, invite.Token);

        callback.StatusCode.ShouldBe(HttpStatusCode.Found, "the list, not hd, decides");
        LoginCompletionTestHost.SessionCookieValue(callback).ShouldNotBeNull();
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, invite.MembershipId))
            .ShouldBe(MembershipStatus.Active);
    }

    /// <summary>The host this suite drives: the harness database, this suite's invite pepper and the provider.</summary>
    /// <param name="provider">The one provider the case scripts.</param>
    private LoginCompletionTestHost StartHost(ILoginProvider provider) =>
        LoginCompletionTestHost.Start(
            Fixture,
            new LoginHostSettings
            {
                InvitePepper = Pepper,
                InvitePepperVersion = PepperVersion,
                AuditWriter = null,
                Providers = [provider],
            });

    /// <summary>Writes the org's allowlist as the owner: an empty list clears the restriction.</summary>
    /// <param name="orgId">The org.</param>
    /// <param name="domains">Its <c>allowed_email_domains</c>.</param>
    private Task<int> SetAllowedDomainsAsync(Guid orgId, string[] domains) => Fixture.SeedAsync(
        "update public.organization set allowed_email_domains = @domains::text[] where id = @org_id",
        [("domains", domains), ("org_id", orgId)],
        Token);
}
