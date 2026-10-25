using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Independent QA of 07.3.1-T1 (waiver table, states, endpoints, approver ≠ requester), tier T3.
/// Negative, boundary and exploratory checks the author's <see cref="WaiverTests"/> does not pin:
/// expiry/reason/scope boundaries, mass-assignment, RBAC for the non-waiver roles, the service-token
/// ceiling, concurrent revoke, the app-role column grants and guard, the owner-run expiry, audit-row
/// accounting, keyset stability with equal <c>created_at</c>, cross-project cursor safety and the
/// no-tenant RLS read. Written against the real host and a real PostgreSQL; it pins the HTTP/DB
/// contract, never internals.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class WaiverQaTests
{
    private readonly PostgresTenancyFixture _fixture;

    public WaiverQaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    // ------------------------------------------------------------------ helpers

    private static Action<HttpRequestHeaders> As(Guid user, Guid org) => TenancyApiFactory.Resolved(user: user, orgHeader: org);

    private static object Body(
        World world,
        string scope = "case",
        string reason = "known failing case",
        DateTimeOffset? expiresAt = null,
        Guid? project = null,
        Guid? target = null) => new
        {
            project_id = project ?? world.Project,
            scope,
            target_id = target ?? Guid.CreateVersion7(),
            reason,
            expires_at = expiresAt ?? DateTimeOffset.UtcNow.AddDays(7),
        };

    private static Task<ApiAnswer> CreateAsync(World world, object body, Action<HttpRequestHeaders>? caller = null, string? key = null) =>
        world.Client.SendAsync(HttpMethod.Post, "/v1/waivers", caller ?? world.Admin, body, key ?? NewKey());

    private static Task<ApiAnswer> ApproveAsync(World world, Guid waiver, Action<HttpRequestHeaders>? caller = null, string? key = null) =>
        world.Client.SendAsync(HttpMethod.Post, $"/v1/waivers/{waiver}/approve", caller ?? world.Admin, null, key ?? NewKey());

    private static Task<ApiAnswer> RevokeAsync(World world, Guid waiver, Action<HttpRequestHeaders>? caller = null, string? key = null) =>
        world.Client.SendAsync(HttpMethod.Post, $"/v1/waivers/{waiver}/revoke", caller ?? world.Admin, null, key ?? NewKey());

    private static Task<ApiAnswer> ListAsync(World world, Guid project, string query = "", Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/waivers{query}", caller ?? world.Admin);

    /// <summary>An active member of the org holding the built-in role, seeded as the owner then granted through the API.</summary>
    private async Task<Guid> AddMemberAsync(World world, string roleCode)
    {
        var userId = Guid.CreateVersion7();
        var membershipId = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public."user" (id, email) values ('{userId}', 'member-{userId:N}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{membershipId}', '{world.Org}', '{userId}', 'member-{userId:N}@example.test', 'active')
            """);

        var role = await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.role where code = '{roleCode}' and org_id is null");

        var granted = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/orgs/{world.Org}/memberships/{membershipId}/roles",
            world.Admin,
            new { role_id = role },
            NewKey());
        granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);

        return userId;
    }

    /// <summary>Inserts a waiver in a chosen state as the owner (the migration role), bypassing the API.</summary>
    private async Task<Guid> InsertWaiverAsync(
        World world,
        string state,
        Guid? approvedBy,
        DateTimeOffset expiresAt,
        DateTimeOffset? createdAt = null,
        Guid? project = null)
    {
        var id = Guid.CreateVersion7();
        var approved = approvedBy is { } a ? $"'{a}'" : "null";
        var created = createdAt is { } c ? $"'{c.UtcDateTime:O}'" : "now()";

        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            $"""
            insert into public.waiver (id, org_id, project_id, scope, target_id, reason, requested_by, approved_by, state, expires_at, created_at)
            values ('{id}', '{world.Org}', '{project ?? world.Project}', 'case', '{Guid.CreateVersion7()}', 'qa seed', '{world.User}', {approved}, '{state}', '{expiresAt.UtcDateTime:O}', {created})
            """)).ShouldBe(1);
        await owner.CommitAsync();

        return id;
    }

    private async Task<long> AuditCountAsync(World world, string action) =>
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.audit_log where action = '{action}'");

    private static (string Path, string Reason) Detail(ApiAnswer answer, int index = 0)
    {
        var detail = answer.Body.GetProperty("error").GetProperty("details")[index];

        return (detail.GetProperty("path").GetString()!, detail.GetProperty("reason").GetString()!);
    }

    private static IReadOnlyList<Guid> Ids(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];

    // ------------------------------------------------------------------ boundaries: expiry, reason, scope

    [Fact]
    public async Task Validation_Boundaries_ExpiryReasonScope()
    {
        await using var world = await StartAsync();

        // expires_at exactly now (and any earlier instant) is not strictly in the future.
        var exactlyNow = await CreateAsync(world, Body(world, expiresAt: DateTimeOffset.UtcNow));
        exactlyNow.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(exactlyNow).ShouldBe(("expires_at", "not_in_future"));

        // A far-future expiry is accepted and stored.
        var far = await CreateAsync(world, Body(world, expiresAt: DateTimeOffset.UtcNow.AddYears(100)));
        far.Status.ShouldBe(HttpStatusCode.Created, far.ErrorMessage);

        // reason is capped at exactly 2000 characters (waiver_reason_check).
        var atMax = await CreateAsync(world, Body(world, reason: new string('x', 2000)));
        atMax.Status.ShouldBe(HttpStatusCode.Created, atMax.ErrorMessage);

        var overMax = await CreateAsync(world, Body(world, reason: new string('x', 2001)));
        overMax.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(overMax).ShouldBe(("reason", "too_long"));

        // The scope list is closed and case-sensitive.
        var casing = await CreateAsync(world, Body(world, scope: "Case"));
        casing.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(casing).ShouldBe(("scope", "unknown"));

        // The contract is 1..2000 characters with no NUL and no trim rule, so a whitespace-only
        // reason is contract-valid; recorded here so a future trim rule is a deliberate change.
        var blank = await CreateAsync(world, Body(world, reason: "   "));
        blank.Status.ShouldBe(HttpStatusCode.Created, blank.ErrorMessage);

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.waiver")).ShouldBe(3);
    }

    [Fact]
    public async Task UnknownJsonFields_AreIgnored_AndCannotSmuggleState()
    {
        await using var world = await StartAsync();

        // A client must not be able to mass-assign `state`/`approved_by` through the create body.
        var answer = await CreateAsync(world, new
        {
            project_id = world.Project,
            scope = "case",
            target_id = Guid.CreateVersion7(),
            reason = "qa",
            expires_at = DateTimeOffset.UtcNow.AddDays(1),
            state = "approved",
            approved_by = Guid.CreateVersion7(),
            unexpected = "ignored",
        });

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        answer.Text("state").ShouldBe("requested");
        answer.Body.GetProperty("approved_by").ValueKind.ShouldBe(JsonValueKind.Null);
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.waiver where id = '{answer.Id()}'")).ShouldBe("requested");
    }

    // ------------------------------------------------------------------ RBAC and the service-token ceiling

    [Fact]
    public async Task MembersWithoutPolicyWrite_CannotApproveOrRevoke()
    {
        await using var world = await StartAsync();
        var id = (await CreateAsync(world, Body(world))).Id();
        var maintainer = await AddMemberAsync(world, "test_maintainer");
        var viewer = await AddMemberAsync(world, "viewer");

        var byMaintainer = await ApproveAsync(world, id, As(maintainer, world.Org));
        byMaintainer.Status.ShouldBe(HttpStatusCode.Forbidden);
        byMaintainer.ErrorCode.ShouldBe("forbidden");

        var byViewer = await ApproveAsync(world, id, As(viewer, world.Org));
        byViewer.Status.ShouldBe(HttpStatusCode.Forbidden);

        var revokeByViewer = await RevokeAsync(world, id, As(viewer, world.Org));
        revokeByViewer.Status.ShouldBe(HttpStatusCode.Forbidden);

        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.waiver where id = '{id}'")).ShouldBe("requested");
        (await AuditCountAsync(world, "waiver.approve")).ShouldBe(0);
    }

    [Fact]
    public async Task ServiceToken_CannotHoldPolicyWrite()
    {
        await using var world = await StartAsync();

        // `policy:write` is a staff permission, outside the service-role ceiling (ci_producer/agent):
        // a service token claiming it is malformed and holds nothing, so the write is refused.
        var service = TenancyApiFactory.Resolved(
            serviceOrg: world.Org,
            serviceProject: world.Project,
            servicePermissions: ["policy:write"]);

        var create = await CreateAsync(world, Body(world), service);
        create.Status.ShouldBe(HttpStatusCode.Forbidden);

        var id = (await CreateAsync(world, Body(world))).Id();
        var approve = await ApproveAsync(world, id, service);
        approve.Status.ShouldBe(HttpStatusCode.Forbidden);

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.waiver")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ concurrency and expiry

    [Fact]
    public async Task ConcurrentDoubleRevoke_OneSucceedsOne409()
    {
        await using var world = await StartAsync();
        var id = (await CreateAsync(world, Body(world))).Id();
        var approver = await AddMemberAsync(world, "release_approver");

        var answers = await Task.WhenAll(
            RevokeAsync(world, id, As(approver, world.Org)),
            RevokeAsync(world, id, As(approver, world.Org)));

        answers.Count(answer => answer.Status == HttpStatusCode.OK).ShouldBe(1);
        answers.Count(answer => answer.Status == HttpStatusCode.Conflict).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.waiver where id = '{id}'")).ShouldBe("revoked");
        (await AuditCountAsync(world, "waiver.revoke")).ShouldBe(1);
    }

    [Fact]
    public async Task Revoke_OfAnApprovedWaiverWhoseExpiryPassed_Is409()
    {
        await using var world = await StartAsync();
        var approver = await AddMemberAsync(world, "release_approver");
        var id = await InsertWaiverAsync(world, "approved", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(-1));

        var revoke = await RevokeAsync(world, id, As(approver, world.Org));

        revoke.Status.ShouldBe(HttpStatusCode.Conflict);
        revoke.ErrorCode.ShouldBe("waiver_state_conflict");
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.waiver where id = '{id}'")).ShouldBe("approved");
    }

    // ------------------------------------------------------------------ the app role's grants and the guard

    [Fact]
    public async Task AppRole_CannotUpdateReasonExpiryOrRequester()
    {
        await using var world = await StartAsync();
        var id = (await CreateAsync(world, Body(world))).Id();

        foreach (var assignment in new[]
        {
            "reason = 'changed'",
            "expires_at = now() + interval '1 day'",
            $"requested_by = '{Guid.CreateVersion7()}'",
        })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
            (await scope.CaptureErrorAsync($"update public.waiver set {assignment} where id = '{id}'"))
                .SqlState.ShouldBe("42501");
        }
    }

    [Fact]
    public async Task AppRole_CannotMoveRequestedToExpired()
    {
        await using var world = await StartAsync();
        var id = (await CreateAsync(world, Body(world))).Id();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync($"update public.waiver set state = 'expired' where id = '{id}'");

        error.SqlState.ShouldBe("23514");
        error.MessageText.ShouldContain("waiver");
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.waiver where id = '{id}'")).ShouldBe("requested");
    }

    [Fact]
    public async Task OwnerRole_CanMoveApprovedToExpired()
    {
        await using var world = await StartAsync();
        var id = await InsertWaiverAsync(world, "approved", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(-1));

        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync($"update public.waiver set state = 'expired' where id = '{id}'")).ShouldBe(1);
        await owner.CommitAsync();

        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.waiver where id = '{id}'")).ShouldBe("expired");
    }

    // ------------------------------------------------------------------ audit accounting

    [Fact]
    public async Task Audit_OneRowPerSuccessfulMutation_NoneOn403Or409()
    {
        await using var world = await StartAsync();
        var approver = await AddMemberAsync(world, "release_approver");

        var id = (await CreateAsync(world, Body(world))).Id();
        (await AuditCountAsync(world, "waiver.create")).ShouldBe(1);

        // 403 self-approval: no write, no audit.
        (await ApproveAsync(world, id, world.Admin)).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await AuditCountAsync(world, "waiver.approve")).ShouldBe(0);

        (await ApproveAsync(world, id, As(approver, world.Org))).Status.ShouldBe(HttpStatusCode.OK);
        (await AuditCountAsync(world, "waiver.approve")).ShouldBe(1);

        // 409 second approval: still one audit row.
        (await ApproveAsync(world, id, As(approver, world.Org))).Status.ShouldBe(HttpStatusCode.Conflict);
        (await AuditCountAsync(world, "waiver.approve")).ShouldBe(1);

        (await RevokeAsync(world, id, As(approver, world.Org))).Status.ShouldBe(HttpStatusCode.OK);
        (await AuditCountAsync(world, "waiver.revoke")).ShouldBe(1);

        // 409 second revoke: still one audit row.
        (await RevokeAsync(world, id, As(approver, world.Org))).Status.ShouldBe(HttpStatusCode.Conflict);
        (await AuditCountAsync(world, "waiver.revoke")).ShouldBe(1);
    }

    [Fact]
    public async Task IdempotentReplay_AuditsOnce()
    {
        await using var world = await StartAsync();
        var key = NewKey();
        var body = Body(world);

        (await CreateAsync(world, body, key: key)).Status.ShouldBe(HttpStatusCode.Created);
        (await CreateAsync(world, body, key: key)).Status.ShouldBe(HttpStatusCode.Created);

        (await AuditCountAsync(world, "waiver.create")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.waiver")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ the keyset list

    [Fact]
    public async Task List_IsStableAndOrderedByCreatedAtThenId_WithEqualCreatedAt()
    {
        await using var world = await StartAsync();
        var at = DateTimeOffset.UtcNow.AddMinutes(-5);
        for (var i = 0; i < 3; i++)
        {
            await InsertWaiverAsync(world, "requested", null, DateTimeOffset.UtcNow.AddDays(1), createdAt: at);
        }

        var first = await ListAsync(world, world.Project, "?limit=50");
        var second = await ListAsync(world, world.Project, "?limit=50");
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        Ids(first).ShouldBe(Ids(second));

        // The API order must match the database's own (created_at, id) order, so the id tiebreaker
        // is really in the statement and not left to the planner.
        var expected = await _fixture.ColumnAsync<Guid>(
            world.Database,
            $"select id from public.waiver where project_id = '{world.Project}' order by created_at, id");
        Ids(first).ShouldBe(expected);

        // Paging one row at a time over equal created_at must walk the same order with no skip or repeat.
        var walked = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var query = "?limit=1" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await ListAsync(world, world.Project, query);
            page.Status.ShouldBe(HttpStatusCode.OK, page.ErrorMessage);
            walked.AddRange(Ids(page));
            cursor = page.Body.GetProperty("next_cursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        walked.ShouldBe(expected);
    }

    [Fact]
    public async Task Cursor_FromAnotherProject_NeverLeaksItsRows_AndMalformedCursorIs422()
    {
        await using var world = await StartAsync();
        var a1 = (await CreateAsync(world, Body(world))).Id();
        var a2 = (await CreateAsync(world, Body(world))).Id();
        var projectB = await CreateProjectAsync(world.Client, world.User, world.Org, "other");
        var b1 = (await CreateAsync(world, Body(world, project: projectB))).Id();

        var firstPage = await ListAsync(world, world.Project, "?limit=1");
        firstPage.Status.ShouldBe(HttpStatusCode.OK, firstPage.ErrorMessage);
        var cursor = firstPage.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldNotBeNullOrEmpty();

        // A cursor issued for project A, replayed against project B, must only ever page B's rows.
        var bPage = await ListAsync(world, projectB, $"?limit=50&cursor={Uri.EscapeDataString(cursor!)}");
        bPage.Status.ShouldBe(HttpStatusCode.OK, bPage.ErrorMessage);
        var seen = Ids(bPage);
        seen.ShouldNotContain(a1);
        seen.ShouldNotContain(a2);
        seen.ShouldAllBe(id => id == b1);

        var malformed = await ListAsync(world, world.Project, "?cursor=not-a-cursor");
        malformed.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(malformed).ShouldBe(("cursor", "malformed"));
    }

    // ------------------------------------------------------------------ RLS with no tenant context

    [Fact]
    public async Task Rls_NoOrgSet_ReadsZeroRows()
    {
        await using var world = await StartAsync();
        (await CreateAsync(world, Body(world))).Status.ShouldBe(HttpStatusCode.Created);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database));
        (await scope.ScalarAsync<int>("select count(*)::int from public.waiver")).ShouldBe(0);
    }
}
