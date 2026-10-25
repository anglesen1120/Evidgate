using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Invites;

/// <summary>
/// 02.1.2-T2 AC8 / T75: the only path on which an email alone may bind. The org opts in
/// (<c>organization.invite_autobind_verified_domain</c>), the provider is Google, and the
/// account's own hosted domain (<c>hd</c>) names the domain of the provider-verified
/// address. Every other combination leaves the invite <c>pending</c> — and the same empty
/// result, never an error that tells the caller which condition failed.
/// </summary>
[Trait("Category", "Docker")]
[Collection(HarnessSuite.Name)]
public sealed class AutoBindTests : BindTestBase
{
    public AutoBindTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// Off by default: with the opt-in unset, a Google login with an exactly matching
    /// verified address and hosted domain binds nothing — not even when the invite carries a
    /// token the login simply did not present.
    /// </summary>
    [Fact]
    public async Task EmailAlone_NeverBinds_WithoutTheOptIn()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.com");

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(
                Guid.CreateVersion7(),
                "google",
                invite.Email,
                EmailVerified: true,
                InviteToken: null,
                HostedDomain: "example.com"),
            Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Bound.ShouldBeEmpty();
        (await MembershipStateAsync(org, invite.MembershipId)).Status.ShouldBe(MembershipStatus.Pending);
    }

    /// <summary>
    /// Opt-in on, Google, and an <c>hd</c> that names the verified address's own domain: the
    /// pending invite activates without any secret, and only the opted-in org of the two
    /// binds.
    /// </summary>
    [Fact]
    public async Task OptIn_And_MatchingHostedDomain_BindsWithoutASecret()
    {
        var optedIn = await SeedOrgAsync(autobindVerifiedDomain: true);
        var notOptedIn = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(optedIn, "invitee@example.com");
        var otherOrgInvite = await SeedInviteAsync(notOptedIn, "invitee@example.com");
        var userId = Guid.CreateVersion7();

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(
                userId,
                "google",
                "Invitee@Example.com",
                EmailVerified: true,
                InviteToken: null,
                HostedDomain: "Example.com"),
            Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Bound.Count.ShouldBe(1);
        result.Value.Bound[0].OrgId.ShouldBe(optedIn);
        result.Value.Bound[0].MembershipId.ShouldBe(invite.MembershipId);
        result.Value.Bound[0].Via.ShouldBe("verified_domain");

        var (status, boundUser) = await MembershipStateAsync(optedIn, invite.MembershipId);
        status.ShouldBe(MembershipStatus.Active);
        boundUser.ShouldBe(userId);
        (await MembershipStateAsync(notOptedIn, otherOrgInvite.MembershipId)).Status.ShouldBe(MembershipStatus.Pending);
    }

    /// <summary>
    /// The four ways the auto-bind must refuse, each leaving the row <c>pending</c> with the
    /// same empty answer: an <c>hd</c> of another domain, a provider other than Google, a
    /// missing <c>hd</c>, and an unverified address.
    /// </summary>
    [Fact]
    public async Task HostedDomainMismatch_NonGoogleProvider_MissingHd_And_Unverified_StayPending()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: true);
        var invite = await SeedInviteAsync(org, "invitee@example.com");
        var binder = CreateBinder();
        var userId = Guid.CreateVersion7();

        var wrongDomain = await binder.BindAsync(
            new BindPendingMembershipsRequest(userId, "google", invite.Email, EmailVerified: true, InviteToken: null, HostedDomain: "other.example.com"),
            Token);

        wrongDomain.Value.Bound.ShouldBeEmpty("the hd must name the address's own domain");

        var github = await binder.BindAsync(
            new BindPendingMembershipsRequest(userId, "github", invite.Email, EmailVerified: true, InviteToken: null, HostedDomain: "example.com"),
            Token);

        github.Value.Bound.ShouldBeEmpty("only a Google Workspace hd is trusted");

        var noHd = await binder.BindAsync(
            new BindPendingMembershipsRequest(userId, "google", invite.Email, EmailVerified: true, InviteToken: null, HostedDomain: null),
            Token);

        noHd.Value.Bound.ShouldBeEmpty("without an hd there is no domain to trust");

        var unverified = await binder.BindAsync(
            new BindPendingMembershipsRequest(userId, "google", invite.Email, EmailVerified: false, InviteToken: null, HostedDomain: "example.com"),
            Token);

        unverified.Value.Bound.ShouldBeEmpty("an unverified address never binds");

        (await MembershipStateAsync(org, invite.MembershipId)).Status.ShouldBe(MembershipStatus.Pending);
    }

    /// <summary>
    /// The opt-in never upgrades an address that names a different invitee, and a plain
    /// pending row without a minted token (the 02.1.2-T1 shape) cannot be swept up by it
    /// either: the candidate lookup requires an expiry, so it is never a candidate.
    /// </summary>
    [Fact]
    public async Task OptIn_DoesNotBind_AnotherAddress_OrAnInviteWithoutAToken()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: true);
        var invite = await SeedInviteAsync(org, "invitee@example.com");
        var tokenless = await SeedPendingInviteWithoutTokenAsync(org, "tokenless@example.com");
        var binder = CreateBinder();

        var anotherAddress = await binder.BindAsync(
            new BindPendingMembershipsRequest(Guid.CreateVersion7(), "google", "colleague@example.com", EmailVerified: true, InviteToken: null, HostedDomain: "example.com"),
            Token);

        anotherAddress.Value.Bound.ShouldBeEmpty();

        var tokenlessResult = await binder.BindAsync(
            new BindPendingMembershipsRequest(Guid.CreateVersion7(), "google", "tokenless@example.com", EmailVerified: true, InviteToken: null, HostedDomain: "example.com"),
            Token);

        tokenlessResult.Value.Bound.ShouldBeEmpty("an invite without a minted expiry is not a candidate");

        (await MembershipStateAsync(org, invite.MembershipId)).Status.ShouldBe(MembershipStatus.Pending);
        (await MembershipStateAsync(org, tokenless)).Status.ShouldBe(MembershipStatus.Pending);
    }
}
