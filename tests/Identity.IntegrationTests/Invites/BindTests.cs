using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Invites;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Invites;

/// <summary>
/// 02.1.2-T2 §9 (T75): the invite-secret half of the bind — the one path that turns a
/// <c>pending</c> membership into an active one. Every case runs the real pre-tenant
/// resolver (<c>evidgate_resolve.invite_by_secret</c>), the real per-org tenant scope and
/// the real single-use update, so "stays pending" means the row is untouched and "bound"
/// means <c>status</c> and <c>user_id</c> really moved inside a committed transaction.
/// </summary>
[Trait("Category", "Docker")]
[Collection(HarnessSuite.Name)]
public sealed class BindTests : BindTestBase
{
    public BindTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// AC2/AC3: a valid token plus a provider-verified address equal to the invited one
    /// activates the row, binds exactly the signing-in user, and reports the org with
    /// <c>invite_secret</c> as the way it bound.
    /// </summary>
    [Fact]
    public async Task SecretAndVerifiedEmail_BindsThePendingInvite()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "Invitee@Example.com");
        var userId = await SeedUserAsync();

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(userId, "github", "invitee@example.com", EmailVerified: true, invite.Token, HostedDomain: null),
            Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Bound.Count.ShouldBe(1);
        result.Value.Bound[0].OrgId.ShouldBe(org);
        result.Value.Bound[0].MembershipId.ShouldBe(invite.MembershipId);
        result.Value.Bound[0].Via.ShouldBe("invite_secret");

        var (status, boundUser) = await MembershipStateAsync(org, invite.MembershipId);
        status.ShouldBe(MembershipStatus.Active);
        boundUser.ShouldBe(userId);
    }

    /// <summary>
    /// T75: the provider's verified flag and the address itself are both load-bearing. An
    /// unverified login binds nothing even with the right token; a verified login whose
    /// address is not the invited one binds nothing; and a Google login that reports
    /// <c>email_verified = false</c> binds nothing even when its <c>hd</c> matches and the
    /// org has opted in — the opt-in lowers the requirement to an address, never to an
    /// unverified one.
    /// </summary>
    [Fact]
    public async Task UnverifiedEmailStaysPending()
    {
        // Opted in from the start: none of the three cases below may use the auto-bind path
        // to slip past the verified-address requirement.
        var org = await SeedOrgAsync(autobindVerifiedDomain: true);
        var invite = await SeedInviteAsync(org, "invitee@example.com");
        var binder = CreateBinder();

        var unverified = await binder.BindAsync(
            new BindPendingMembershipsRequest(await SeedUserAsync(), "github", invite.Email, EmailVerified: false, invite.Token, HostedDomain: null),
            Token);

        unverified.IsSuccess.ShouldBeTrue("an unverified login is not an error, it just binds nothing");
        unverified.Value.Bound.ShouldBeEmpty();
        (await MembershipStateAsync(org, invite.MembershipId)).Status.ShouldBe(MembershipStatus.Pending);

        var otherAddress = await binder.BindAsync(
            new BindPendingMembershipsRequest(await SeedUserAsync(), "github", "someone.else@example.com", EmailVerified: true, invite.Token, HostedDomain: null),
            Token);

        otherAddress.Value.Bound.ShouldBeEmpty("the token belongs to another address");
        (await MembershipStateAsync(org, invite.MembershipId)).Status.ShouldBe(MembershipStatus.Pending);

        // The provider fixture shape of a Google account that has not verified its address:
        // opted in, matching hd, and still refused.
        var googleUnverified = await binder.BindAsync(
            new BindPendingMembershipsRequest(
                await SeedUserAsync(),
                "google",
                invite.Email,
                EmailVerified: false,
                InviteToken: null,
                HostedDomain: "example.com"),
            Token);

        googleUnverified.Value.Bound.ShouldBeEmpty();
        (await MembershipStateAsync(org, invite.MembershipId)).Status.ShouldBe(MembershipStatus.Pending);
    }

    /// <summary>
    /// T75: email equality alone never binds — without the secret (absent, garbled, wrong, or
    /// naming a prefix no invite carries) the row stays <c>pending</c> and the answer is the
    /// same empty result every time, so the login flow cannot be used to probe an address.
    /// </summary>
    [Fact]
    public async Task SecretRequired()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.com");
        var binder = CreateBinder();

        var noToken = await binder.BindAsync(
            new BindPendingMembershipsRequest(await SeedUserAsync(), "github", invite.Email, EmailVerified: true, InviteToken: null, HostedDomain: null),
            Token);

        noToken.Value.Bound.ShouldBeEmpty();

        var garbled = await binder.BindAsync(
            new BindPendingMembershipsRequest(await SeedUserAsync(), "github", invite.Email, EmailVerified: true, "evgi_garbled", HostedDomain: null),
            Token);

        garbled.Value.Bound.ShouldBeEmpty();

        // The right prefix with a secret that is well-formed but not the stored one.
        var foreign = InviteTokenFormat.Generate();
        var wrongSecret = string.Concat("evgi_", invite.Prefix, "_", foreign.Secret);
        var mismatched = await binder.BindAsync(
            new BindPendingMembershipsRequest(await SeedUserAsync(), "github", invite.Email, EmailVerified: true, wrongSecret, HostedDomain: null),
            Token);

        mismatched.Value.Bound.ShouldBeEmpty();

        // A well-formed token whose prefix no invite carries.
        var unknownPrefix = await binder.BindAsync(
            new BindPendingMembershipsRequest(await SeedUserAsync(), "github", invite.Email, EmailVerified: true, foreign.Full, HostedDomain: null),
            Token);

        unknownPrefix.Value.Bound.ShouldBeEmpty();

        (await MembershipStateAsync(org, invite.MembershipId)).Status.ShouldBe(MembershipStatus.Pending);
    }

    /// <summary>
    /// T75 single use: a token binds once. The second presentation — the same user or any
    /// other — answers an empty result and never rebinds the row to somebody else.
    /// </summary>
    [Fact]
    public async Task ConsumedInvite_IsNeverReplayed()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.com");
        var binder = CreateBinder();
        var firstUser = await SeedUserAsync();

        var first = await binder.BindAsync(
            new BindPendingMembershipsRequest(firstUser, "github", invite.Email, EmailVerified: true, invite.Token, HostedDomain: null),
            Token);

        first.Value.Bound.Count.ShouldBe(1);

        var replay = await binder.BindAsync(
            new BindPendingMembershipsRequest(await SeedUserAsync(), "github", invite.Email, EmailVerified: true, invite.Token, HostedDomain: null),
            Token);

        replay.IsSuccess.ShouldBeTrue();
        replay.Value.Bound.ShouldBeEmpty("the prefix no longer resolves a pending invite");

        var (status, boundUser) = await MembershipStateAsync(org, invite.MembershipId);
        status.ShouldBe(MembershipStatus.Active);
        boundUser.ShouldBe(firstUser, "the first binding stands; a replay can never move it");
    }

    /// <summary>
    /// A cancelled invite (<c>pending → revoked</c>) keeps its token for audit but can never
    /// bind, and an invite the sweeper expired (<c>pending → expired</c>) cannot either: both
    /// answer the same empty result as an unknown prefix.
    /// </summary>
    [Fact]
    public async Task CancelledAndExpiredInvites_NeverBind()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var cancelled = await SeedInviteAsync(org, "cancelled@example.com", MembershipStatus.Revoked);
        var expired = await SeedInviteAsync(org, "expired@example.com", MembershipStatus.Expired, TimeSpan.FromHours(-1));
        var binder = CreateBinder();

        var cancelledResult = await binder.BindAsync(
            new BindPendingMembershipsRequest(await SeedUserAsync(), "github", cancelled.Email, EmailVerified: true, cancelled.Token, HostedDomain: null),
            Token);

        cancelledResult.Value.Bound.ShouldBeEmpty();
        (await MembershipStateAsync(org, cancelled.MembershipId)).Status.ShouldBe(MembershipStatus.Revoked);

        var expiredResult = await binder.BindAsync(
            new BindPendingMembershipsRequest(await SeedUserAsync(), "github", expired.Email, EmailVerified: true, expired.Token, HostedDomain: null),
            Token);

        expiredResult.Value.Bound.ShouldBeEmpty();
        (await MembershipStateAsync(org, expired.MembershipId)).Status.ShouldBe(MembershipStatus.Expired);
    }

    /// <summary>
    /// The expiry is decided by the tenant-side re-read as well: a row that still resolves
    /// (the resolver's <c>invite_expires_at &gt; now()</c> saw a live clock) is refused when
    /// the binder's own clock already reads past the deadline, and it stays <c>pending</c>.
    /// </summary>
    [Fact]
    public async Task ExpiredInvite_IsRefused_Even_When_TheResolverStillReturnsIt()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.com", expiresIn: TimeSpan.FromMinutes(5));

        var result = await CreateBinder(clockOffset: TimeSpan.FromMinutes(10)).BindAsync(
            new BindPendingMembershipsRequest(await SeedUserAsync(), "github", invite.Email, EmailVerified: true, invite.Token, HostedDomain: null),
            Token);

        result.Value.Bound.ShouldBeEmpty();
        (await MembershipStateAsync(org, invite.MembershipId)).Status.ShouldBe(MembershipStatus.Pending);
    }

    /// <summary>
    /// AC7 in the bind path: the resolved org is the only org the transaction can touch, so a
    /// token of org A never activates org B's pending invite for the same address, and the row
    /// of org A is the one that binds.
    /// </summary>
    [Fact]
    public async Task TokenOfOneOrg_NeverBindsAnotherOrgsRow()
    {
        var orgA = await SeedOrgAsync(autobindVerifiedDomain: false);
        var orgB = await SeedOrgAsync(autobindVerifiedDomain: false);
        var inviteA = await SeedInviteAsync(orgA, "shared@example.com");
        var inviteB = await SeedInviteAsync(orgB, "shared@example.com");

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(await SeedUserAsync(), "github", "shared@example.com", EmailVerified: true, inviteA.Token, HostedDomain: null),
            Token);

        result.Value.Bound.Count.ShouldBe(1);
        result.Value.Bound[0].OrgId.ShouldBe(orgA);
        (await MembershipStateAsync(orgA, inviteA.MembershipId)).Status.ShouldBe(MembershipStatus.Active);
        (await MembershipStateAsync(orgB, inviteB.MembershipId)).Status.ShouldBe(MembershipStatus.Pending);
    }

    /// <summary>
    /// The address of another invite is not a credential either: presenting org A's token
    /// with the verified address of org B's invitee binds nothing in either org.
    /// </summary>
    [Fact]
    public async Task VerifiedEmailOfAnotherInvite_NeverBindsThisToken()
    {
        var orgA = await SeedOrgAsync(autobindVerifiedDomain: false);
        var orgB = await SeedOrgAsync(autobindVerifiedDomain: false);
        var inviteA = await SeedInviteAsync(orgA, "alice@example.com");
        var inviteB = await SeedInviteAsync(orgB, "bob@example.com");

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(await SeedUserAsync(), "github", inviteB.Email, EmailVerified: true, inviteA.Token, HostedDomain: null),
            Token);

        result.Value.Bound.ShouldBeEmpty();
        (await MembershipStateAsync(orgA, inviteA.MembershipId)).Status.ShouldBe(MembershipStatus.Pending);
        (await MembershipStateAsync(orgB, inviteB.MembershipId)).Status.ShouldBe(MembershipStatus.Pending);
    }

    /// <summary>
    /// A second bind of an already bound row yields an empty result, for the same user and
    /// for another one: the resolver only answers <c>pending</c> rows.
    /// </summary>
    [Fact]
    public async Task SecondBindOfAnAlreadyBoundRow_YieldsNothing()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.com");
        var binder = CreateBinder();
        var userId = await SeedUserAsync();

        var request = new BindPendingMembershipsRequest(userId, "github", invite.Email, EmailVerified: true, invite.Token, HostedDomain: null);
        (await binder.BindAsync(request, Token)).Value.Bound.Count.ShouldBe(1);

        var second = await binder.BindAsync(request, Token);

        second.IsSuccess.ShouldBeTrue();
        second.Value.Bound.ShouldBeEmpty();
        (await MembershipStateAsync(org, invite.MembershipId)).UserId.ShouldBe(userId);
    }

    /// <summary>
    /// A user who already holds a membership of the org satisfies the invite by being in it:
    /// the <c>membership_org_user_key</c> refusal is swallowed, the call succeeds with an
    /// empty list, and the pending row is left alone rather than becoming a duplicate.
    /// </summary>
    [Fact]
    public async Task UserAlreadyMemberOfThatOrg_StaysUnboundWithoutError()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var (_, userId) = await SeedActiveMemberAsync(org, "member@example.com");
        var invite = await SeedInviteAsync(org, "invitee@example.com");

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(userId, "github", invite.Email, EmailVerified: true, invite.Token, HostedDomain: null),
            Token);

        result.IsSuccess.ShouldBeTrue("already being a member is not a failure");
        result.Value.Bound.ShouldBeEmpty();
        (await MembershipStateAsync(org, invite.MembershipId)).Status.ShouldBe(MembershipStatus.Pending);
        (await CountInStatusAsync(org, MembershipStatus.Active)).ShouldBe(1);
    }
}
