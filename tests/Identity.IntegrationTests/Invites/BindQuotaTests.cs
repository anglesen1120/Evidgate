using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Invites;

/// <summary>
/// 02.1.2-T2 AC9 / T75: a bind is a seat. At <c>max_seats</c> the whole call fails with 429
/// <c>quota_exceeded</c> and the invite stays pending; under concurrency the org's row lock
/// is what keeps the active count at the limit — never above it, whatever the number of
/// simultaneous logins.
/// </summary>
[Trait("Category", "Docker")]
[Collection(HarnessSuite.Name)]
public sealed class BindQuotaTests : BindTestBase
{
    public BindQuotaTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// The org is full: one active member against <c>max_seats = 1</c>. A valid invite of a
    /// new address fails the call with the 429 envelope of 02.1.1-T3.2 — code, status and the
    /// limit's name in <c>details</c> — and leaves the row <c>pending</c> with no user.
    /// </summary>
    [Fact]
    public async Task Bind_AtMaxSeats_IsRefused_With_QuotaExceeded_429()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        await SetMaxSeatsAsync(org, maxSeats: 1);
        await SeedActiveMemberAsync(org, "member@example.com");
        var invite = await SeedInviteAsync(org, "invitee@example.com");

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(await SeedUserAsync(), "github", invite.Email, EmailVerified: true, invite.Token, HostedDomain: null),
            Token);

        result.IsSuccess.ShouldBeFalse();
        var error = result.Error.ShouldNotBeNull();
        error.Code.ShouldBe(ApiErrorCode.QuotaExceeded);
        error.Status.ShouldBe(StatusCodes.Status429TooManyRequests);
        var details = error.Details.ShouldNotBeNull();
        details.ShouldContain(detail => detail.Path == "max_seats" && detail.Reason == "reached");

        var (status, boundUser) = await MembershipStateAsync(org, invite.MembershipId);
        status.ShouldBe(MembershipStatus.Pending, "a refused org binds nothing in its transaction");
        boundUser.ShouldBeNull();
        (await CountInStatusAsync(org, MembershipStatus.Active)).ShouldBe(1);
    }

    /// <summary>
    /// Five concurrent binds of five distinct pending invites into an org with
    /// <c>max_seats = 2</c> and no member yet: exactly two activate and three are refused with
    /// 429, so the org ends with exactly the two seats it has — the row lock taken before the
    /// count is what makes the race harmless.
    /// </summary>
    [Fact]
    public async Task Concurrent_Binds_Never_Exceed_MaxSeats()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        await SetMaxSeatsAsync(org, maxSeats: 2);

        var invites = new List<SeededInvite>(5);
        for (var index = 0; index < 5; index++)
        {
            invites.Add(await SeedInviteAsync(
                org,
                string.Create(CultureInfo.InvariantCulture, $"invitee{index}@example.com")));
        }

        var binder = CreateBinder();
        var requests = new List<BindPendingMembershipsRequest>(invites.Count);
        foreach (var invite in invites)
        {
            // Seeded on the arranging path, never inside the racing lambda: the bind's `user_id`
            // needs its `public."user"` row (0044's membership_user_fk) before the race starts.
            requests.Add(new BindPendingMembershipsRequest(
                await SeedUserAsync(),
                "github",
                invite.Email,
                EmailVerified: true,
                invite.Token,
                HostedDomain: null));
        }

        var attempts = requests
            .Select(request => Task.Run(
                () => binder.BindAsync(request, Token).AsTask(),
                Token))
            .ToArray();

        var results = await Task.WhenAll(attempts);

        results.Count(result => result.IsSuccess).ShouldBe(
            2,
            "exactly the free seats bind, never more (the org row lock serialises the binds)");

        foreach (var refused in results.Where(result => !result.IsSuccess))
        {
            refused.Error.ShouldNotBeNull().Code.ShouldBe(ApiErrorCode.QuotaExceeded);
        }

        (await CountInStatusAsync(org, MembershipStatus.Active)).ShouldBe(2);
        (await CountInStatusAsync(org, MembershipStatus.Pending)).ShouldBe(3);

        foreach (var invite in invites)
        {
            var (status, boundUser) = await MembershipStateAsync(org, invite.MembershipId);
            if (status == MembershipStatus.Active)
            {
                boundUser.ShouldNotBeNull("an active membership is always bound to its user");
            }
            else
            {
                status.ShouldBe(MembershipStatus.Pending);
                boundUser.ShouldBeNull();
            }
        }
    }
}
