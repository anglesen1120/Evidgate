using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// The last-admin guard and its races (02.1.2 §8, AC4): the refusal on every route that
/// could leave the org without an active <c>tenant_admin</c>, and the concurrent pairs
/// whose losing request is refused or replayed instead of surfacing as a 500. The rest of
/// the suite is in <c>MembershipEndpointTests.cs</c>.
/// </summary>
public sealed partial class MembershipEndpointTests
{
    /// <summary>
    /// AC4: the org's last active <c>tenant_admin</c> can be neither removed
    /// (<c>DELETE</c>) nor demoted (<c>DELETE …/roles/{tenant_admin}</c>) nor disabled
    /// (<c>PATCH</c>) — 409 <c>last_admin</c> each time, and the membership stays active.
    /// Once a second admin exists, the first one is removable.
    /// </summary>
    [Fact]
    public async Task Last_Admin_Cannot_Be_Removed_Or_Demoted()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (adminId, _) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var adminRoleId = await RoleIdAsync("tenant_admin");

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        using var removed = await DeleteAsync(client, $"/v1/orgs/{org}/memberships/{adminId}", "remove-1");
        await AssertLastAdminAsync(removed);

        using var demoted = await DeleteAsync(client, $"/v1/orgs/{org}/memberships/{adminId}/roles/{adminRoleId}", "demote-1");
        await AssertLastAdminAsync(demoted);

        using var disabled = await PatchAsync(client, $"/v1/orgs/{org}/memberships/{adminId}", """{"status":"removed"}""", "1", "disable-1");
        await AssertLastAdminAsync(disabled);

        (await CountAsync("select count(*) from public.membership where status = 'active'")).ShouldBe(1);

        // A second admin makes the first one removable (the org keeps at least one).
        await SeedActiveMemberAsync(org, "admin2@example.com", "tenant_admin");

        using var nowAllowed = await DeleteAsync(client, $"/v1/orgs/{org}/memberships/{adminId}", "remove-2");
        nowAllowed.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var fetched = await client.GetAsync($"/v1/orgs/{org}/memberships/{adminId}", Token);
        JsonSerializer.Deserialize<MembershipView>(await fetched.Content.ReadAsStringAsync(Token), Wire)!
            .Status.ShouldBe(MembershipStatus.Removed);
    }

    /// <summary>
    /// AC4 concurrency — demotion (02.1.2 §8): two requests demote the org's two admins
    /// in parallel. <c>ActiveMembershipsHoldingRole</c> takes <c>SELECT … FOR UPDATE</c>
    /// over the org's active <c>tenant_admin</c> rows, so the two tenant transactions
    /// serialise and the second re-evaluates the reduced set: exactly one 204, the loser
    /// refused (409 <c>last_admin</c>, or 403 when the winner's commit already removed the
    /// caller's own admin row), and one active admin left. A plain count would let both
    /// through and leave the org with none.
    /// </summary>
    [Fact]
    public async Task Concurrent_Double_Demotion_Keeps_One_Admin()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (firstId, _) = await SeedActiveMemberAsync(org, "admin-a@example.com", "tenant_admin");
        var (secondId, _) = await SeedActiveMemberAsync(org, "admin-b@example.com", "tenant_admin");
        var adminRoleId = await RoleIdAsync("tenant_admin");

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        // Both requests are issued before either is awaited, so their transactions
        // really overlap in the database.
        var raced = await Task.WhenAll(
            DeleteAsync(client, $"/v1/orgs/{org}/memberships/{firstId}/roles/{adminRoleId}", "race-1"),
            DeleteAsync(client, $"/v1/orgs/{org}/memberships/{secondId}/roles/{adminRoleId}", "race-2"));

        try
        {
            raced.Count(response => response.StatusCode == HttpStatusCode.NoContent).ShouldBe(1);

            var refused = raced.Single(response => response.StatusCode != HttpStatusCode.NoContent);
            (refused.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Forbidden).ShouldBeTrue(
                "the loser is refused, not silently dropped");

            var code = (await refused.Content.ReadFromJsonAsync<JsonElement>(Token))
                .GetProperty("error").GetProperty("code").GetString();
            (code is "last_admin" or "forbidden").ShouldBeTrue("refused as last_admin or forbidden, got " + code);

            (await ActiveAdminCountAsync(org)).ShouldBe(1, "the org keeps at least one active tenant_admin (AC4)");
        }
        finally
        {
            foreach (var response in raced)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// AC4 concurrency — removal: same race on the <c>DELETE …/memberships/{m}</c> path,
    /// which takes the guard through <c>RemoveMemberCommand</c>. Whichever request loses is
    /// refused: by the guard seeing the remaining admin as the last one, or by the rank
    /// comparison once the winner's commit removed the caller's own admin row.
    /// </summary>
    [Fact]
    public async Task Concurrent_Removal_Of_Both_Admins_Keeps_One()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (firstId, _) = await SeedActiveMemberAsync(org, "admin-a@example.com", "tenant_admin");
        var (secondId, _) = await SeedActiveMemberAsync(org, "admin-b@example.com", "tenant_admin");

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        var raced = await Task.WhenAll(
            DeleteAsync(client, $"/v1/orgs/{org}/memberships/{firstId}", "race-1"),
            DeleteAsync(client, $"/v1/orgs/{org}/memberships/{secondId}", "race-2"));

        try
        {
            raced.Count(response => response.StatusCode == HttpStatusCode.NoContent).ShouldBe(1);
            raced.Count(response => response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Forbidden)
                .ShouldBe(1, "the loser is refused by the guard or by the rank comparison");

            (await ActiveAdminCountAsync(org)).ShouldBe(1);
            (await CountAsync("select count(*) from public.membership where status = 'active'")).ShouldBe(1);
        }
        finally
        {
            foreach (var response in raced)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// Idempotency under true concurrency (00 §9.1, 02.1.2 §5): two <c>DELETE</c>
    /// requests for the same membership with different <c>Idempotency-Key</c>s (so no
    /// store replay) both answer 204 — the loser's optimistic UPDATE matches no row and
    /// it re-reads the now-<c>removed</c> row. Before the guard existed the loser raised
    /// <c>DbUpdateConcurrencyException</c> and the client got 500 <c>internal_error</c>.
    /// </summary>
    [Fact]
    public async Task Concurrent_Double_Remove_Of_The_Same_Member_Is_204_For_Both()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var (memberId, _) = await SeedActiveMemberAsync(org, "viewer@example.com", "viewer");

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        var raced = await Task.WhenAll(
            DeleteAsync(client, $"/v1/orgs/{org}/memberships/{memberId}", "dup-1"),
            DeleteAsync(client, $"/v1/orgs/{org}/memberships/{memberId}", "dup-2"));

        try
        {
            raced.ShouldAllBe(response => response.StatusCode == HttpStatusCode.NoContent);
            (await ScalarAsync<string>("select status from public.membership where id = @id", ("id", memberId)))
                .ShouldBe(MembershipStatus.Removed);
        }
        finally
        {
            foreach (var response in raced)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// The same race on the revoke route: two concurrent revokes of one granted pair —
    /// one 204, and the loser answers 404, which is the serial replay's verdict for a
    /// pair that is no longer granted (never 500 <c>internal_error</c>).
    /// </summary>
    [Fact]
    public async Task Concurrent_Double_Revoke_Of_The_Same_Pair_Is_204_And_404()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var (memberId, _) = await SeedActiveMemberAsync(org, "viewer@example.com", "viewer");
        var viewerRoleId = await RoleIdAsync("viewer");

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        var raced = await Task.WhenAll(
            DeleteAsync(client, $"/v1/orgs/{org}/memberships/{memberId}/roles/{viewerRoleId}", "dup-1"),
            DeleteAsync(client, $"/v1/orgs/{org}/memberships/{memberId}/roles/{viewerRoleId}", "dup-2"));

        try
        {
            raced.Count(response => response.StatusCode == HttpStatusCode.NoContent).ShouldBe(1);
            raced.Count(response => response.StatusCode == HttpStatusCode.NotFound).ShouldBe(1);
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
