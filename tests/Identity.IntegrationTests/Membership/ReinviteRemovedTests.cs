using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Membership;

/// <summary>
/// Inviting an address whose user still holds a <c>removed</c> membership in the org is
/// refused with 409 <c>member_removed</c> and writes no pending row: the row's own
/// <c>membership_org_user_key</c> covers every status, so a second row for that user
/// could never bind. A fresh address, and the same address removed from another org,
/// still receive the ordinary 201 invite.
/// </summary>
/// <remarks>
/// Rows are arranged on the owner connection; every call under test runs through the
/// real Api composition as <c>evidgate_app</c> under RLS, against the assembly's shared
/// migrated database (each case seeds its own org and its own address).
/// </remarks>
[Trait("Category", "Docker")]
public sealed class ReinviteRemovedTests : CatalogueTestBase
{
    public ReinviteRemovedTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// The refusal AC1's second clause adds: a bound member removed from the org cannot
    /// be invited again, whatever the case or surrounding spaces of the address, and the
    /// only row for it is still the <c>removed</c> one.
    /// </summary>
    [Fact]
    public async Task Invite_Of_An_Address_Whose_Membership_Was_Removed_Is_409_And_Writes_No_Row()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await SeedActiveMemberAsync(org, Unique("admin") + "@example.com", "tenant_admin");

        var email = Unique("removed") + "@example.com";
        var (memberId, _) = await SeedActiveMemberAsync(org, email, "viewer");

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        using var removed = await DeleteAsync(client, $"/v1/orgs/{org}/memberships/{memberId}", "remove-1");
        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent, "the positive control: the member is removable");

        using var reinvite = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            JsonSerializer.Serialize(new { email = "  " + email.ToUpperInvariant() + " " }),
            "reinvite-1");

        reinvite.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var envelope = await reinvite.Content.ReadFromJsonAsync<JsonElement>(Token);
        envelope.GetProperty("error").GetProperty("code").GetString().ShouldBe("member_removed");

        (await ScalarAsync<long>(
            "select count(*) from public.membership where org_id = @org_id and invited_email = @email",
            ("org_id", org), ("email", email)))
            .ShouldBe(1, "no pending membership may be created for an address that cannot bind");

        (await ScalarAsync<string>(
            "select status from public.membership where org_id = @org_id and invited_email = @email",
            ("org_id", org), ("email", email)))
            .ShouldBe("removed");
    }

    /// <summary>
    /// The positive control for the refusal above: an address with no membership in the
    /// org is invited as usual, so the 409 is caused by the removed row and nothing else.
    /// </summary>
    [Fact]
    public async Task Invite_Of_A_Fresh_Address_Is_201()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await SeedActiveMemberAsync(org, Unique("admin") + "@example.com", "tenant_admin");

        var email = Unique("fresh") + "@example.com";

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        using var created = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            JsonSerializer.Serialize(new { email }),
            "fresh-1");

        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        (await ScalarAsync<string>(
            "select status from public.membership where org_id = @org_id and invited_email = @email",
            ("org_id", org), ("email", email)))
            .ShouldBe("pending");
    }

    /// <summary>
    /// The refusal is scoped to the org of the removed membership: the same address can
    /// still be invited to an org that never held it.
    /// </summary>
    [Fact]
    public async Task Invite_Of_An_Address_Removed_From_Another_Org_Is_201()
    {
        var otherOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await SeedActiveMemberAsync(otherOrg, Unique("admin") + "@example.com", "tenant_admin");

        var email = Unique("moved") + "@example.com";
        var (memberId, _) = await SeedActiveMemberAsync(otherOrg, email, "viewer");

        await using (var other = IdentityApiFactory.Start(Fixture, otherOrg))
        using (var otherClient = other.CreateClient())
        {
            using var removed = await DeleteAsync(
                otherClient, $"/v1/orgs/{otherOrg}/memberships/{memberId}", "remove-1");
            removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        using var created = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            JsonSerializer.Serialize(new { email }),
            "fresh-org-1");

        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        (await ScalarAsync<string>(
            "select status from public.membership where org_id = @org_id and invited_email = @email",
            ("org_id", org), ("email", email)))
            .ShouldBe("pending");
    }

    /// <summary>An address no other case of this run uses, so the shared database stays isolated.</summary>
    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Seeds one <c>active</c> membership with the named built-in roles, as the owner (arranging rows only).</summary>
    private async Task<(Guid MembershipId, Guid UserId)> SeedActiveMemberAsync(
        Guid orgId,
        string email,
        params string[] roleCodes)
    {
        var membershipId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        // `membership_user_fk` makes the bound membership name a real account, so the
        // account is seeded before the membership.
        await Fixture.SeedAsync(
            """insert into public."user" (id, email) values (@id, @email)""",
            [("id", userId), ("email", email)],
            Token);

        await Fixture.SeedAsync(
            "insert into public.membership (id, org_id, user_id, invited_email, status) values (@id, @org_id, @user_id, @email, 'active')",
            [("id", membershipId), ("org_id", orgId), ("user_id", userId), ("email", email)],
            Token);

        foreach (var code in roleCodes)
        {
            await Fixture.SeedAsync(
                """
                insert into public.membership_role (id, org_id, membership_id, role_id)
                select @id, @org_id, @membership_id, role.id from public.role
                 where role.org_id is null and role.code = @code
                """,
                [
                    ("id", Guid.CreateVersion7()),
                    ("org_id", orgId),
                    ("membership_id", membershipId),
                    ("code", code),
                ],
                Token);
        }

        return (membershipId, userId);
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string path,
        string json,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> DeleteAsync(
        HttpClient client,
        string path,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>One scalar on the owner connection (arranging/asserting rows, not through the API).</summary>
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
}
