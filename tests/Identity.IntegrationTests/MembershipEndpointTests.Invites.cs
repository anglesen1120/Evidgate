using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// The invite half of 02.1.2 §5: AC1's normalised <c>pending</c> membership and its 409
/// <c>invite_exists</c> duplicate, the 02.1.2 §3a ASCII-only address shape, and T11's
/// <c>Idempotency-Key</c> replay. The rest of the suite is in
/// <c>MembershipEndpointTests.cs</c>.
/// </summary>
public sealed partial class MembershipEndpointTests
{
    /// <summary>
    /// AC1: the first invite creates a <c>pending</c> membership bound to the normalised
    /// address (no user yet, no role unless asked for one), and the row is readable from
    /// both documented read routes.
    /// </summary>
    [Fact]
    public async Task Invite_Creates_A_Pending_Membership()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        using var created = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            """{"email":"  Alice@Example.COM "}""",
            "invite-1");

        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        var view = JsonSerializer.Deserialize<MembershipView>(await created.Content.ReadAsStringAsync(Token), Wire)!;
        view.OrgId.ShouldBe(org);
        view.Status.ShouldBe(MembershipStatus.Pending);
        view.InvitedEmail.ShouldBe("alice@example.com", "the address is normalised (trim + lowercase) before it is stored");
        view.UserId.ShouldBeNull("an invite is bound to a user only by the login flow (02.3.1-T2)");
        view.Roles.ShouldBeEmpty();
        view.Version.ShouldBe(1);
        created.Headers.ETag!.Tag.ShouldBe("\"1\"", "the response publishes the row's version as its ETag");

        using var fetched = await client.GetAsync($"/v1/orgs/{org}/memberships/{view.Id}", Token);
        fetched.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonSerializer.Deserialize<MembershipView>(await fetched.Content.ReadAsStringAsync(Token), Wire)!
            .Id.ShouldBe(view.Id);

        using var page = await client.GetAsync($"/v1/orgs/{org}/memberships?limit=50", Token);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var listed = JsonSerializer.Deserialize<MembershipListResponse>(await page.Content.ReadAsStringAsync(Token), Wire)!;
        listed.Items.Select(item => item.Id).ShouldBe([view.Id]);
        listed.NextCursor.ShouldBeNull();
    }

    /// <summary>
    /// AC1: a second invite for the same address in the same org is 409
    /// <c>invite_exists</c> — including when the client spells the address with a
    /// different case, because the comparison happens on the normalised value — and no
    /// second row is written.
    /// </summary>
    [Fact]
    public async Task Second_Invite_For_The_Same_Address_Is_409()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        using var first = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            """{"email":"alice@example.com"}""",
            "invite-1");
        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var second = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            """{"email":"ALICE@example.com"}""",
            "invite-2");

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var envelope = await second.Content.ReadFromJsonAsync<JsonElement>(Token);
        envelope.GetProperty("error").GetProperty("code").GetString().ShouldBe("invite_exists");

        (await CountAsync("select count(*) from public.membership")).ShouldBe(1);
    }

    /// <summary>
    /// 02.1.2 §3a boundary: the accepted address shape is ASCII-only, so a Unicode
    /// homoglyph of an ASCII letter is refused at the edge (422 <c>request_invalid</c>)
    /// and nothing is stored. The shape is what keeps .NET's <c>ToLowerInvariant</c> and
    /// PostgreSQL's locale-aware <c>lower()</c> — the 0011 check
    /// <c>invited_email = lower(invited_email)</c> — from ever disagreeing on a stored
    /// value, so an address the two would normalise differently must not get in.
    /// </summary>
    [Fact]
    public async Task Invite_Refuses_A_Non_Ascii_Homoglyph_Address()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        // Cyrillic 'а' (U+0430) in the local part and in the domain: both look like
        // ASCII 'a' and neither is in the accepted shape.
        var emails = new[] { "\u0430lice@example.com", "alice@ex\u0430mple.com" };

        for (var index = 0; index < emails.Length; index++)
        {
            using var response = await PostAsync(
                client,
                $"/v1/orgs/{org}/memberships",
                $$"""{"email":"{{emails[index]}}"}""",
                $"invite-{index + 1}");

            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            Reasons(await response.Content.ReadFromJsonAsync<JsonElement>(Token)).ShouldContain("invalid_format");
        }

        (await CountAsync("select count(*) from public.membership")).ShouldBe(0);
    }

    /// <summary>
    /// T11: the same <c>Idempotency-Key</c> with the same body replays the stored 201
    /// instead of creating a second invite; a different body under the same key is 409
    /// <c>idempotency_conflict</c>.
    /// </summary>
    [Fact]
    public async Task Invite_Is_Idempotent_Under_Its_Key()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        using var first = await PostAsync(client, $"/v1/orgs/{org}/memberships", """{"email":"alice@example.com"}""", "invite-1");
        var firstView = JsonSerializer.Deserialize<MembershipView>(await first.Content.ReadAsStringAsync(Token), Wire)!;

        using var replay = await PostAsync(client, $"/v1/orgs/{org}/memberships", """{"email":"alice@example.com"}""", "invite-1");
        replay.StatusCode.ShouldBe(HttpStatusCode.Created);
        JsonSerializer.Deserialize<MembershipView>(await replay.Content.ReadAsStringAsync(Token), Wire)!
            .Id.ShouldBe(firstView.Id);

        using var conflicting = await PostAsync(client, $"/v1/orgs/{org}/memberships", """{"email":"bob@example.com"}""", "invite-1");
        conflicting.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await conflicting.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("idempotency_conflict");

        (await CountAsync("select count(*) from public.membership")).ShouldBe(1);
    }
}
