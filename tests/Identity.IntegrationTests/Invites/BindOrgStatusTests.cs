using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Domain;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Invites;

/// <summary>
/// 02.1.2-T2 F4: the secret-bind path honours <c>organization.status</c>. The seat gate statement
/// reads the status in the same row lock that reads <c>max_seats</c>, so a <c>suspended</c> (or
/// <c>deleted</c>) org binds nothing — the rule the auto-bind resolver already applies with
/// <c>o.status = 'active'</c> (0043), now applied on both paths.
/// </summary>
/// <remarks>
/// A refusal here is indistinguishable from every other one — the plain pending invite of
/// 02.1.2-T1, a wrong secret, an expired row — so the tests read the row back through a real tenant
/// scope: still <c>pending</c>, no <c>user_id</c>, no seat consumed.
/// </remarks>
[Trait("Category", "Docker")]
[Collection(HarnessSuite.Name)]
public sealed class BindOrgStatusTests : BindTestBase
{
    public BindOrgStatusTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// F4: a suspended org binds nothing even with a valid secret and a matching verified address,
    /// and the row consumes no seat.
    /// </summary>
    [Fact]
    public async Task SuspendedOrg_WithValidSecretAndMatchingEmail_StaysPending()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false, status: "suspended");
        await SetMaxSeatsAsync(org, maxSeats: 5);
        var invite = await SeedInviteAsync(org, "invitee@example.com");
        var userId = Guid.CreateVersion7();

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(userId, "github", "invitee@example.com", EmailVerified: true, invite.Token, HostedDomain: null),
            Token);

        result.IsSuccess.ShouldBeTrue("a suspended org is not an error to the caller, it just binds nothing");
        result.Value.Bound.ShouldBeEmpty();

        var (status, boundUser) = await MembershipStateAsync(org, invite.MembershipId);
        status.ShouldBe(MembershipStatus.Pending);
        boundUser.ShouldBeNull();
        (await CountInStatusAsync(org, MembershipStatus.Active)).ShouldBe(0);
    }

    /// <summary>
    /// F4 with an unlimited org: <c>max_seats</c> NULL returns before any count, and that early
    /// return must still carry the org's real status — a suspended org binds nothing whether or
    /// not it sets a limit.
    /// </summary>
    [Fact]
    public async Task SuspendedOrgWithoutSeatLimit_StaysPending()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false, status: "suspended");
        await SetMaxSeatsAsync(org, maxSeats: null);
        var invite = await SeedInviteAsync(org, "invitee@example.com");
        var userId = Guid.CreateVersion7();

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(userId, "github", "invitee@example.com", EmailVerified: true, invite.Token, HostedDomain: null),
            Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Bound.ShouldBeEmpty();

        var (status, boundUser) = await MembershipStateAsync(org, invite.MembershipId);
        status.ShouldBe(MembershipStatus.Pending);
        boundUser.ShouldBeNull();
    }

    /// <summary>
    /// The control for the case above: the same invite in the same shape of org, but
    /// <c>active</c>, binds — so the assertion on the suspended org measures the status and
    /// nothing else about the arrangement.
    /// </summary>
    [Fact]
    public async Task ActiveOrg_WithValidSecretAndMatchingEmail_Binds()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false, status: "active");
        await SetMaxSeatsAsync(org, maxSeats: 5);
        var invite = await SeedInviteAsync(org, "invitee@example.com");
        var userId = Guid.CreateVersion7();

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(userId, "github", "invitee@example.com", EmailVerified: true, invite.Token, HostedDomain: null),
            Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Bound.Count.ShouldBe(1);
        result.Value.Bound[0].MembershipId.ShouldBe(invite.MembershipId);
        result.Value.Bound[0].Via.ShouldBe("invite_secret");

        var (status, boundUser) = await MembershipStateAsync(org, invite.MembershipId);
        status.ShouldBe(MembershipStatus.Active);
        boundUser.ShouldBe(userId);
    }
}
