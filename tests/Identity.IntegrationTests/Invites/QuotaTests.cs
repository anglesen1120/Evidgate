using System.Net;
using System.Text;
using System.Text.Json;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Invites;

/// <summary>
/// The seat limit of 02.1.1-T3 as the invite routes enforce it (02.1.2-T2.3): a create counts
/// <c>active + pending</c> against <c>organization.max_seats</c> under the org's row lock and answers
/// the Tenancy-shaped 429 <c>quota_exceeded</c>, a NULL limit means unlimited, and a resend — which
/// re-mints an existing row and therefore consumes no seat — still succeeds at the boundary.
/// </summary>
[Trait("Category", "Docker")]
public sealed class QuotaTests : CatalogueTestBase
{
    public QuotaTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// An invite fits while <c>active + pending &lt; max_seats</c>; the next one is 429
    /// <c>quota_exceeded</c> with the <c>max_seats</c> detail, not retryable, and writes neither a
    /// membership nor a notification.
    /// </summary>
    [Fact]
    public async Task MaxSeatsReached429()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await SetMaxSeatsAsync(org, 2);
        await SeedMembershipAsync(org, "seated@example.com", MembershipStatus.Active);

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        // One seat is taken, so the first invite (the second seat) still fits.
        var first = await PostAsync(client, $"/v1/orgs/{org}/memberships", """{"email":"first@example.com"}""", "quota-1");
        first.Status.ShouldBe(HttpStatusCode.Created);

        var second = await PostAsync(client, $"/v1/orgs/{org}/memberships", """{"email":"second@example.com"}""", "quota-2");

        second.Status.ShouldBe(HttpStatusCode.TooManyRequests);

        var error = second.Body.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("quota_exceeded");
        error.GetProperty("retryable").GetBoolean()
            .ShouldBeFalse("an unchanged retry cannot fit: the limit or the row count must change first");
        second.HasRetryAfter.ShouldBeTrue("00 §9.2: a 429 always carries Retry-After");

        var detail = error.GetProperty("details")[0];
        detail.GetProperty("path").GetString().ShouldBe("max_seats");
        detail.GetProperty("reason").GetString().ShouldBe("reached");

        (await ScalarAsync<long>("select count(*) from public.membership where org_id = @org", ("org", org)))
            .ShouldBe(2, "the refused invite wrote no row: one active member and one pending invite");
        (await ScalarAsync<long>(
            "select count(*) from public.outbox where org_id = @org and task_type = 'identity.invite.notify'",
            ("org", org))).ShouldBe(1, "and no notification either");
    }

    /// <summary>A NULL <c>max_seats</c> is "no limit" (02.1.1-T3), however many seats are taken.</summary>
    [Fact]
    public async Task MaxSeatsNullMeansUnlimited()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        (await ScalarAsync<long>(
            "select count(*) from public.organization where id = @org and max_seats is null",
            ("org", org))).ShouldBe(1, "the seeded org starts without a seat limit");

        for (var index = 0; index < 3; index++)
        {
            await SeedMembershipAsync(org, $"seated{index}@example.com", MembershipStatus.Active);
        }

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        var created = await PostAsync(client, $"/v1/orgs/{org}/memberships", """{"email":"first@example.com"}""", "unlimited-1");

        created.Status.ShouldBe(HttpStatusCode.Created);
        string.IsNullOrWhiteSpace(created.Body.GetProperty("invite_token").GetString()).ShouldBeFalse("the create answers the token once");
    }

    /// <summary>
    /// AC6 at the boundary: re-minting an existing <c>pending</c> row consumes no seat, so a resend
    /// succeeds at exactly <c>max_seats</c> where a create is already refused.
    /// </summary>
    [Fact]
    public async Task ResendAtTheBoundaryStillSucceeds()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await SetMaxSeatsAsync(org, 1);
        var pendingId = await SeedMembershipAsync(org, "waiting@example.com", MembershipStatus.Pending);

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        // active + pending == max_seats: the org is full for new invites.
        var refused = await PostAsync(client, $"/v1/orgs/{org}/memberships", """{"email":"another@example.com"}""", "boundary-0");
        refused.Status.ShouldBe(HttpStatusCode.TooManyRequests);
        refused.Body.GetProperty("error").GetProperty("code").GetString().ShouldBe("quota_exceeded");

        var resent = await PostAsync(client, $"/v1/orgs/{org}/memberships/{pendingId}/resend", null, "boundary-1");

        resent.Status.ShouldBe(HttpStatusCode.Accepted, "a resend re-mints a row the count already holds");
        resent.Body.GetProperty("id").GetGuid().ShouldBe(pendingId);
        string.IsNullOrWhiteSpace(resent.Body.GetProperty("invite_token").GetString()).ShouldBeFalse("the resend mints a fresh token");

        (await ScalarAsync<long>("select count(*) from public.membership where org_id = @org", ("org", org)))
            .ShouldBe(1, "the resend changed no seat: still the one pending row");
        (await ScalarAsync<long>(
            "select count(*) from public.membership where org_id = @org and status in ('active', 'pending')",
            ("org", org))).ShouldBe(1);
    }

    /// <summary>Sets the org's seat limit through the owner seam; null means unlimited.</summary>
    private Task<int> SetMaxSeatsAsync(Guid orgId, int? maxSeats) => Fixture.SeedAsync(
        "update public.organization set max_seats = @max_seats where id = @org_id",
        [("max_seats", (object?)maxSeats ?? DBNull.Value), ("org_id", orgId)],
        Token);

    /// <summary>Seeds one membership in the given state as the owner and returns its id.</summary>
    private async Task<Guid> SeedMembershipAsync(Guid orgId, string email, string status)
    {
        var membershipId = Guid.CreateVersion7();

        await Fixture.SeedAsync(
            "insert into public.membership (id, org_id, invited_email, status) values (@id, @org_id, @email, @status)",
            [("id", membershipId), ("org_id", orgId), ("email", email), ("status", status)],
            Token);

        return membershipId;
    }

    /// <summary>One owner-connection statement: the harness's arranging/asserting seam (00 §5.5 C3).</summary>
    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(Token))!;
    }

    /// <summary>One POST through the real Api composition, with its <c>Idempotency-Key</c> (and a body only where the route takes one).</summary>
    private static async Task<(HttpStatusCode Status, JsonElement Body, bool HasRetryAfter)> PostAsync(
        HttpClient client,
        string path,
        string? json,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = string.IsNullOrEmpty(text) ? default : JsonSerializer.Deserialize<JsonElement>(text);

        return (response.StatusCode, body, response.Headers.RetryAfter is not null);
    }
}
