using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// The membership routes of 02.1.2 §5 through the real Api composition (Program →
/// ApiHost) over the harness's migrated database, as <c>evidgate_app</c> under RLS:
/// AC1 (invite → pending, duplicate → 409 <c>invite_exists</c>), AC4 (the last active
/// <c>tenant_admin</c> can be neither removed nor demoted), AC7 (a membership of org A
/// is invisible to org B → 404), the <c>Idempotency-Key</c> replay (T11), the
/// <c>ETag</c>/<c>If-Match</c> precondition (00 §9.1) and the 00 §9.2 envelope for a
/// validator failure.
/// </summary>
[Trait("Category", "Docker")]
public sealed class MembershipEndpointTests : CatalogueTestBase
{
    /// <summary>snake_case wire → the module's public DTOs (00 §7 API JSON).</summary>
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    public MembershipEndpointTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

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
    /// serialise and the second re-evaluates the reduced set: exactly one 204, one 409
    /// <c>last_admin</c>, and one active admin left. A plain count would let both
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
            raced.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);

            var refused = raced.Single(response => response.StatusCode == HttpStatusCode.Conflict);
            (await refused.Content.ReadFromJsonAsync<JsonElement>(Token))
                .GetProperty("error").GetProperty("code").GetString().ShouldBe("last_admin");

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
    /// which takes the guard through <c>RemoveMemberCommand</c>. Whichever request loses
    /// the row lock sees the remaining admin as the last one and refuses.
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
            raced.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);

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

    /// <summary>
    /// AC7: a membership of org A is invisible to org B — 404 both when the route names
    /// org B (RLS and the composite key make it unreadable) and when it names org A while
    /// the request runs as org B (the tenant is the principal's, never the URL's) — and
    /// org B's list page does not contain it.
    /// </summary>
    [Fact]
    public async Task Membership_Of_Another_Org_Is_404()
    {
        var orgA = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var orgB = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (membershipOfA, _) = await SeedActiveMemberAsync(orgA, "a@example.com", "viewer");

        await using var factory = IdentityApiFactory.Start(Fixture, orgB);
        using var client = factory.CreateClient();

        using var underB = await client.GetAsync($"/v1/orgs/{orgB}/memberships/{membershipOfA}", Token);
        underB.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await underB.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("not_found");

        using var underA = await client.GetAsync($"/v1/orgs/{orgA}/memberships/{membershipOfA}", Token);
        underA.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the URL org is not the request tenant");

        using var page = await client.GetAsync($"/v1/orgs/{orgB}/memberships", Token);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonSerializer.Deserialize<MembershipListResponse>(await page.Content.ReadAsStringAsync(Token), Wire)!
            .Items.ShouldBeEmpty();
    }

    /// <summary>
    /// 00 §9.2 / T4.1: a validator failure is 422 <c>request_invalid</c> with a
    /// lower_snake <c>reason</c> per field, answered before the tenant transaction opens.
    /// </summary>
    [Fact]
    public async Task Validator_Failures_Are_The_Envelope()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        using var malformed = await PostAsync(client, $"/v1/orgs/{org}/memberships", """{"email":"not-an-address"}""", "invite-1");
        malformed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var malformedEnvelope = await malformed.Content.ReadFromJsonAsync<JsonElement>(Token);
        malformedEnvelope.GetProperty("error").GetProperty("code").GetString().ShouldBe("request_invalid");
        Reasons(malformedEnvelope).ShouldContain("invalid_format");

        using var empty = await PostAsync(client, $"/v1/orgs/{org}/memberships", """{"roles":[]}""", "invite-2");
        empty.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Reasons(await empty.Content.ReadFromJsonAsync<JsonElement>(Token)).ShouldContain("required");

        using var unsupported = await PatchAsync(
            client,
            $"/v1/orgs/{org}/memberships/{Guid.CreateVersion7()}",
            """{"status":"active"}""",
            "1",
            "patch-1");
        unsupported.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Reasons(await unsupported.Content.ReadFromJsonAsync<JsonElement>(Token)).ShouldContain("unsupported_status");
    }

    /// <summary>
    /// 00 §9.1: the disable path is an optimistic-concurrency update — a stale
    /// <c>If-Match</c> is 412 <c>precondition_failed</c>, a missing one is 422, and the
    /// matching one answers 200 with the row in its new state and a fresh <c>ETag</c>.
    /// </summary>
    [Fact]
    public async Task Disable_Requires_A_Matching_If_Match()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (membershipId, _) = await SeedActiveMemberAsync(org, "member@example.com", "viewer");

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        using var stale = await PatchAsync(client, $"/v1/orgs/{org}/memberships/{membershipId}", """{"status":"removed"}""", "99", "patch-1");
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await stale.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("precondition_failed");

        using var missing = await PatchAsync(client, $"/v1/orgs/{org}/memberships/{membershipId}", """{"status":"removed"}""", null, "patch-2");
        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Reasons(await missing.Content.ReadFromJsonAsync<JsonElement>(Token)).ShouldContain("required");

        using var accepted = await PatchAsync(client, $"/v1/orgs/{org}/memberships/{membershipId}", """{"status":"removed"}""", "1", "patch-3");
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonSerializer.Deserialize<MembershipView>(await accepted.Content.ReadAsStringAsync(Token), Wire)!
            .Status.ShouldBe(MembershipStatus.Removed);

        // A repeat with the version the response carried is idempotent: 200, still removed.
        using var repeat = await PatchAsync(client, $"/v1/orgs/{org}/memberships/{membershipId}", """{"status":"removed"}""", "2", "patch-4");
        repeat.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// 02.1.2 §5 (AC5's role half): granting a built-in role adds it to the returned role
    /// set and to the row's grants; revoking it removes it and answers 204; granting a
    /// role the tenant cannot see is 409 <c>unknown_role</c>; revoking a pair that was
    /// never granted is 404.
    /// </summary>
    [Fact]
    public async Task Grant_And_Revoke_Role_Change_The_Role_Set()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (membershipId, _) = await SeedActiveMemberAsync(org, "member@example.com", "viewer");
        // A second admin keeps the last-admin guard out of this case: the membership
        // below becomes an admin too, and revoking one of two admins is allowed (AC4).
        await SeedActiveMemberAsync(org, "other-admin@example.com", "tenant_admin");
        var adminRoleId = await RoleIdAsync("tenant_admin");

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        using var granted = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships/{membershipId}/roles",
            $$"""{"role_id":"{{adminRoleId}}"}""",
            "grant-1");
        granted.StatusCode.ShouldBe(HttpStatusCode.OK);

        var afterGrant = JsonSerializer.Deserialize<MembershipView>(await granted.Content.ReadAsStringAsync(Token), Wire)!;
        afterGrant.Roles.Select(role => role.Code).Order(StringComparer.Ordinal).ShouldBe(["tenant_admin", "viewer"]);

        using var unknown = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships/{membershipId}/roles",
            $$"""{"role_id":"{{Guid.CreateVersion7()}}"}""",
            "grant-2");
        unknown.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await unknown.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("unknown_role");

        using var revoked = await DeleteAsync(
            client,
            $"/v1/orgs/{org}/memberships/{membershipId}/roles/{adminRoleId}",
            "revoke-1");
        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var fetched = await client.GetAsync($"/v1/orgs/{org}/memberships/{membershipId}", Token);
        JsonSerializer.Deserialize<MembershipView>(await fetched.Content.ReadAsStringAsync(Token), Wire)!
            .Roles.Select(role => role.Code).ShouldBe(["viewer"]);

        using var again = await DeleteAsync(
            client,
            $"/v1/orgs/{org}/memberships/{membershipId}/roles/{adminRoleId}",
            "revoke-2");
        again.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the pair is not granted any more");
    }

    /// <summary>
    /// W7-SEC-03 (02.1.2 §5): a staff or service catalogue role can be neither granted to
    /// nor carried into a membership — `platform_operator` is visible to every tenant
    /// (catalogue read) and carries `support:read`, so attaching it to a human would hand
    /// over a staff permission. Both routes answer the existing 409 `unknown_role`, and
    /// nothing is written.
    /// </summary>
    [Fact]
    public async Task Staff_And_Service_Roles_Cannot_Be_Granted_Or_Invited()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (memberId, _) = await SeedActiveMemberAsync(org, "member@example.com", "viewer");
        var staffRoleId = await RoleIdAsync("platform_operator");

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        using var granted = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships/{memberId}/roles",
            $$"""{"role_id":"{{staffRoleId}}"}""",
            "staff-grant-1");

        granted.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await granted.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("unknown_role");

        using var invited = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            $$"""{"email":"staff@example.com","roles":["{{staffRoleId}}"]}""",
            "staff-invite-1");

        invited.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await invited.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("unknown_role");

        (await ScalarAsync<long>(
            "select count(*) from public.membership_role where role_id = @id",
            ("id", staffRoleId))).ShouldBe(0, "the staff role reached no membership");
        (await CountAsync("select count(*) from public.membership"))
            .ShouldBe(1, "the refused invite wrote no membership either");
    }

    /// <summary>
    /// W7-SEC-02 (00 §2): a `tenant_admin` may not grant its own membership a role that
    /// carries a permission it does not hold. `release_approver` owns `policy:write` and
    /// `artifact:read_sensitive`, and `tenant_admin` has neither — so the request is 403
    /// `forbidden` and no `membership_role` row is written, because a refusal that leaves
    /// the escalation behind is worse than no refusal.
    /// </summary>
    [Fact]
    public async Task Tenant_Admin_Cannot_Grant_Release_Approver_To_Itself()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (adminMembershipId, adminUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var releaseApproverId = await RoleIdAsync("release_approver");

        // The module's real evaluator over the real membership: the escalation rule is
        // about the caller's own rows, so a fixed "grant everything" stand-in would prove
        // nothing (this is the shape BuiltinRoleTests uses for its real-admin cases).
        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, Principal: RequestPrincipal.Member(adminUserId)));
        using var client = factory.CreateClient();

        using var response = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships/{adminMembershipId}/roles",
            $$"""{"role_id":"{{releaseApproverId}}"}""",
            "self-grant-1");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("forbidden");

        (await ScalarAsync<long>(
            "select count(*) from public.membership_role where membership_id = @id",
            ("id", adminMembershipId))).ShouldBe(1, "only the seeded tenant_admin grant exists");
    }

    /// <summary>
    /// The rule applies to every target, not only the caller's own membership: a
    /// `tenant_admin` without `policy:write` may not appoint another member as
    /// `release_approver`, or one admin could mint a second approver and defeat the
    /// two-person waiver rule. 403 `forbidden`, no grant written.
    /// </summary>
    [Fact]
    public async Task Tenant_Admin_Cannot_Grant_Release_Approver_To_Another_Member()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (_, adminUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var (memberId, _) = await SeedActiveMemberAsync(org, "member@example.com", "viewer");
        var releaseApproverId = await RoleIdAsync("release_approver");

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, Principal: RequestPrincipal.Member(adminUserId)));
        using var client = factory.CreateClient();

        using var response = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships/{memberId}/roles",
            $$"""{"role_id":"{{releaseApproverId}}"}""",
            "delegated-grant-1");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("forbidden");

        (await ScalarAsync<long>(
            "select count(*) from public.membership_role where membership_id = @id",
            ("id", memberId))).ShouldBe(1, "only the seeded viewer grant exists");
    }

    /// <summary>
    /// The invite half of the same rule: inviting another address with a role the caller
    /// does not fully hold is 403 `forbidden` and creates no membership.
    /// </summary>
    [Fact]
    public async Task Tenant_Admin_Cannot_Invite_Another_Address_As_Release_Approver()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (_, adminUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var releaseApproverId = await RoleIdAsync("release_approver");

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, Principal: RequestPrincipal.Member(adminUserId)));
        using var client = factory.CreateClient();

        using var response = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            $$"""{"email":"second@example.com","roles":["{{releaseApproverId}}"]}""",
            "other-invite-1");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("forbidden");

        (await CountAsync("select count(*) from public.membership"))
            .ShouldBe(1, "the refused invite wrote no membership");
    }

    /// <summary>
    /// Positive control: a caller that holds every permission of the role (a
    /// `tenant_admin` that is also a `release_approver`) may grant that role to another
    /// member and invite another address with it.
    /// </summary>
    [Fact]
    public async Task Caller_Holding_Every_Permission_Of_The_Role_May_Grant_And_Invite_It()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (_, callerUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin", "release_approver");
        var (memberId, _) = await SeedActiveMemberAsync(org, "member@example.com", "viewer");
        var releaseApproverId = await RoleIdAsync("release_approver");

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, Principal: RequestPrincipal.Member(callerUserId)));
        using var client = factory.CreateClient();

        using var grant = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships/{memberId}/roles",
            $$"""{"role_id":"{{releaseApproverId}}"}""",
            "holder-grant-1");

        grant.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonSerializer.Deserialize<MembershipView>(await grant.Content.ReadAsStringAsync(Token), Wire)!
            .Roles.Select(role => role.Code).Order(StringComparer.Ordinal)
            .ShouldBe(["release_approver", "viewer"]);

        using var invite = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            $$"""{"email":"second@example.com","roles":["{{releaseApproverId}}"]}""",
            "holder-invite-1");

        invite.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>
    /// W7-SEC-02, the invite half: an invite addressed to the caller's own membership is
    /// the same self-grant — the address is the only handle a membership row has before
    /// the login binds it to a user. Same rule, same 403 `forbidden`, no membership
    /// created.
    /// </summary>
    [Fact]
    public async Task Inviting_The_Callers_Own_Address_To_A_Role_They_Do_Not_Hold_Is_403()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (_, adminUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var releaseApproverId = await RoleIdAsync("release_approver");

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, Principal: RequestPrincipal.Member(adminUserId)));
        using var client = factory.CreateClient();

        using var response = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            $$"""{"email":"admin@example.com","roles":["{{releaseApproverId}}"]}""",
            "self-invite-1");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("forbidden");

        (await CountAsync("select count(*) from public.membership"))
            .ShouldBe(1, "the refused invite wrote no membership");
    }

    /// <summary>
    /// W7-SEC-02, the two-step form of the invite half: an invite to the caller's own
    /// address with no roles passes (it grants nothing), but a later grant onto that
    /// pending row is still a self-grant — the row binds to the caller — so it is the same
    /// 403 `forbidden` and no grant is written.
    /// </summary>
    [Fact]
    public async Task Granting_A_Role_To_A_Pending_Invite_For_The_Callers_Own_Address_Is_403()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var (_, adminUserId) = await SeedActiveMemberAsync(org, "admin@example.com", "tenant_admin");
        var releaseApproverId = await RoleIdAsync("release_approver");

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, Principal: RequestPrincipal.Member(adminUserId)));
        using var client = factory.CreateClient();

        using var invite = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            """{"email":"  Admin@Example.com ","roles":[]}""",
            "self-invite-empty-1");

        invite.StatusCode.ShouldBe(HttpStatusCode.Created);
        var pendingId = JsonSerializer.Deserialize<MembershipView>(await invite.Content.ReadAsStringAsync(Token), Wire)!.Id;

        using var grant = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships/{pendingId}/roles",
            $$"""{"role_id":"{{releaseApproverId}}"}""",
            "self-pending-grant-1");

        grant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await grant.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("forbidden");

        (await ScalarAsync<long>(
            "select count(*) from public.membership_role where membership_id = @id",
            ("id", pendingId))).ShouldBe(0, "the refused grant wrote nothing onto the pending row");
    }

    /// <summary>Every <c>error.details[].reason</c> of an envelope.</summary>
    private static List<string> Reasons(JsonElement envelope) =>
    [
        .. envelope.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetProperty("reason").GetString()!),
    ];

    /// <summary>
    /// 00 §9.1: the page walks the <c>(created_at, id)</c> keyset — <c>?limit=</c> bounds it,
    /// <c>next_cursor</c> continues it without overlap, and a cursor this endpoint did not
    /// issue is 422 rather than a silently different page.
    /// </summary>
    [Fact]
    public async Task List_Pages_On_The_Created_Id_Cursor()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);

        for (var index = 0; index < 3; index++)
        {
            await SeedActiveMemberAsync(org, $"member{index}@example.com", "viewer");
        }

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        var first = JsonSerializer.Deserialize<MembershipListResponse>(
            await client.GetStringAsync($"/v1/orgs/{org}/memberships?limit=2", Token), Wire)!;

        first.Items.Count.ShouldBe(2);
        first.NextCursor.ShouldNotBeNull("a third row exists, so the page continues");

        var second = JsonSerializer.Deserialize<MembershipListResponse>(
            await client.GetStringAsync(
                $"/v1/orgs/{org}/memberships?limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}",
                Token),
            Wire)!;

        second.Items.Count.ShouldBe(1);
        second.NextCursor.ShouldBeNull();
        second.Items.Select(item => item.Id).ShouldNotContain(id => first.Items.Any(item => item.Id == id));
        first.Items.Select(item => item.CreatedAt).ShouldBe(
            first.Items.Select(item => item.CreatedAt).Order(),
            "the page is (created_at, id)-ordered");

        using var malformed = await client.GetAsync($"/v1/orgs/{org}/memberships?cursor=not-a-cursor", Token);
        malformed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await malformed.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("request_invalid");

        // 00 §9.1's cap is the same contract on this route as on the catalogue pages.
        foreach (var limit in new[] { "0", "201", "not-a-number" })
        {
            using var outOfRange = await client.GetAsync($"/v1/orgs/{org}/memberships?limit={limit}", Token);

            outOfRange.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await outOfRange.Content.ReadFromJsonAsync<JsonElement>(Token))
                .GetProperty("error").GetProperty("details").EnumerateArray()
                .ShouldContain(detail => detail.GetProperty("path").GetString() == "limit");
        }
    }

    /// <summary>
    /// 02.1.2 §6: a <c>pending</c> invite is cancelled through 02.1.2-T2's route, so neither
    /// the admin removal path nor the disable path may turn it into <c>removed</c> — both
    /// answer 409 <c>invalid_transition</c> and the row keeps its status.
    /// </summary>
    [Fact]
    public async Task Pending_Invite_Cannot_Be_Removed_Through_The_Member_Routes()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var inviteId = await SeedPendingInviteAsync(org, "invitee@example.com");

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        using var removed = await DeleteAsync(client, $"/v1/orgs/{org}/memberships/{inviteId}", "remove-1");
        removed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await removed.Content.ReadFromJsonAsync<JsonElement>(Token))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("invalid_transition");

        using var disabled = await PatchAsync(client, $"/v1/orgs/{org}/memberships/{inviteId}", """{"status":"removed"}""", "1", "patch-1");
        disabled.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await ScalarAsync<string>("select status from public.membership where id = @id", ("id", inviteId)))
            .ShouldBe(MembershipStatus.Pending);
    }

    /// <summary>Seeds one pending invite and returns its membership id, as the owner.</summary>
    private async Task<Guid> SeedPendingInviteAsync(Guid orgId, string email)
    {
        var membershipId = Guid.CreateVersion7();

        await Fixture.SeedAsync(
            "insert into public.membership (id, org_id, invited_email, status) values (@id, @org_id, @email, 'pending')",
            [("id", membershipId), ("org_id", orgId), ("email", email)],
            Token);

        return membershipId;
    }

    private static async Task AssertLastAdminAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("error").GetProperty("code").GetString().ShouldBe("last_admin");
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string json, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> PatchAsync(
        HttpClient client,
        string path,
        string json,
        string? ifMatch,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string path, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Seeds one <c>active</c> membership with the named built-in roles, as the owner (arranging rows only).</summary>
    private async Task<(Guid MembershipId, Guid UserId)> SeedActiveMemberAsync(Guid orgId, string email, params string[] roleCodes)
    {
        var membershipId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        // 0044's `membership_user_fk` (membership.user_id -> public."user") makes the bound
        // membership name a real account, so the account is seeded before the membership.
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

    /// <summary>The id of one built-in role of 0010's seed.</summary>
    private Task<Guid> RoleIdAsync(string code) =>
        ScalarAsync<Guid>("select id from public.role where org_id is null and code = @code", ("code", code));

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

    private Task<long> CountAsync(string sql) => ScalarAsync<long>(sql);
}
