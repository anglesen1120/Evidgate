using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
public sealed partial class MembershipEndpointTests : CatalogueTestBase
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
            // An insert-select over a code the catalogue does not hold writes nothing and raises
            // nothing, so the arrangement says so: a refusal test would otherwise pass for a
            // member holding no role at all.
            (await Fixture.SeedAsync(
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
                Token)).ShouldBe(1, $"the fixture seeded the role '{code}' once for user {userId:D}");
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
