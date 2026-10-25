using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Invites;

/// <summary>
/// 02.1.2-T2 QA pass, written independently of the slice's author: negative, boundary and
/// race cases that the slice's own suites do not pin. A failing test here is a finding about
/// the product, not a flaky test.
/// </summary>
[Trait("Category", "Docker")]
[Collection(HarnessSuite.Name)]
public sealed class InviteQaTests : BindTestBase
{
    public InviteQaTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// Six simultaneous resends of one pending invite. Each reads the row as pending; the
    /// losers' UPDATE meets the version the winner already moved, so EF raises
    /// <c>DbUpdateConcurrencyException</c>. The resend handler maps only unique violations, so
    /// a loser would answer 500 instead of 409 <c>invite_not_pending</c>.
    /// </summary>
    [Fact]
    public async Task Concurrent_Resends_Of_One_Pending_Invite_Never_Answer_500()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();
        var membershipId = await CreatePendingInviteAsync(client, org, "create-qa-a");

        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
            SendAsync(client, HttpMethod.Post, ResendPath(org, membershipId), null, $"resend-qa-a-{index}")));

        var statuses = answers.Select(answer => answer.Status).ToArray();
        statuses.ShouldAllBe(
            status => status == HttpStatusCode.Accepted || status == HttpStatusCode.Conflict,
            "every concurrent resend answers 202 or 409, never 500; got " + string.Join(", ", statuses));

        foreach (var conflict in answers.Where(answer => answer.Status == HttpStatusCode.Conflict))
        {
            Code(conflict.Body).ShouldBe("invite_not_pending");
        }

        (await CountInStatusAsync(org, MembershipStatus.Pending)).ShouldBe(1, "a resend never creates a second row");
    }

    /// <summary>
    /// Resends and cancels fired together at one pending invite. Whichever commits first wins;
    /// the loser must answer 409 <c>invite_not_pending</c>, never 500. The end state follows the
    /// winner: revoked when a cancel answered 204, pending otherwise.
    /// </summary>
    [Fact]
    public async Task Resend_Racing_Cancel_Never_Answers_500_And_The_End_State_Follows_The_Winner()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();
        var membershipId = await CreatePendingInviteAsync(client, org, "create-qa-b");

        var resends = Enumerable.Range(0, 4).Select(index =>
            SendAsync(client, HttpMethod.Post, ResendPath(org, membershipId), null, $"resend-qa-b-{index}"));
        var cancels = Enumerable.Range(0, 4).Select(index =>
            SendAsync(client, HttpMethod.Post, CancelPath(org, membershipId), null, $"cancel-qa-b-{index}"));

        var answers = await Task.WhenAll(resends.Concat(cancels));

        var statuses = answers.Select(answer => answer.Status).ToArray();
        statuses.ShouldAllBe(
            status => status == HttpStatusCode.Accepted
                || status == HttpStatusCode.NoContent
                || status == HttpStatusCode.Conflict,
            "a resend racing a cancel answers 202, 204 or 409, never 500; got " + string.Join(", ", statuses));

        var cancelled = answers.Count(answer => answer.Status == HttpStatusCode.NoContent);
        cancelled.ShouldBeLessThanOrEqualTo(1, "only one cancel can take the pending row");

        var (status, _) = await MembershipStateAsync(org, membershipId);
        status.ShouldBe(
            cancelled == 1 ? MembershipStatus.Revoked : MembershipStatus.Pending,
            "the end state follows the winning write");
    }

    /// <summary>
    /// Five simultaneous cancels of one pending invite: exactly one answers 204, every other
    /// answers 409 <c>invite_not_pending</c>, and the row ends revoked.
    /// </summary>
    [Fact]
    public async Task Concurrent_Cancels_Of_One_Invite_Yield_Exactly_One_204()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();
        var membershipId = await CreatePendingInviteAsync(client, org, "create-qa-c");

        var answers = await Task.WhenAll(Enumerable.Range(0, 5).Select(index =>
            SendAsync(client, HttpMethod.Post, CancelPath(org, membershipId), null, $"cancel-qa-c-{index}")));

        answers.Count(answer => answer.Status == HttpStatusCode.NoContent).ShouldBe(1);

        foreach (var answer in answers.Where(answer => answer.Status != HttpStatusCode.NoContent))
        {
            answer.Status.ShouldBe(HttpStatusCode.Conflict);
            Code(answer.Body).ShouldBe("invite_not_pending");
        }

        (await MembershipStateAsync(org, membershipId)).Status.ShouldBe(MembershipStatus.Revoked);
    }

    /// <summary>
    /// Four simultaneous logins presenting the same single-use token with four different
    /// verified users: exactly one binds, the others bind nothing, and the row is bound to a
    /// single user.
    /// </summary>
    [Fact]
    public async Task Concurrent_Binds_Of_One_Single_Use_Token_Bind_Exactly_One_User()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.com");
        var binder = CreateBinder();

        var attempts = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(
                () => binder.BindAsync(
                    new BindPendingMembershipsRequest(Guid.CreateVersion7(), "github", invite.Email, EmailVerified: true, invite.Token, HostedDomain: null),
                    Token).AsTask(),
                Token))
            .ToArray();

        var results = await Task.WhenAll(attempts);

        results.Count(result => result.IsSuccess && result.Value.Bound.Count == 1).ShouldBe(1, "exactly one login binds the single-use token");
        results.Where(result => result.IsSuccess).Sum(result => result.Value.Bound.Count).ShouldBe(1);

        var (status, boundUser) = await MembershipStateAsync(org, invite.MembershipId);
        status.ShouldBe(MembershipStatus.Active);
        boundUser.ShouldNotBeNull();
    }

    /// <summary>
    /// Boundary: one seat free (<c>max_seats = 2</c>, one active member). The bind is allowed and
    /// fills the last seat; the count sits exactly at the limit afterwards.
    /// </summary>
    [Fact]
    public async Task Bind_OneBelowMaxSeats_StillBinds_And_FillsTheLastSeat()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        await SetMaxSeatsAsync(org, maxSeats: 2);
        await SeedActiveMemberAsync(org, "member@example.com");
        var invite = await SeedInviteAsync(org, "invitee@example.com");

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(Guid.CreateVersion7(), "github", invite.Email, EmailVerified: true, invite.Token, HostedDomain: null),
            Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Bound.Count.ShouldBe(1);
        (await CountInStatusAsync(org, MembershipStatus.Active)).ShouldBe(2);
    }

    /// <summary>
    /// A cancelled invite stays revoked: even with the org opted in, a matching Google login
    /// must not auto-bind it. The candidate lookup filters on status = pending.
    /// </summary>
    [Fact]
    public async Task Revoked_Invite_NeverAutoBinds_Even_With_The_OptIn()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: true);
        var invite = await SeedInviteAsync(org, "invitee@example.com", status: MembershipStatus.Revoked);

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(Guid.CreateVersion7(), "google", invite.Email, EmailVerified: true, null, HostedDomain: "example.com"),
            Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Bound.ShouldBeEmpty();
        (await MembershipStateAsync(org, invite.MembershipId)).Status.ShouldBe(MembershipStatus.Revoked);
    }

    /// <summary>
    /// A verified address that differs only in case and surrounding whitespace is the same
    /// invitee: the bind normalises the address and binds the pending invite.
    /// </summary>
    [Fact]
    public async Task Verified_Email_With_Case_And_Padding_Still_Binds()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.com");

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(Guid.CreateVersion7(), "github", "  Invitee@Example.com ", EmailVerified: true, invite.Token, HostedDomain: null),
            Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Bound.Count.ShouldBe(1);
        (await MembershipStateAsync(org, invite.MembershipId)).Status.ShouldBe(MembershipStatus.Active);
    }

    /// <summary>
    /// The replay of a resend with the same <c>Idempotency-Key</c> answers again with 202 but
    /// without an <c>invite_token</c>, and does not mint a second invite. The secret is never
    /// stored in the idempotency store (D-S78-Q1).
    /// </summary>
    [Fact]
    public async Task Resend_ReplayedWithTheSameIdempotencyKey_AnswersAgain_WithoutAToken()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();
        var membershipId = await CreatePendingInviteAsync(client, org, "create-qa-h");

        var first = await SendAsync(client, HttpMethod.Post, ResendPath(org, membershipId), null, "resend-qa-h");
        first.Status.ShouldBe(HttpStatusCode.Accepted);
        first.Body.GetProperty("invite_token").GetString().ShouldNotBeNullOrWhiteSpace();

        var replay = await SendAsync(client, HttpMethod.Post, ResendPath(org, membershipId), null, "resend-qa-h");

        replay.Status.ShouldBe(HttpStatusCode.Accepted);
        (replay.Body.TryGetProperty("invite_token", out var replayedToken) && replayedToken.ValueKind != JsonValueKind.Null)
            .ShouldBeFalse("a replay must never carry the plaintext token again");
        (await CountInStatusAsync(org, MembershipStatus.Pending)).ShouldBe(1);
    }

    /// <summary>
    /// The two answers whose body carries the plaintext token also carry
    /// <c>Cache-Control: no-store</c> (F13, 00 §9.1): no cache may keep a live invite secret.
    /// </summary>
    [Fact]
    public async Task InviteTokenAnswers_CarryCacheControlNoStore()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        var created = await SendWithCacheHeaderAsync(
            client,
            HttpMethod.Post,
            $"/v1/orgs/{org}/memberships",
            """{"email":"invitee@example.com"}""",
            "create-qa-store");

        created.Status.ShouldBe(HttpStatusCode.Created);
        created.CacheControl.ShouldBe("no-store");

        var membershipId = created.Body.GetProperty("id").GetGuid();

        var resent = await SendWithCacheHeaderAsync(
            client,
            HttpMethod.Post,
            ResendPath(org, membershipId),
            null,
            "resend-qa-store");

        resent.Status.ShouldBe(HttpStatusCode.Accepted);
        resent.CacheControl.ShouldBe("no-store");
    }

    private static string ResendPath(Guid org, Guid membershipId) =>
        $"/v1/orgs/{org}/memberships/{membershipId}/resend";

    private static string CancelPath(Guid org, Guid membershipId) =>
        $"/v1/orgs/{org}/memberships/{membershipId}/cancel-invite";

    private static async Task<Guid> CreatePendingInviteAsync(HttpClient client, Guid org, string idempotencyKey)
    {
        var created = await SendAsync(
            client,
            HttpMethod.Post,
            $"/v1/orgs/{org}/memberships",
            """{"email":"invitee@example.com"}""",
            idempotencyKey);

        created.Status.ShouldBe(HttpStatusCode.Created);

        return created.Body.GetProperty("id").GetGuid();
    }

    /// <summary>One request through the real Api composition, with its <c>Idempotency-Key</c>.</summary>
    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? json,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(method, path);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await client.SendAsync(request, Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        var body = string.IsNullOrEmpty(text) ? default : JsonSerializer.Deserialize<JsonElement>(text);

        return (response.StatusCode, body);
    }

    /// <summary>
    /// <see cref="SendAsync"/> plus the response's <c>Cache-Control</c>, for the header assertions
    /// the tuple of <see cref="SendAsync"/> cannot carry.
    /// </summary>
    private static async Task<(HttpStatusCode Status, JsonElement Body, string? CacheControl)> SendWithCacheHeaderAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? json,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(method, path);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await client.SendAsync(request, Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        var body = string.IsNullOrEmpty(text) ? default : JsonSerializer.Deserialize<JsonElement>(text);
        var cacheControl = response.Headers.TryGetValues("Cache-Control", out var values)
            ? string.Join(", ", values)
            : null;

        return (response.StatusCode, body, cacheControl);
    }

    /// <summary>The 00 §9.2 <c>error.code</c> of a failure body.</summary>
    private static string Code(JsonElement body) =>
        body.GetProperty("error").GetProperty("code").GetString()!;
}
