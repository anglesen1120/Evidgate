using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 07.3.1-T2 through the real host and a real PostgreSQL: the <c>quarantine_entry</c> lifecycle
/// (AC4–AC6), the T23 critical-case refusal, RBAC, cross-tenant isolation, idempotency, validation
/// and the keyset list. The server code is written from the 07.3.1 plan and the D1–D6 decisions;
/// these tests pin the HTTP/DB contract, never internals.
/// </summary>
[Trait("Category", "Docker")]
public sealed class QuarantineTests
{
    private readonly PostgresTenancyFixture _fixture;

    public QuarantineTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        IReadOnlyList<Guid> Cases,
        Guid Maintainer,
        Guid MaintainerMembership) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public Action<HttpRequestHeaders> AsMaintainer => TenancyApiFactory.Resolved(user: Maintainer, orgHeader: Org);

        public Guid Case => Cases[0];

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync(string? database = null)
    {
        // The plan test passes a dedicated database: it disables sequential scans and reads the planner's choice,
        // which a table other classes seed at the same time would change.
        database ??= await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));
        var (cases, maintainer, membership) = await SeedCasesAndMaintainerAsync(database, client, user, org, project);

        return new World(api, client, database, user, org, project, cases, maintainer, membership);
    }

    // ------------------------------------------------------------------ helpers

    private static Action<HttpRequestHeaders> As(Guid user, Guid org) => TenancyApiFactory.Resolved(user: user, orgHeader: org);

    /// <summary>Three catalog cases and an active <c>test_maintainer</c> membership (the only role holding <c>quarantine:write</c>, D1).</summary>
    private async Task<(IReadOnlyList<Guid> Cases, Guid User, Guid Membership)> SeedCasesAndMaintainerAsync(
        string database, HttpClient client, Guid admin, Guid org, Guid project)
    {
        var cases = new List<Guid>();
        var maintainer = Guid.CreateVersion7();
        var membership = Guid.CreateVersion7();

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            // 0044's `membership_user_fk` makes the membership's user_id name a real account.
            await scope.ExecuteAsync(
                """insert into public."user" (id, email) values (@id, @email)""",
                ("id", maintainer), ("email", $"maintainer-{maintainer:N}@example.test"));
            await scope.ExecuteAsync(
                "insert into public.membership (id, org_id, user_id, invited_email, status) values (@id, @org_id, @user_id, @email, 'active')",
                ("id", membership), ("org_id", org), ("user_id", maintainer), ("email", $"maintainer-{maintainer:N}@example.test"));
            for (var i = 0; i < 3; i++)
            {
                var caseId = Guid.CreateVersion7();
                cases.Add(caseId);
                await scope.ExecuteAsync(
                    """
                    insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit)
                    values (@id, @org_id, @project_id, @annotation_id, @name, 'playwright', 'abcdef0')
                    """,
                    ("id", caseId), ("org_id", org), ("project_id", project), ("annotation_id", Guid.CreateVersion7()), ("name", $"case {i}"));
            }

            await scope.CommitAsync();
        }

        var role = await _fixture.ScalarAsync<Guid>(database, "select id from public.role where code = 'test_maintainer' and org_id is null");
        var granted = await client.SendAsync(
            HttpMethod.Post, $"/v1/orgs/{org}/memberships/{membership}/roles", As(admin, org), new { role_id = role }, NewKey());
        granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);

        return (cases, maintainer, membership);
    }

    private static object Body(
        World world,
        string reason = "known failing case",
        DateTimeOffset? expiresAt = null,
        Guid? project = null,
        Guid? caseId = null,
        Guid? owner = null) => new
        {
            project_id = project ?? world.Project,
            case_id = caseId ?? world.Case,
            owner = owner ?? world.MaintainerMembership,
            reason,
            expires_at = expiresAt ?? DateTimeOffset.UtcNow.AddDays(7),
        };

    private static Task<ApiAnswer> CreateAsync(World world, object body, Action<HttpRequestHeaders>? caller = null, string? key = null) =>
        world.Client.SendAsync(HttpMethod.Post, "/v1/quarantine", caller ?? world.AsMaintainer, body, key ?? NewKey());

    private static Task<ApiAnswer> RemoveAsync(World world, Guid entry, Action<HttpRequestHeaders>? caller = null, string? key = null) =>
        world.Client.SendAsync(HttpMethod.Delete, $"/v1/quarantine/{entry}", caller ?? world.AsMaintainer, null, key ?? NewKey());

    private static Task<ApiAnswer> ListAsync(World world, Guid project, string query = "", Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/quarantine{query}", caller ?? world.AsMaintainer);

    /// <summary>An active member of the org holding the built-in role, seeded as the owner then granted through the API.</summary>
    private Task<Guid> AddMemberAsync(World world, string roleCode) => AddMemberAsync(world, world.Org, world.Admin, roleCode);

    /// <summary>An active member of <paramref name="org"/> holding the built-in role, granted by <paramref name="admin"/>.</summary>
    private async Task<Guid> AddMemberAsync(World world, Guid org, Action<HttpRequestHeaders> admin, string roleCode)
    {
        var userId = Guid.CreateVersion7();
        var membershipId = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public."user" (id, email) values ('{userId}', 'member-{userId:N}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{membershipId}', '{org}', '{userId}', 'member-{userId:N}@example.test', 'active')
            """);

        var role = await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.role where code = '{roleCode}' and org_id is null");
        var granted = await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/orgs/{org}/memberships/{membershipId}/roles", admin, new { role_id = role }, NewKey());
        granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);

        return userId;
    }

    /// <summary>Inserts a <c>policy_revision</c> in a chosen state as the owner: activation merged with 07.1.1-T4 and the API refuses to activate a revision whose critical case is under an active quarantine (Q2), so the state is seeded directly.</summary>
    private async Task<Guid> InsertRevisionAsync(World world, string state, IReadOnlyList<string> criticalCases, int revisionNo = 1)
    {
        var id = Guid.CreateVersion7();
        var body = JsonSerializer.Serialize(new { critical_cases = criticalCases });
        var hash = "sha256:" + new string((char)('a' + revisionNo), 64);

        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            $"""
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, state, created_by)
            values ('{id}', '{world.Org}', '{world.Project}', {revisionNo}, '{hash}', '{body}'::jsonb, '{state}', '{world.User}')
            """)).ShouldBe(1);
        await owner.CommitAsync();

        return id;
    }

    /// <summary>Inserts an <c>active</c> entry whose <c>expires_at</c> is already past, as the owner (the sweeper has not run).</summary>
    private async Task<Guid> InsertExpiredActiveAsync(World world, Guid caseId)
    {
        var id = Guid.CreateVersion7();

        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            $"""
            insert into public.quarantine_entry (id, org_id, project_id, case_id, owner, reason, state, expires_at)
            values ('{id}', '{world.Org}', '{world.Project}', '{caseId}', '{world.MaintainerMembership}', 'expired', 'active', now() - interval '1 hour')
            """)).ShouldBe(1);
        await owner.CommitAsync();

        return id;
    }

    private async Task<long> AuditCountAsync(World world, string action) =>
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.audit_log where action = '{action}' and org_id = '{world.Org:D}'");

    private static (string Path, string Reason) Detail(ApiAnswer answer, int index = 0)
    {
        var detail = answer.Body.GetProperty("error").GetProperty("details")[index];
        return (detail.GetProperty("path").GetString()!, detail.GetProperty("reason").GetString()!);
    }

    // ------------------------------------------------------------------ AC4: the critical-case refusal

    [Fact]
    public async Task CriticalCase_Is422CriticalCaseNotQuarantinable_AndWritesNoRow()
    {
        await using var world = await StartAsync();
        await InsertRevisionAsync(world, "active", [world.Case.ToString("D")]);

        var refused = await CreateAsync(world, Body(world));

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("critical_case_not_quarantinable");
        Detail(refused).ShouldBe(("case_id", "critical"));
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.quarantine_entry where org_id = '{world.Org:D}'")).ShouldBe(0);
        (await AuditCountAsync(world, "quarantine.create")).ShouldBe(0);
    }

    [Fact]
    public async Task CriticalMatch_IsCaseInsensitiveOnTheUuidText()
    {
        await using var world = await StartAsync();
        await InsertRevisionAsync(world, "active", [world.Case.ToString("D").ToUpperInvariant()]);

        var refused = await CreateAsync(world, Body(world));

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("critical_case_not_quarantinable");
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.quarantine_entry where org_id = '{world.Org:D}'")).ShouldBe(0);
    }

    [Fact]
    public async Task ANonUuidCriticalItem_NeverMatches()
    {
        await using var world = await StartAsync();
        await InsertRevisionAsync(world, "active", ["checkout/guest"]);

        var created = await CreateAsync(world, Body(world));

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
    }

    [Fact]
    public async Task NoActiveRevision_AllowsTheWrite_AndADraftRevisionIsNotActive()
    {
        await using var world = await StartAsync();

        // No revision at all: no critical list, the write is allowed.
        (await CreateAsync(world, Body(world))).Status.ShouldBe(HttpStatusCode.Created);

        // A draft revision carrying the case is not the active one, so the second case is still allowed.
        await InsertRevisionAsync(world, "draft", [world.Cases[1].ToString("D")]);
        var second = await CreateAsync(world, Body(world, caseId: world.Cases[1]));

        second.Status.ShouldBe(HttpStatusCode.Created, second.ErrorMessage);
    }

    // ------------------------------------------------------------------ AC5: the create

    [Fact]
    public async Task NonCriticalCase_CreatesAnActiveEntry()
    {
        await using var world = await StartAsync();

        var created = await CreateAsync(world, Body(world));

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        created.Text("state").ShouldBe("active");
        created.Id("case_id").ShouldBe(world.Case);
        created.Id("project_id").ShouldBe(world.Project);
        created.Id("owner").ShouldBe(world.MaintainerMembership);
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.quarantine_entry where id = '{created.Id()}'")).ShouldBe("active");
        (await AuditCountAsync(world, "quarantine.create")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ AC6: the remove

    [Fact]
    public async Task Remove_MovesActiveToRemoved_AndASecondRemoveIs404()
    {
        await using var world = await StartAsync();
        var id = (await CreateAsync(world, Body(world))).Id();

        var removed = await RemoveAsync(world, id);

        removed.Status.ShouldBe(HttpStatusCode.OK, removed.ErrorMessage);
        removed.Text("state").ShouldBe("removed");
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.quarantine_entry where id = '{id}'")).ShouldBe("removed");
        (await AuditCountAsync(world, "quarantine.remove")).ShouldBe(1);

        var again = await RemoveAsync(world, id);
        again.Status.ShouldBe(HttpStatusCode.NotFound);
        again.ErrorCode.ShouldBe("not_found");
        (await AuditCountAsync(world, "quarantine.remove")).ShouldBe(1);
    }

    [Fact]
    public async Task Remove_OfAnUnsweptExpiredActiveEntry_Is200()
    {
        await using var world = await StartAsync();
        var id = await InsertExpiredActiveAsync(world, world.Case);

        var removed = await RemoveAsync(world, id);

        removed.Status.ShouldBe(HttpStatusCode.OK, removed.ErrorMessage);
        removed.Text("state").ShouldBe("removed");
    }

    [Fact]
    public async Task Create_WhileAnUnsweptExpiredActiveEntryExists_Is409()
    {
        await using var world = await StartAsync();
        await InsertExpiredActiveAsync(world, world.Case);

        var conflict = await CreateAsync(world, Body(world));

        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("quarantine_conflict");
    }

    [Fact]
    public async Task ConcurrentCreate_ForOneCase_IsOne201AndOne409()
    {
        await using var world = await StartAsync();

        var answers = await Task.WhenAll(CreateAsync(world, Body(world)), CreateAsync(world, Body(world)));

        answers.Count(answer => answer.Status == HttpStatusCode.Created).ShouldBe(1);
        answers.Count(answer => answer.Status == HttpStatusCode.Conflict).ShouldBe(1);
        answers.Single(answer => answer.Status == HttpStatusCode.Conflict).ErrorCode.ShouldBe("quarantine_conflict");
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.quarantine_entry where org_id = '{world.Org:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentRemove_IsOne200AndOne404()
    {
        await using var world = await StartAsync();
        var id = (await CreateAsync(world, Body(world))).Id();

        var answers = await Task.WhenAll(RemoveAsync(world, id), RemoveAsync(world, id));

        answers.Count(answer => answer.Status == HttpStatusCode.OK).ShouldBe(1);
        answers.Count(answer => answer.Status == HttpStatusCode.NotFound).ShouldBe(1);
        (await AuditCountAsync(world, "quarantine.remove")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ RBAC

    [Fact]
    public async Task PermissionMatrix_OnlyTestMaintainerWrites_EveryMemberRoleReads()
    {
        await using var world = await StartAsync();
        var releaseApprover = await AddMemberAsync(world, "release_approver");
        var qaEngineer = await AddMemberAsync(world, "qa_engineer");
        var viewer = await AddMemberAsync(world, "viewer");

        // test_maintainer (the seeded one) may create and remove.
        var created = await CreateAsync(world, Body(world));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        (await RemoveAsync(world, created.Id())).Status.ShouldBe(HttpStatusCode.OK);

        // Every other member role is refused on both writes (quarantine:write is test_maintainer only, D1).
        foreach (var (label, user) in new[] { ("release_approver", releaseApprover), ("qa_engineer", qaEngineer), ("viewer", viewer) })
        {
            var deniedCreate = await CreateAsync(world, Body(world, caseId: world.Cases[1]), As(user, world.Org));
            deniedCreate.Status.ShouldBe(HttpStatusCode.Forbidden, $"{label} must not create");
            deniedCreate.ErrorCode.ShouldBe("forbidden");

            var deniedRemove = await RemoveAsync(world, Guid.CreateVersion7(), As(user, world.Org));
            deniedRemove.Status.ShouldBe(HttpStatusCode.Forbidden, $"{label} must not remove");
        }

        // The maintainer does not hold policy:write, so it cannot create a waiver.
        var waiver = await world.Client.SendAsync(
            HttpMethod.Post,
            "/v1/waivers",
            world.AsMaintainer,
            new
            {
                project_id = world.Project,
                scope = "case",
                target_id = world.Case,
                reason = "not allowed",
                expires_at = DateTimeOffset.UtcNow.AddDays(7),
            },
            NewKey());
        waiver.Status.ShouldBe(HttpStatusCode.Forbidden);
        waiver.ErrorCode.ShouldBe("forbidden");

        // Reads stay policy:read: viewer and release_approver both list.
        (await ListAsync(world, world.Project, caller: As(viewer, world.Org))).Status.ShouldBe(HttpStatusCode.OK);
        (await ListAsync(world, world.Project, caller: As(releaseApprover, world.Org))).Status.ShouldBe(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------ isolation

    [Fact]
    public async Task AnotherOrg_CannotListOrRemove_AndSeesNotFound()
    {
        await using var world = await StartAsync();
        var id = (await CreateAsync(world, Body(world))).Id();

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, Unique.Slug("theirs"));

        // The rival must hold quarantine:write or the route answers 403 before the tenant check, and the isolation
        // claim is the 404 that follows the permission gate. The org founder holds tenant_admin + release_approver
        // (OrganizationFounder), neither of which carries quarantine:write (test_maintainer only, D1), so the rival
        // is a test_maintainer of org B.
        var rivalMaintainer = await AddMemberAsync(world, otherOrg, As(otherUser, otherOrg), "test_maintainer");
        var rival = As(rivalMaintainer, otherOrg);

        var cross = await RemoveAsync(world, id, rival);
        var random = await RemoveAsync(world, Guid.CreateVersion7(), rival);
        cross.Status.ShouldBe(HttpStatusCode.NotFound);
        cross.ErrorCode.ShouldBe("not_found");
        random.Status.ShouldBe(HttpStatusCode.NotFound);
        random.ErrorCode.ShouldBe("not_found");

        (await ListAsync(world, otherProject, caller: world.Admin)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await ListAsync(world, Guid.CreateVersion7(), caller: world.Admin)).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnknownOrOtherProjectCase_Is404()
    {
        await using var world = await StartAsync();
        var otherProject = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("other"));

        var unknown = await CreateAsync(world, Body(world, caseId: Guid.CreateVersion7()));
        unknown.Status.ShouldBe(HttpStatusCode.NotFound);
        unknown.ErrorCode.ShouldBe("not_found");

        // A case of another project in the same org is not visible in this project.
        var otherCase = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit)
            values ('{otherCase}', '{world.Org}', '{otherProject}', '{Guid.CreateVersion7()}', 'other', 'playwright', 'abcdef0')
            """);
        var crossProject = await CreateAsync(world, Body(world, caseId: otherCase));
        crossProject.Status.ShouldBe(HttpStatusCode.NotFound);
        crossProject.ErrorCode.ShouldBe("not_found");

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.quarantine_entry where org_id = '{world.Org:D}'")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ validation

    [Fact]
    public async Task Validation_RefusesBadExpiryReasonOwnerAndUnknownProject()
    {
        await using var world = await StartAsync();

        var past = await CreateAsync(world, Body(world, expiresAt: DateTimeOffset.UtcNow.AddDays(-1)));
        past.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(past).ShouldBe(("expires_at", "not_in_future"));

        var empty = await CreateAsync(world, Body(world, reason: string.Empty));
        empty.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(empty).ShouldBe(("reason", "empty"));

        var tooLong = await CreateAsync(world, Body(world, reason: new string('x', 2001)));
        tooLong.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(tooLong).ShouldBe(("reason", "too_long"));

        var unknownOwner = await CreateAsync(world, Body(world, owner: Guid.CreateVersion7()));
        unknownOwner.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(unknownOwner).ShouldBe(("owner", "unknown"));

        var unknownProject = await CreateAsync(world, Body(world, project: Guid.CreateVersion7()));
        unknownProject.Status.ShouldBe(HttpStatusCode.NotFound);
        unknownProject.ErrorCode.ShouldBe("not_found");

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.quarantine_entry where org_id = '{world.Org:D}'")).ShouldBe(0);
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
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.quarantine_entry where org_id = '{world.Org:D}'")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ the list

    [Fact]
    public async Task List_WalksEveryEntryWithTheCursor_AndRefusesOutOfRangeLimits()
    {
        await using var world = await StartAsync();
        var created = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            created.Add((await CreateAsync(world, Body(world, caseId: world.Cases[i], reason: $"case {i}"))).Id());
        }

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var query = "?limit=1" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await ListAsync(world, world.Project, query);
            page.Status.ShouldBe(HttpStatusCode.OK, page.ErrorMessage);
            seen.AddRange(page.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
            cursor = page.Body.GetProperty("next_cursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        pages.ShouldBe(3);
        seen.ShouldBe(created);

        (await ListAsync(world, world.Project, "?limit=0")).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ListAsync(world, world.Project, "?limit=201")).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    // ------------------------------------------------------------------ P4: the list index

    [Fact]
    public async Task ListQuery_UsesTheProjectCreatedIndex()
    {
        // Dedicated: the plan is read off this database's own table, which no parallel class seeds.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(database);
        for (var i = 0; i < 3; i++)
        {
            (await CreateAsync(world, Body(world, caseId: world.Cases[i], reason: $"case {i}"))).Status.ShouldBe(HttpStatusCode.Created);
        }

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        await scope.SetAsync("enable_seqscan", "off");

        var plan = await scope.ScalarAsync<string>(
            $"""
            explain (format json)
            select id from public.quarantine_entry
             where org_id = '{world.Org}' and project_id = '{world.Project}'
             order by created_at, id
             limit 51
            """);

        plan.ShouldContain("quarantine_entry_project_created_idx");
    }
}
