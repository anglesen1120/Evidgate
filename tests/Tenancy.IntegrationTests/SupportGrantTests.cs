using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Real HTTP and database assertions keep approval tied to the authenticated member and revocation final.
/// Owner-seeded grants isolate tenant decisions from the separately served operator request flow.
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed partial class SupportGrantTests : IAsyncLifetime
{
    private readonly PostgresTenancyFixture _fixture;
    private DedicatedDatabase? _dedicated;
    private string? _database;

    public SupportGrantTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // Every test decides state on its own grants, so each owns a migrated database.
    public async ValueTask InitializeAsync() => _dedicated = await _fixture.CreateDedicatedDatabaseAsync();

    public async ValueTask DisposeAsync()
    {
        if (_dedicated is not null)
        {
            await _dedicated.DisposeAsync();
        }
    }

    [Fact]
    public async Task Approve_RecordsTheAuthenticatedMember_AndAuditsTheGrant()
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        var grant = await SeedGrantAsync(org, "requested");

        var approved = await client.SendAsync(
            HttpMethod.Post, $"/v1/support-grants/{grant}/approve", Admin(founder, org), null, NewKey());

        approved.Status.ShouldBe(HttpStatusCode.OK, approved.ErrorMessage);
        approved.ErrorCode.ShouldBeNull("the route answers a bodiless 200, not an envelope");
        (await ColumnAsync<Guid>("approved_by", grant)).ShouldBe(
            founder, "the approver is the principal the request authenticated as");
        (await _fixture.ScalarAsync<bool>(
                _database!,
                $"select approved_at is not null and approved_at >= created_at from public.support_grant where id = '{grant}'"))
            .ShouldBeTrue("the database stamps the decision time; it is never a value the caller picked");

        (await LatestAuditAsync("support_grant.approve")).ShouldBe(
            $"user|{founder:D}|support_grant|{grant:D}|{org:D}",
            "one audit row names the actor, the grant and the tenant");
    }

    [Fact]
    public async Task Approve_IgnoresABodyNamingAnotherApprover()
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        var grant = await SeedGrantAsync(org, "requested");
        var somebodyElse = Guid.CreateVersion7();

        // The route binds no body, so a client cannot name an approver at all: the value is ignored, never honoured.
        var approved = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/support-grants/{grant}/approve",
            Admin(founder, org),
            new { approved_by = somebodyElse, approver = somebodyElse },
            NewKey());

        approved.Status.ShouldBe(HttpStatusCode.OK, approved.ErrorMessage);
        (await ColumnAsync<Guid>("approved_by", grant)).ShouldBe(founder);
        (await _fixture.ScalarAsync<long>(
                _database!,
                $"select count(*) from public.support_grant where id = '{grant}' and approved_by = '{somebodyElse}'"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task Approve_RequesterCannotApproveItsOwnRequest()
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;

        // Reusing the founder as requester isolates the two-person rule from role eligibility.
        var grant = await SeedGrantAsync(org, "requested", operatorUserId: founder);

        var refused = await client.SendAsync(
            HttpMethod.Post, $"/v1/support-grants/{grant}/approve", Admin(founder, org), null, NewKey());

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("approver_must_differ");
        (await ColumnAsync<Guid?>("approved_by", grant)).ShouldBeNull();
        (await AuditCountAsync("support_grant.approve")).ShouldBe(0, "a refused approval changed nothing");
    }

    /// <summary>
    /// The pre-check's own refusal: the caller holds <c>support:approve</c> through a custom role but is not an active
    /// built-in <c>tenant_admin</c>. Distinct from 403 <c>forbidden</c>, which would mean the permission stage refused
    /// the call before the handler ran.
    /// </summary>
    [Fact]
    public async Task Approve_NonAdminHolder_IsRefusedAsNotATenantAdmin()
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        var grant = await SeedGrantAsync(org, "requested");
        var approver = await SeedCustomApproverAsync(org);

        var refused = await client.SendAsync(
            HttpMethod.Post, $"/v1/support-grants/{grant}/approve", Admin(approver, org), null, NewKey());

        refused.Status.ShouldBe(HttpStatusCode.Forbidden, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("approver_not_tenant_admin");
        (await ColumnAsync<Guid?>("approved_by", grant)).ShouldBeNull();
        (await AuditCountAsync("support_grant.approve")).ShouldBe(0);

        // The positive control for the same request: the founder, a tenant_admin, is accepted.
        (await client.SendAsync(HttpMethod.Post, $"/v1/support-grants/{grant}/approve", Admin(founder, org), null, NewKey()))
            .Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Approve_UnknownOrForeignGrant_IsNotFoundAndWritesNothing()
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        var otherFounder = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(client, otherFounder, "beta");
        var foreign = await SeedGrantAsync(otherOrg, "requested");

        var unknown = await client.SendAsync(
            HttpMethod.Post, $"/v1/support-grants/{Guid.CreateVersion7()}/approve", Admin(founder, org), null, NewKey());
        var crossOrg = await client.SendAsync(
            HttpMethod.Post, $"/v1/support-grants/{foreign}/approve", Admin(founder, org), null, NewKey());

        unknown.Status.ShouldBe(HttpStatusCode.NotFound);
        unknown.ErrorCode.ShouldBe("not_found");
        crossOrg.Status.ShouldBe(HttpStatusCode.NotFound, "RLS hides the other tenant's row, so it is indistinguishable");
        crossOrg.ErrorCode.ShouldBe("not_found");
        (await ColumnAsync<Guid?>("approved_by", foreign)).ShouldBeNull();
        (await AuditCountAsync("support_grant.approve")).ShouldBe(0);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("revoked")]
    [InlineData("expired")]
    public async Task Approve_GrantNotAwaitingApproval_IsConflict(string state)
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        var grant = await SeedGrantAsync(org, state, approver: founder);

        var refused = await client.SendAsync(
            HttpMethod.Post, $"/v1/support-grants/{grant}/approve", Admin(founder, org), null, NewKey());

        refused.Status.ShouldBe(HttpStatusCode.Conflict, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("grant_not_requested");
        (await AuditCountAsync("support_grant.approve")).ShouldBe(0);
    }

    [Theory]
    [InlineData("requested")]
    [InlineData("approved")]
    public async Task Revoke_EndsARequestedOrApprovedGrant_AndSecondRevokeIsConflict(string state)
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        var grant = await SeedGrantAsync(org, state, approver: founder);

        var revoked = await client.SendAsync(
            HttpMethod.Post, $"/v1/support-grants/{grant}/revoke", Admin(founder, org), null, NewKey());

        revoked.Status.ShouldBe(HttpStatusCode.OK, revoked.ErrorMessage);
        (await _fixture.ScalarAsync<bool>(
                _database!, $"select revoked_at is not null from public.support_grant where id = '{grant}'"))
            .ShouldBeTrue();
        (await LatestAuditAsync("support_grant.revoke")).ShouldBe(
            $"user|{founder:D}|support_grant|{grant:D}|{org:D}");

        var again = await client.SendAsync(
            HttpMethod.Post, $"/v1/support-grants/{grant}/revoke", Admin(founder, org), null, NewKey());

        again.Status.ShouldBe(HttpStatusCode.Conflict, again.ErrorMessage);
        again.ErrorCode.ShouldBe("grant_already_revoked");
        (await AuditCountAsync("support_grant.revoke")).ShouldBe(1, "the no-op refusal writes no second audit row");
    }

    [Fact]
    public async Task Revoke_IsNotBoundToTheApprover()
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        var grant = await SeedGrantAsync(org, "approved", approver: founder);
        var otherAdmin = await AddMemberAsync(_fixture, _database!, org, "tenant_admin");

        var revoked = await client.SendAsync(
            HttpMethod.Post, $"/v1/support-grants/{grant}/revoke", Admin(otherAdmin, org), null, NewKey());

        revoked.Status.ShouldBe(HttpStatusCode.OK, revoked.ErrorMessage);
        (await LatestAuditAsync("support_grant.revoke"))!.ShouldContain($"|{otherAdmin:D}|");
    }

    [Fact]
    public async Task List_PagesNewestFirst_AndDerivesTheStateInSql()
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;

        // One grant per reachable state, each an hour older than the one seeded before it: the keyset order is
        // therefore exactly this list, and every state word is asserted by id.
        var requested = await SeedGrantAsync(org, "requested");
        var approved = await SeedGrantAsync(org, "approved", approver: founder);
        var revoked = await SeedGrantAsync(org, "revoked");
        var expired = await SeedGrantAsync(org, "expired");

        var first = await GetAsync(client, founder, org, "?limit=2");
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        Items(first).ShouldBe([requested, approved]);
        first.Body.GetProperty("next_cursor").GetString().ShouldNotBeNullOrEmpty("two rows are left");

        var second = await GetAsync(client, founder, org, $"?limit=2&cursor={Uri.EscapeDataString(first.Body.GetProperty("next_cursor").GetString()!)}");
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        Items(second).ShouldBe([revoked, expired]);
        second.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);

        var states = await StatesAsync(client, founder, org);
        states[requested].ShouldBe("requested");
        states[approved].ShouldBe("approved");
        states[revoked].ShouldBe("revoked");
        states[expired].ShouldBe("expired", "expiry is derived, never stored");

        // The decision fields travel with the row: the approver and instant are present once approved, absent before.
        var page = await GetAsync(client, founder, org, string.Empty);
        var approvedRow = Item(page, approved);
        approvedRow.GetProperty("approved_by").GetGuid().ShouldBe(founder);
        approvedRow.GetProperty("approved_at").ValueKind.ShouldBe(JsonValueKind.String);
        Item(page, requested).GetProperty("approved_by").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task List_SeesOnlyItsOwnOrg_AndOtherOrgCannotDecideItsGrants()
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;
        var mine = await SeedGrantAsync(org, "requested");
        var otherFounder = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(client, otherFounder, "beta");
        var theirs = await SeedGrantAsync(otherOrg, "requested");

        (await ItemsAsync(client, otherFounder, otherOrg)).ShouldBe([theirs], "org B's page holds its grant and no other org's");

        // The path names the org and a header cannot override it, so a caller with no membership there is
        // refused at tenant resolution with 403 `forbidden`, and the body never carries the other org's rows.
        var crossed = await client.SendAsync(
            HttpMethod.Get, $"/v1/orgs/{org}/support-grants", Admin(otherFounder, otherOrg));
        crossed.Status.ShouldBe(HttpStatusCode.Forbidden);
        crossed.ErrorCode.ShouldBe("forbidden");
        crossed.Body.GetRawText().ShouldNotContain(mine.ToString("D"));

        (await client.SendAsync(HttpMethod.Post, $"/v1/support-grants/{mine}/revoke", Admin(otherFounder, otherOrg), null, NewKey()))
            .Status.ShouldBe(HttpStatusCode.NotFound);
        (await ColumnAsync<Guid?>("revoked_at", mine)).ShouldBeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("201")]
    [InlineData("abc")]
    [InlineData("-1")]
    public async Task List_OutOfRangeOrUnbindableLimit_IsRefused(string limit)
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;

        var refused = await GetAsync(client, founder, org, $"?limit={limit}");

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("request_invalid");
        refused.Body.GetProperty("error").GetProperty("details")[0].GetProperty("reason").GetString()
            .ShouldBe("out_of_range");
    }

    [Fact]
    public async Task List_BadCursor_IsRefusedAtCursor()
    {
        var (api, client, founder, org) = await StartAsync();
        await using var _ = api;

        var refused = await GetAsync(client, founder, org, "?cursor=not-a-cursor");

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("request_invalid");
        refused.Body.GetProperty("error").GetProperty("details")[0].GetProperty("reason").GetString()
            .ShouldBe("malformed");
    }

    // ---------------------------------------------------------------- helpers

    private async Task<(TenancyApiFactory Api, HttpClient Client, Guid Founder, Guid Org)> StartAsync()
    {
        _database = _dedicated!.Name;
        var api = TenancyApiFactory.Ci(_fixture.AppConnectionString(_database));
        var client = api.CreateClient();
        var founder = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, founder, "acme");

        return (api, client, founder, org);
    }

    private static Action<System.Net.Http.Headers.HttpRequestHeaders> Admin(Guid user, Guid org) =>
        TenancyApiFactory.Resolved(user: user, orgHeader: org);

    private static Task<ApiAnswer> GetAsync(HttpClient client, Guid user, Guid org, string query) =>
        client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/support-grants{query}", Admin(user, org));

    private static List<Guid> Items(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];

    private static async Task<List<Guid>> ItemsAsync(HttpClient client, Guid user, Guid org) =>
        Items(await GetAsync(client, user, org, string.Empty));

    private static async Task<Dictionary<Guid, string>> StatesAsync(HttpClient client, Guid user, Guid org)
    {
        var page = await GetAsync(client, user, org, string.Empty);
        page.Status.ShouldBe(HttpStatusCode.OK, page.ErrorMessage);

        return page.Body.GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetGuid(), item => item.GetProperty("state").GetString()!);
    }

    private static JsonElement Item(ApiAnswer page, Guid id) =>
        page.Body.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == id);

    /// <summary>
    /// Files one grant as the owner, because the request route is the operator's and this host does not serve it.
    /// The state is arranged with real transitions (<c>approved_at</c>, <c>revoked_at</c>) so the list's derived state
    /// reads a stored fact, and the created/expiry instants are spread so the keyset order is deterministic.
    /// </summary>
    /// <param name="org">The tenant the grant opens.</param>
    /// <param name="state"><c>requested</c>, <c>approved</c>, <c>revoked</c> or <c>expired</c>.</param>
    /// <param name="operatorUserId">The operator account; a fresh one unless a test needs the requester to be the approver.</param>
    /// <param name="approver">The member recorded as approver of an <c>approved</c> seed.</param>
    private async Task<Guid> SeedGrantAsync(Guid org, string state, Guid? operatorUserId = null, Guid? approver = null)
    {
        var grant = Guid.CreateVersion7();
        var operatorUser = operatorUserId ?? Guid.CreateVersion7();
        var created = state switch
        {
            "expired" => "now() - interval '4 hours'",
            "revoked" => "now() - interval '3 hours'",
            "approved" => "now() - interval '2 hours'",
            _ => "now() - interval '1 hour'",
        };
        var expires = state == "expired" ? "now() - interval '1 hour'" : "now() + interval '2 hours'";

        var transition = state switch
        {
            "approved" => $"update public.support_grant set approved_by = '{approver ?? throw new ArgumentNullException(nameof(approver))}', approved_at = now() where id = '{grant}';",
            "revoked" => $"update public.support_grant set revoked_at = now() where id = '{grant}';",
            _ => string.Empty,
        };

        // A caller-supplied operator account already exists (the self-approval case reuses a founder), so only a fresh one is inserted.
        var operatorInsert = operatorUserId is null
            ? $"insert into public.\"user\" (id, email) values ('{operatorUser}', 'operator-{operatorUser:N}@example.test');"
            : string.Empty;

        await _fixture.ExecuteAsync(
            _database!,
            $"""
             {operatorInsert}
             insert into public.support_grant (id, org_id, operator_user_id, scopes, project_ids, reason, expires_at, created_at)
             values ('{grant}', '{org}', '{operatorUser}', array['run:read'], ARRAY[]::uuid[], '{state} support grant', {expires}, {created});
             {transition}
             """);

        return grant;
    }

    /// <summary>
    /// A member of the org holding one custom role whose permission set is <c>support:approve</c> only: the holder the
    /// pre-check must refuse. The role is owner-written because the routes that would create it are not this test's
    /// subject, and the seed asserts it wrote what it meant to (an <c>insert … select</c> can write zero rows).
    /// </summary>
    /// <param name="org">The tenant.</param>
    private async Task<Guid> SeedCustomApproverAsync(Guid org)
    {
        var user = Guid.CreateVersion7();
        var role = Guid.CreateVersion7();
        var email = $"custom-approver-{user:N}@example.test";

        await _fixture.ExecuteAsync(
            _database!,
            $"""
             insert into public."user" (id, email) values ('{user}', '{email}');
             insert into public.membership (id, org_id, user_id, invited_email, status)
             values (gen_random_uuid(), '{org}', '{user}', '{email}', 'active');
             insert into public.role (id, org_id, code, name, is_builtin, kind)
             values ('{role}', '{org}', 'support_approver_{role:N}', 'Support approver', false, 'member');
             insert into public.role_permission (id, org_id, role_id, permission_code)
             values (gen_random_uuid(), '{org}', '{role}', 'support:approve');
             insert into public.membership_role (id, org_id, membership_id, role_id)
             select gen_random_uuid(), '{org}', m.id, '{role}'
               from public.membership m
              where m.org_id = '{org}' and m.user_id = '{user}';
             """);

        (await _fixture.ScalarAsync<long>(
                _database!,
                $"""
                 select count(*)
                   from public.membership_role mr
                   join public.role_permission rp on rp.role_id = mr.role_id and rp.org_id = mr.org_id
                  where mr.org_id = '{org}' and rp.permission_code = 'support:approve' and mr.role_id = '{role}'
                 """))
            .ShouldBe(1, "the seeded holder must really carry support:approve, or the refusal proves nothing");

        return user;
    }

    private Task<T> ColumnAsync<T>(string column, Guid grant) =>
        _fixture.ScalarAsync<T>(_database!, $"select {column} from public.support_grant where id = '{grant}'");

    private Task<long> AuditCountAsync(string action) =>
        _fixture.ScalarAsync<long>(_database!, $"select count(*) from public.audit_log where action = '{action}'");

    private Task<string> LatestAuditAsync(string action) =>
        _fixture.ScalarAsync<string>(
            _database!,
            $"""
             select actor_type || '|' || actor_id || '|' || object_type || '|' || object_id || '|' || org_id
               from public.audit_log
              where action = '{action}'
              order by seq desc
              limit 1
             """);
}
