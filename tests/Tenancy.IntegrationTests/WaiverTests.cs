using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Evidgate.Modules.Policy.Application.Waivers;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 07.3.1-T1 through the real host and a real PostgreSQL: the <c>waiver</c> table's states,
/// the approver ≠ requester rule at the API and the database (AC1–AC3), the column grants
/// and the guard trigger, RBAC, cross-tenant isolation, idempotency, validation and the
/// keyset list. The server code is written from <c>local://waiver-contract.md</c>; these
/// tests pin the HTTP/DB contract, never internals.
/// </summary>
[Trait("Category", "Docker")]
public sealed class WaiverTests
{
    private readonly PostgresTenancyFixture _fixture;

    public WaiverTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, DedicatedDatabase Owned, Guid User, Guid Org, Guid Project) : IAsyncDisposable
    {
        public string Database => Owned.Name;

        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                await Api.DisposeAsync();
            }
            finally
            {
                // The database is this world's to drop even when the host's own disposal throws.
                await Owned.DisposeAsync();
            }
        }
    }

    private Task<World> StartAsync() =>
        _fixture.StartDedicatedAsync(async owned =>
        {
            var database = owned.Name;
            var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
            var client = api.CreateClient();
            var user = Guid.CreateVersion7();
            var org = await CreateOrgAsync(client, user, "acme");
            var project = await CreateProjectAsync(client, user, org, "checkout");

            return new World(api, client, owned, user, org, project);
        });

    // ------------------------------------------------------------------ helpers

    /// <summary>The caller headers of a member of <paramref name="org"/>.</summary>
    private static Action<HttpRequestHeaders> As(Guid user, Guid org) => TenancyApiFactory.Resolved(user: user, orgHeader: org);

    /// <summary>A well-formed create body; every call gets a fresh <c>target_id</c> unless one is named.</summary>
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

    /// <summary>
    /// An active member of <paramref name="world"/>'s org holding the built-in <paramref name="roleCode"/>.
    /// An invite only becomes active through 02.3.x's login (not merged), so the membership row is seeded
    /// as the owner — the seam <c>IdentityTestData.SeedActiveMemberAsync</c> and <c>IsolationWorld</c> use —
    /// while the role itself is granted through the real membership API.
    /// </summary>
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

    /// <summary>Inserts a <c>requested</c> waiver whose <c>expires_at</c> is already past, as the owner.</summary>
    private async Task<Guid> InsertExpiredWaiverAsync(World world)
    {
        var id = Guid.CreateVersion7();

        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            $"""
            insert into public.waiver (id, org_id, project_id, scope, target_id, reason, requested_by, expires_at)
            values ('{id}', '{world.Org}', '{world.Project}', 'case', '{Guid.CreateVersion7()}', 'expired', '{world.User}', now() - interval '1 hour')
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

    // ------------------------------------------------------------------ AC1: self-approval

    [Fact]
    public async Task SelfApproval_Is403WaiverSelfApproval_AndLeavesTheRowRequested()
    {
        await using var world = await StartAsync();
        var created = await CreateAsync(world, Body(world));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var id = created.Id();
        created.Text("state").ShouldBe("requested");
        created.Body.GetProperty("approved_by").ValueKind.ShouldBe(JsonValueKind.Null);

        // The org's creator holds release_approver, so it may call the route — but not approve its own waiver.
        var self = await ApproveAsync(world, id, world.Admin);

        self.Status.ShouldBe(HttpStatusCode.Forbidden);
        self.ErrorCode.ShouldBe("waiver_self_approval");
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.waiver where id = '{id}'")).ShouldBe("requested");
        (await _fixture.ScalarAsync<Guid?>(world.Database, $"select approved_by from public.waiver where id = '{id}'")).ShouldBeNull();
        (await AuditCountAsync(world, "waiver.approve")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ AC2: a different approver

    [Fact]
    public async Task ADifferentApprover_Approves_AndWritesOneAuditRow()
    {
        await using var world = await StartAsync();
        var created = await CreateAsync(world, Body(world));
        var id = created.Id();
        var approver = await AddMemberAsync(world, "release_approver");

        var approved = await ApproveAsync(world, id, As(approver, world.Org));

        approved.Status.ShouldBe(HttpStatusCode.OK, approved.ErrorMessage);
        approved.Text("state").ShouldBe("approved");
        approved.Id("approved_by").ShouldBe(approver);
        approved.Id("requested_by").ShouldBe(world.User);
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.waiver where id = '{id}'")).ShouldBe("approved");
        (await _fixture.ScalarAsync<Guid?>(world.Database, $"select approved_by from public.waiver where id = '{id}'")).ShouldBe(approver);
        (await AuditCountAsync(world, "waiver.approve")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ AC3: the database check

    [Fact]
    public async Task RawInsert_WithApprovedByEqualToRequestedBy_FailsTheCheckConstraint()
    {
        await using var world = await StartAsync();
        var user = world.User;

        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        var error = await owner.CaptureErrorAsync(
            $"""
            insert into public.waiver (id, org_id, project_id, scope, target_id, reason, requested_by, approved_by, state, expires_at)
            values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', 'case', '{Guid.CreateVersion7()}', 'self approval', '{user}', '{user}', 'approved', now() + interval '1 day')
            """);

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("waiver_self_approval_check");
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.waiver")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ grants and the guard trigger

    [Fact]
    public async Task AppRole_CannotInsertAnApprovedRow_ChangeIdentity_ExpireOrDelete()
    {
        await using var world = await StartAsync();
        var created = await CreateAsync(world, Body(world));
        var id = created.Id();
        var approver = await AddMemberAsync(world, "release_approver");
        (await ApproveAsync(world, id, As(approver, world.Org))).Status.ShouldBe(HttpStatusCode.OK);

        // The app role's INSERT grant names no `state` column, so it can never write an approved row.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.CaptureErrorAsync(
                $"""
                insert into public.waiver (id, org_id, project_id, scope, target_id, reason, requested_by, expires_at, state)
                values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', 'case', '{Guid.CreateVersion7()}', 'reason', '{world.User}', now() + interval '1 day', 'requested')
                """)).SqlState.ShouldBe("42501");
        }

        // approved_by may change only on requested -> approved: the guard refuses it on an approved row.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            var error = await scope.CaptureErrorAsync($"update public.waiver set approved_by = '{Guid.CreateVersion7()}' where id = '{id}'");
            error.SqlState.ShouldBe("23514");
            error.MessageText.ShouldContain("waiver");
        }

        // `expired` is written only by the 07.3.2-T2 sweeper, never by the app role.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            var error = await scope.CaptureErrorAsync($"update public.waiver set state = 'expired' where id = '{id}'");
            error.SqlState.ShouldBe("23514");
            error.MessageText.ShouldContain("waiver");
        }

        // An identity column is outside the app role's UPDATE grant.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.CaptureErrorAsync($"update public.waiver set project_id = '{Guid.CreateVersion7()}' where id = '{id}'"))
                .SqlState.ShouldBe("42501");
        }

        // The app role holds no DELETE at all.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.CaptureErrorAsync($"delete from public.waiver where id = '{id}'")).SqlState.ShouldBe("42501");
        }

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.waiver")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ RBAC

    [Fact]
    public async Task QaEngineer_CannotCreate_ViewerReads_ButCannotApprove()
    {
        await using var world = await StartAsync();
        var created = await CreateAsync(world, Body(world));
        var id = created.Id();
        var qaEngineer = await AddMemberAsync(world, "qa_engineer");
        var viewer = await AddMemberAsync(world, "viewer");

        var denied = await CreateAsync(world, Body(world), As(qaEngineer, world.Org));
        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        denied.ErrorCode.ShouldBe("forbidden");

        var listed = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/waivers", As(viewer, world.Org));
        listed.Status.ShouldBe(HttpStatusCode.OK, listed.ErrorMessage);
        listed.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ShouldContain(id);

        var approve = await ApproveAsync(world, id, As(viewer, world.Org));
        approve.Status.ShouldBe(HttpStatusCode.Forbidden);
        approve.ErrorCode.ShouldBe("forbidden");

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.waiver")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ the state machine

    [Fact]
    public async Task Approve_AfterApproval_Is409()
    {
        await using var world = await StartAsync();
        var id = (await CreateAsync(world, Body(world))).Id();
        var approver = await AddMemberAsync(world, "release_approver");

        (await ApproveAsync(world, id, As(approver, world.Org))).Status.ShouldBe(HttpStatusCode.OK);

        var second = await ApproveAsync(world, id, As(approver, world.Org));
        second.Status.ShouldBe(HttpStatusCode.Conflict);
        second.ErrorCode.ShouldBe("waiver_state_conflict");
    }

    [Fact]
    public async Task Revoke_FromRequestedAndApproved_Is200_AndFromRevoked_Is409()
    {
        await using var world = await StartAsync();
        var approver = await AddMemberAsync(world, "release_approver");

        var requested = (await CreateAsync(world, Body(world))).Id();
        var revokedRequested = await RevokeAsync(world, requested, world.Admin);
        revokedRequested.Status.ShouldBe(HttpStatusCode.OK, revokedRequested.ErrorMessage);
        revokedRequested.Text("state").ShouldBe("revoked");

        var approved = (await CreateAsync(world, Body(world))).Id();
        (await ApproveAsync(world, approved, As(approver, world.Org))).Status.ShouldBe(HttpStatusCode.OK);
        var revokedApproved = await RevokeAsync(world, approved, As(approver, world.Org));
        revokedApproved.Status.ShouldBe(HttpStatusCode.OK, revokedApproved.ErrorMessage);
        revokedApproved.Text("state").ShouldBe("revoked");

        var again = await RevokeAsync(world, approved, As(approver, world.Org));
        again.Status.ShouldBe(HttpStatusCode.Conflict);
        again.ErrorCode.ShouldBe("waiver_state_conflict");
    }

    [Fact]
    public async Task Approve_FromRevoked_Is409()
    {
        await using var world = await StartAsync();
        var approver = await AddMemberAsync(world, "release_approver");
        var id = (await CreateAsync(world, Body(world))).Id();
        (await RevokeAsync(world, id, world.Admin)).Status.ShouldBe(HttpStatusCode.OK);

        var approve = await ApproveAsync(world, id, As(approver, world.Org));

        approve.Status.ShouldBe(HttpStatusCode.Conflict);
        approve.ErrorCode.ShouldBe("waiver_state_conflict");
    }

    [Fact]
    public async Task ApproveOrRevoke_OfAnExpiredWaiver_Is409()
    {
        await using var world = await StartAsync();
        var approver = await AddMemberAsync(world, "release_approver");
        var id = await InsertExpiredWaiverAsync(world);

        var approve = await ApproveAsync(world, id, As(approver, world.Org));
        approve.Status.ShouldBe(HttpStatusCode.Conflict);
        approve.ErrorCode.ShouldBe("waiver_state_conflict");

        var revoke = await RevokeAsync(world, id, As(approver, world.Org));
        revoke.Status.ShouldBe(HttpStatusCode.Conflict);
        revoke.ErrorCode.ShouldBe("waiver_state_conflict");

        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.waiver where id = '{id}'")).ShouldBe("requested");
    }

    [Fact]
    public async Task ConcurrentDoubleApprove_LeavesExactlyOneApproval()
    {
        await using var world = await StartAsync();
        var id = (await CreateAsync(world, Body(world))).Id();
        var first = await AddMemberAsync(world, "release_approver");
        var second = await AddMemberAsync(world, "release_approver");

        var answers = await Task.WhenAll(
            ApproveAsync(world, id, As(first, world.Org)),
            ApproveAsync(world, id, As(second, world.Org)));

        answers.Count(answer => answer.Status == HttpStatusCode.OK).ShouldBe(1);
        answers.Count(answer => answer.Status == HttpStatusCode.Conflict).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.waiver where id = '{id}'")).ShouldBe("approved");
        (await AuditCountAsync(world, "waiver.approve")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ isolation

    [Fact]
    public async Task AnotherOrg_CannotApproveOrList_AndSeesNotFound()
    {
        await using var world = await StartAsync();
        var id = (await CreateAsync(world, Body(world))).Id();

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");
        var rival = As(otherUser, otherOrg);

        var cross = await ApproveAsync(world, id, rival);
        var random = await ApproveAsync(world, Guid.CreateVersion7(), rival);

        cross.Status.ShouldBe(HttpStatusCode.NotFound);
        cross.ErrorCode.ShouldBe("not_found");
        random.Status.ShouldBe(HttpStatusCode.NotFound);
        random.ErrorCode.ShouldBe("not_found");

        // Org A cannot list org B's project, and the org B project is the same 404 as a missing one.
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{otherProject}/waivers", world.Admin))
            .Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{Guid.CreateVersion7()}/waivers", world.Admin))
            .Status.ShouldBe(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ idempotency

    [Fact]
    public async Task SameKeySameBody_Replays_AndDifferentBodyIs409()
    {
        await using var world = await StartAsync();
        var key = NewKey();
        var body = Body(world);

        var first = await CreateAsync(world, body, key: key);
        var replay = await CreateAsync(world, body, key: key);
        var conflict = await CreateAsync(world, Body(world, reason: "a different reason"), key: key);

        first.Status.ShouldBe(HttpStatusCode.Created);
        replay.Status.ShouldBe(HttpStatusCode.Created);
        replay.Id().ShouldBe(first.Id());
        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.waiver")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ validation

    [Fact]
    public async Task Validation_RefusesBadExpiryScopeReasonAndUnknownProject()
    {
        await using var world = await StartAsync();

        var past = await CreateAsync(world, Body(world, expiresAt: DateTimeOffset.UtcNow.AddDays(-1)));
        past.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(past).ShouldBe(("expires_at", "not_in_future"));

        var scope = await CreateAsync(world, Body(world, scope: "run"));
        scope.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(scope).ShouldBe(("scope", "unknown"));

        var empty = await CreateAsync(world, Body(world, reason: string.Empty));
        empty.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(empty).ShouldBe(("reason", "empty"));

        var tooLong = await CreateAsync(world, Body(world, reason: new string('x', 2001)));
        tooLong.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(tooLong).ShouldBe(("reason", "too_long"));

        var missingTarget = await CreateAsync(world, new
        {
            project_id = world.Project,
            scope = "case",
            reason = "no target",
            expires_at = DateTimeOffset.UtcNow.AddDays(7),
        });
        missingTarget.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(missingTarget).ShouldBe(("target_id", "required"));

        var unknownProject = await CreateAsync(world, Body(world, project: Guid.CreateVersion7()));
        unknownProject.Status.ShouldBe(HttpStatusCode.NotFound);
        unknownProject.ErrorCode.ShouldBe("not_found");

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.waiver")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ the list

    [Fact]
    public async Task List_WalksEveryWaiverWithTheCursor_AndRefusesOutOfRangeLimits()
    {
        await using var world = await StartAsync();
        var created = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            created.Add((await CreateAsync(world, Body(world, reason: $"case {i}"))).Id());
        }

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"/v1/projects/{world.Project}/waivers?limit=1" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await world.Client.SendAsync(HttpMethod.Get, url, world.Admin);
            page.Status.ShouldBe(HttpStatusCode.OK, page.ErrorMessage);
            seen.AddRange(page.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
            cursor = page.Body.GetProperty("next_cursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        pages.ShouldBe(3);
        seen.ShouldBe(created);

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/waivers?limit=0", world.Admin))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/waivers?limit=201", world.Admin))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    // ------------------------------------------------------------------ P4: the list index

    [Fact]
    public async Task ListQuery_UsesTheProjectCreatedIndex()
    {
        await using var world = await StartAsync();
        for (var i = 0; i < 3; i++)
        {
            (await CreateAsync(world, Body(world, reason: $"case {i}"))).Status.ShouldBe(HttpStatusCode.Created);
        }

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        await scope.SetAsync("enable_seqscan", "off");

        // The list's first page: one project, keyset (created_at, id) ascending.
        var plan = await scope.ScalarAsync<string>(
            $"""
            explain (format json)
            select id from public.waiver
             where org_id = '{world.Org}' and project_id = '{world.Project}'
             order by created_at, id
             limit 51
            """);

        plan.ShouldContain("waiver_project_created_idx");
    }

    /// <summary>
    /// 00 §5.7 P4: the keyset cursor walks <c>waiver_project_created_idx</c> and the expiry sweeper's
    /// per-project due rows walk <c>waiver_state_expiry_idx</c>. The target project is seeded sparsely
    /// against a dense rest of the table and <c>ANALYZE</c>d, so the planner prefers each index on its
    /// own; sequential scans are disabled so a small table cannot make it prefer one.
    /// </summary>
    [Fact]
    public async Task CursorAndDueQueries_UseTheirIndexes()
    {
        await using var world = await StartAsync();
        var otherProject = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.project (id, org_id, name, slug)
            values ('{otherProject}', '{world.Org}', 'other', 'other');
            insert into public.waiver (id, org_id, project_id, scope, target_id, reason, requested_by, approved_by, state, expires_at, created_at)
            select gen_random_uuid(), '{world.Org}', '{world.Project}', 'case', gen_random_uuid(), 'due', '{world.User}',
                   case when g <= 100 then '00000000-0000-0000-0000-000000000001'::uuid end,
                   case when g <= 100 then 'approved' else 'requested' end,
                   now() - interval '1 hour',
                   now() - g * interval '1 minute'
              from generate_series(1, 2000) g;
            insert into public.waiver (id, org_id, project_id, scope, target_id, reason, requested_by, expires_at, created_at)
            select gen_random_uuid(), '{world.Org}', '{otherProject}', 'case', gen_random_uuid(), 'other', '{world.User}',
                   now() + interval '7 days',
                   now() - g * interval '1 minute'
              from generate_series(1, 20000) g;
            analyze public.waiver;
            """);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        await scope.SetAsync("enable_seqscan", "off");

        // The next page: the production statement with the (created_at, id) row-comparison cursor.
        var cursor = await scope.ScalarAsync<string>(
            $"explain (format json) {WaiverSql.NextPage}",
            ("org_id", world.Org),
            ("project_id", world.Project),
            ("cursor_created_at", DateTimeOffset.UtcNow.AddDays(-1)),
            ("cursor_id", Guid.CreateVersion7()),
            ("limit", 51));
        cursor.ShouldContain("waiver_project_created_idx");

        // The expiry sweeper's per-project due rows (07.3.2-T2): state and expires_at inside one project.
        var due = await scope.ScalarAsync<string>(
            """
            explain (format json)
            select id from public.waiver
             where org_id = @org_id and project_id = @project_id
               and state = 'approved' and expires_at <= now()
            """,
            ("org_id", world.Org),
            ("project_id", world.Project));
        due.ShouldContain("waiver_state_expiry_idx");
    }
}
