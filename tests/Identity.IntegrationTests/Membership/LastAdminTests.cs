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
/// AC4 over the real Api composition: the org's only active <c>tenant_admin</c> membership can be
/// neither removed (<c>DELETE</c>), demoted (<c>DELETE …/roles/{tenant_admin}</c>) nor disabled
/// (<c>PATCH</c>) — each answers 409 <c>last_admin</c> and writes nothing — and the same requests
/// succeed once a second admin exists. The race half issues both mutations before awaiting either,
/// repeatedly: exactly one wins and the org keeps an admin, which a plain count would not.
/// </summary>
/// <remarks>
/// Rows are arranged on the owner connection; every call under test runs through the real Api
/// composition as <c>evidgate_app</c> under RLS, against the assembly's shared migrated database.
/// Every case counts only the org it seeded, and its addresses are unique to it.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class LastAdminTests : CatalogueTestBase
{
    /// <summary>One lucky interleaving must not pass for the invariant; each round uses a fresh org.</summary>
    private const int RaceIterations = 3;

    public LastAdminTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// The org's last admin cannot be removed: 409 <c>last_admin</c>, the membership stays
    /// <c>active</c> and the org still has one admin. A second admin is the only variable, and the
    /// same request then answers 204 — the refusal is the guard, not the route.
    /// </summary>
    [Fact]
    public async Task Remove_Last_Admin_Is_409_And_Keeps_The_Membership_Active()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (lastAdminId, _) = await SeedActiveMemberAsync(org, Unique("last-remove"), "tenant_admin");

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        using var refused = await DeleteAsync(client, $"/v1/orgs/{org}/memberships/{lastAdminId}", "remove-last-1");

        await AssertLastAdminAsync(refused);
        (await MembershipStatusAsync(lastAdminId)).ShouldBe("active", "a refused removal writes nothing");
        (await ActiveAdminCountAsync(org)).ShouldBe(1);

        await SeedActiveMemberAsync(org, Unique("second-admin"), "tenant_admin");

        using var allowed = await DeleteAsync(client, $"/v1/orgs/{org}/memberships/{lastAdminId}", "remove-last-2");
        allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent, "the second admin is the only thing that changed");
        (await MembershipStatusAsync(lastAdminId)).ShouldBe("removed");
    }

    /// <summary>
    /// The org's last admin cannot lose <c>tenant_admin</c>: 409 <c>last_admin</c>, the grant is still
    /// there. With a second admin the same demotion answers 204 and leaves the org one admin.
    /// </summary>
    [Fact]
    public async Task Demote_Last_Admin_Is_409_And_Keeps_The_Role()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (lastAdminId, _) = await SeedActiveMemberAsync(org, Unique("last-demote"), "tenant_admin");
        var adminRoleId = await RoleIdAsync("tenant_admin");

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        using var refused = await DeleteAsync(
            client,
            $"/v1/orgs/{org}/memberships/{lastAdminId}/roles/{adminRoleId}",
            "demote-last-1");

        await AssertLastAdminAsync(refused);
        (await AdminGrantsAsync(lastAdminId)).ShouldBe(1, "a refused demotion keeps the tenant_admin grant");
        (await ActiveAdminCountAsync(org)).ShouldBe(1);

        await SeedActiveMemberAsync(org, Unique("second-admin"), "tenant_admin");

        using var allowed = await DeleteAsync(
            client,
            $"/v1/orgs/{org}/memberships/{lastAdminId}/roles/{adminRoleId}",
            "demote-last-2");
        allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await AdminGrantsAsync(lastAdminId)).ShouldBe(0);
        (await ActiveAdminCountAsync(org)).ShouldBe(1, "the second admin keeps the org covered");
    }

    /// <summary>
    /// The org's last admin cannot be disabled through the status route either: 409
    /// <c>last_admin</c> and the membership stays <c>active</c>. With a second admin the matching
    /// <c>If-Match</c> answers 200 and the row moves to <c>removed</c>.
    /// </summary>
    [Fact]
    public async Task Disable_Last_Admin_Is_409_And_Keeps_The_Membership_Active()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (lastAdminId, _) = await SeedActiveMemberAsync(org, Unique("last-disable"), "tenant_admin");

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        var version = await VersionAsync(lastAdminId);
        using var refused = await PatchAsync(
            client,
            $"/v1/orgs/{org}/memberships/{lastAdminId}",
            """{"status":"removed"}""",
            version,
            "disable-last-1");

        await AssertLastAdminAsync(refused);
        (await MembershipStatusAsync(lastAdminId)).ShouldBe("active", "a refused disable writes nothing");

        await SeedActiveMemberAsync(org, Unique("second-admin"), "tenant_admin");

        using var allowed = await PatchAsync(
            client,
            $"/v1/orgs/{org}/memberships/{lastAdminId}",
            """{"status":"removed"}""",
            await VersionAsync(lastAdminId),
            "disable-last-2");
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await MembershipStatusAsync(lastAdminId)).ShouldBe("removed");
        (await ActiveAdminCountAsync(org)).ShouldBe(1);
    }

    /// <summary>Two removals of the org's two admins: exactly one wins, the org keeps one admin.</summary>
    [Fact]
    public async Task Concurrent_Removals_Leave_Exactly_One_Active_Admin() =>
        await AssertRaceKeepsOneAdminAsync(
            static (org, membership, key, client) =>
                DeleteAsync(client, $"/v1/orgs/{org}/memberships/{membership}", key),
            static (org, membership, key, client) =>
                DeleteAsync(client, $"/v1/orgs/{org}/memberships/{membership}", key),
            "race-removal");

    /// <summary>Two demotions of the org's two admins: exactly one wins, the org keeps one admin.</summary>
    [Fact]
    public async Task Concurrent_Demotions_Leave_Exactly_One_Active_Admin()
    {
        var adminRoleId = await RoleIdAsync("tenant_admin");

        await AssertRaceKeepsOneAdminAsync(
            (org, membership, key, client) =>
                DeleteAsync(client, $"/v1/orgs/{org}/memberships/{membership}/roles/{adminRoleId}", key),
            (org, membership, key, client) =>
                DeleteAsync(client, $"/v1/orgs/{org}/memberships/{membership}/roles/{adminRoleId}", key),
            "race-demotion");
    }

    /// <summary>
    /// The two routes cross: one request removes an admin while the other demotes the other admin.
    /// Both take the guard's row lock over the same admin set, so exactly one wins and the org keeps
    /// one active <c>tenant_admin</c> whichever path loses.
    /// </summary>
    [Fact]
    public async Task Concurrent_Removal_And_Demotion_Leave_Exactly_One_Active_Admin()
    {
        var adminRoleId = await RoleIdAsync("tenant_admin");

        await AssertRaceKeepsOneAdminAsync(
            static (org, membership, key, client) =>
                DeleteAsync(client, $"/v1/orgs/{org}/memberships/{membership}", key),
            (org, membership, key, client) =>
                DeleteAsync(client, $"/v1/orgs/{org}/memberships/{membership}/roles/{adminRoleId}", key),
            "race-mixed");
    }

    /// <summary>
    /// Seeds two <c>tenant_admin</c> memberships in a fresh org and issues both mutations before
    /// awaiting either, <see cref="RaceIterations"/> times. Each round uses a new org, so a previous
    /// round's winner cannot arrange the next one.
    /// </summary>
    /// <param name="first">The first request, built from the org, its membership and an idempotency key.</param>
    /// <param name="second">The second request, built the same way.</param>
    /// <param name="label">The slug the case's addresses and messages carry.</param>
    private async Task AssertRaceKeepsOneAdminAsync(
        Func<Guid, Guid, string, HttpClient, Task<HttpResponseMessage>> first,
        Func<Guid, Guid, string, HttpClient, Task<HttpResponseMessage>> second,
        string label)
    {
        for (var iteration = 0; iteration < RaceIterations; iteration++)
        {
            var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
            var (firstMembership, _) = await SeedActiveMemberAsync(org, Unique(label + "-a"), "tenant_admin");
            var (secondMembership, _) = await SeedActiveMemberAsync(org, Unique(label + "-b"), "tenant_admin");

            await using var factory = IdentityApiFactory.Start(Fixture, org);
            using var client = factory.CreateClient();
            var round = $"{label} iteration {iteration}";

            // The key is a slug: the label's spaces belong in the assertion messages, not on the wire.
            var raced = await Task.WhenAll(
                first(org, firstMembership, $"{label}-{iteration}-a", client),
                second(org, secondMembership, $"{label}-{iteration}-b", client));

            try
            {
                raced.Count(response => response.StatusCode == HttpStatusCode.NoContent).ShouldBe(
                    1, round + ": exactly one mutation may win the guard's row lock");
                raced.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(
                    1, round + ": the loser must be refused, not silently dropped");

                var refused = raced.Single(response => response.StatusCode == HttpStatusCode.Conflict);
                (await refused.Content.ReadFromJsonAsync<JsonElement>(Token))
                    .GetProperty("error").GetProperty("code").GetString()
                    .ShouldBe("last_admin", round);

                (await ActiveAdminCountAsync(org)).ShouldBe(
                    1, round + ": the org keeps at least one active tenant_admin (AC4)");
            }
            finally
            {
                foreach (var response in raced)
                {
                    response.Dispose();
                }
            }
        }
    }

    private static async Task AssertLastAdminAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("last_admin");
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

    /// <summary>The id of one built-in role of the catalogue seed.</summary>
    private Task<Guid> RoleIdAsync(string code) =>
        ScalarAsync<Guid>("select id from public.role where org_id is null and code = @code", ("code", code));

    private Task<string> MembershipStatusAsync(Guid membershipId) =>
        ScalarAsync<string>("select status from public.membership where id = @id", ("id", membershipId));

    private Task<int> VersionAsync(Guid membershipId) =>
        ScalarAsync<int>("select version from public.membership where id = @id", ("id", membershipId));

    /// <summary>How many <c>tenant_admin</c> grants one membership holds (0 or 1).</summary>
    private Task<long> AdminGrantsAsync(Guid membershipId) =>
        ScalarAsync<long>(
            """
            select count(*)
              from public.membership_role mr
              join public.role r on r.id = mr.role_id
             where mr.membership_id = @membership_id
               and r.code = 'tenant_admin'
            """,
            ("membership_id", membershipId));

    /// <summary>How many active members of the org hold the built-in <c>tenant_admin</c> role (owner read).</summary>
    private Task<long> ActiveAdminCountAsync(Guid orgId) =>
        ScalarAsync<long>(
            """
            select count(*)
              from public.membership m
              join public.membership_role mr on mr.org_id = m.org_id and mr.membership_id = m.id
              join public.role r on r.id = mr.role_id
             where m.org_id = @org_id
               and m.status = 'active'
               and r.code = 'tenant_admin'
            """,
            ("org_id", orgId));

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string path, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> PatchAsync(
        HttpClient client,
        string path,
        string json,
        int ifMatch,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");

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
