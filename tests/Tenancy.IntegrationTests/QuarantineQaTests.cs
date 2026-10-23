using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Independent QA of 07.3.1-T2 (quarantine table, states, endpoints, the T23 refusal), tier T3.
/// Negative, boundary and exploratory checks the author's <see cref="QuarantineTests"/> does not pin:
/// expiry/reason boundaries, mass-assignment, the service-token ceiling, the app-role column grants
/// and guard, the owner-run expiry, audit-row accounting, keyset stability with equal
/// <c>created_at</c>, cross-project cursor safety, the retired-revision case and the no-tenant RLS
/// read. Written against the real host and a real PostgreSQL; it pins the HTTP/DB contract, never
/// internals.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class QuarantineQaTests
{
    private readonly PostgresTenancyFixture _fixture;

    public QuarantineQaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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

    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var (cases, maintainer, membership) = await SeedCasesAndMaintainerAsync(database, client, user, org, project);

        return new World(api, client, database, user, org, project, cases, maintainer, membership);
    }

    // ------------------------------------------------------------------ helpers

    private static Action<HttpRequestHeaders> As(Guid user, Guid org) => TenancyApiFactory.Resolved(user: user, orgHeader: org);

    private async Task<(IReadOnlyList<Guid> Cases, Guid User, Guid Membership)> SeedCasesAndMaintainerAsync(
        string database, HttpClient client, Guid admin, Guid org, Guid project)
    {
        var cases = new List<Guid>();
        var maintainer = Guid.CreateVersion7();
        var membership = Guid.CreateVersion7();

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
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
    private async Task<Guid> AddMemberAsync(World world, string roleCode)
    {
        var userId = Guid.CreateVersion7();
        var membershipId = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{membershipId}', '{world.Org}', '{userId}', 'member-{userId:N}@example.test', 'active')
            """);

        var role = await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.role where code = '{roleCode}' and org_id is null");
        var granted = await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/orgs/{world.Org}/memberships/{membershipId}/roles", world.Admin, new { role_id = role }, NewKey());
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

    /// <summary>Inserts an entry in a chosen state as the owner (bypassing the API), for the grant/guard checks.</summary>
    private async Task<Guid> InsertEntryAsync(World world, string state, Guid? caseId = null, DateTimeOffset? createdAt = null)
    {
        var id = Guid.CreateVersion7();
        var created = createdAt is { } c ? $"'{c.UtcDateTime:O}'" : "now()";

        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            $"""
            insert into public.quarantine_entry (id, org_id, project_id, case_id, owner, reason, state, expires_at, created_at)
            values ('{id}', '{world.Org}', '{world.Project}', '{caseId ?? world.Case}', '{world.MaintainerMembership}', 'qa seed', '{state}', now() + interval '7 days', {created})
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

    // ------------------------------------------------------------------ boundaries: expiry and reason

    [Fact]
    public async Task Validation_Boundaries_ExpiryAndReason()
    {
        await using var world = await StartAsync();

        // expires_at exactly now (and any earlier instant) is not strictly in the future.
        var exactlyNow = await CreateAsync(world, Body(world, expiresAt: DateTimeOffset.UtcNow));
        exactlyNow.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(exactlyNow).ShouldBe(("expires_at", "not_in_future"));

        // A far-future expiry is accepted and stored.
        var far = await CreateAsync(world, Body(world, expiresAt: DateTimeOffset.UtcNow.AddYears(100)));
        far.Status.ShouldBe(HttpStatusCode.Created, far.ErrorMessage);

        // reason is capped at exactly 2000 characters (quarantine_entry_reason_check).
        var atMax = await CreateAsync(world, Body(world, caseId: world.Cases[1], reason: new string('x', 2000)));
        atMax.Status.ShouldBe(HttpStatusCode.Created, atMax.ErrorMessage);

        var overMax = await CreateAsync(world, Body(world, caseId: world.Cases[2], reason: new string('x', 2001)));
        overMax.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(overMax).ShouldBe(("reason", "too_long"));

        // The contract is 1..2000 characters with no trim rule, so a whitespace-only reason is
        // contract-valid; recorded here so a future trim rule is a deliberate change. The 2001-char
        // request wrote nothing, so case 2 is still free.
        var blank = await CreateAsync(world, Body(world, caseId: world.Cases[2], reason: "   "));
        blank.Status.ShouldBe(HttpStatusCode.Created, blank.ErrorMessage);

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.quarantine_entry")).ShouldBe(3);
    }

    [Fact]
    public async Task Reason_WithNul_Is422InvalidValue()
    {
        await using var world = await StartAsync();

        var nul = await CreateAsync(world, Body(world, reason: "a\u0000b"));

        nul.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(nul).ShouldBe(("reason", "invalid_value"));
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.quarantine_entry")).ShouldBe(0);
    }

    [Fact]
    public async Task UnknownJsonFields_AreIgnored_AndCannotSmuggleState()
    {
        await using var world = await StartAsync();

        var answer = await CreateAsync(world, new
        {
            project_id = world.Project,
            case_id = world.Case,
            owner = world.MaintainerMembership,
            reason = "qa",
            expires_at = DateTimeOffset.UtcNow.AddDays(1),
            state = "removed",
            id = Guid.CreateVersion7(),
            unexpected = "ignored",
        });

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        answer.Text("state").ShouldBe("active");
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.quarantine_entry where id = '{answer.Id()}'")).ShouldBe("active");
    }

    // ------------------------------------------------------------------ the service-token ceiling

    [Fact]
    public async Task ServiceToken_CannotHoldQuarantineWrite()
    {
        await using var world = await StartAsync();

        // `quarantine:write` is a member permission, outside the service-role ceiling (ci_producer/agent):
        // a service token claiming it is malformed and holds nothing, so the write is refused.
        var service = TenancyApiFactory.Resolved(
            serviceOrg: world.Org,
            serviceProject: world.Project,
            servicePermissions: ["quarantine:write"]);

        var create = await CreateAsync(world, Body(world), service);
        create.Status.ShouldBe(HttpStatusCode.Forbidden);

        var id = (await CreateAsync(world, Body(world))).Id();
        var remove = await RemoveAsync(world, id, service);
        remove.Status.ShouldBe(HttpStatusCode.Forbidden);

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.quarantine_entry")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ the app role's grants and the guard

    [Fact]
    public async Task AppRole_CannotInsertAState_UpdateContent_OrDelete()
    {
        await using var world = await StartAsync();
        var id = (await CreateAsync(world, Body(world))).Id();

        // The app role's INSERT grant names no `state` column, so it can never write a removed/expired row.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.CaptureErrorAsync(
                $"""
                insert into public.quarantine_entry (id, org_id, project_id, case_id, owner, reason, expires_at, state)
                values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', '{world.Cases[1]}', '{world.MaintainerMembership}', 'reason', now() + interval '1 day', 'removed')
                """)).SqlState.ShouldBe("42501");
        }

        // Every content column is outside the app role's UPDATE grant.
        foreach (var assignment in new[]
        {
            "reason = 'changed'",
            "expires_at = now() + interval '1 day'",
            $"owner = '{Guid.CreateVersion7()}'",
            $"case_id = '{world.Cases[1]}'",
        })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
            (await scope.CaptureErrorAsync($"update public.quarantine_entry set {assignment} where id = '{id}'"))
                .SqlState.ShouldBe("42501");
        }

        // The app role holds no DELETE at all.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.CaptureErrorAsync($"delete from public.quarantine_entry where id = '{id}'")).SqlState.ShouldBe("42501");
        }

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.quarantine_entry")).ShouldBe(1);
    }

    [Fact]
    public async Task AppRole_CannotMoveActiveToExpired_ButTheOwnerCan()
    {
        await using var world = await StartAsync();
        var id = (await CreateAsync(world, Body(world))).Id();

        // `expired` is written only by the 07.3.2-T2 sweeper, never by the app role.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            var error = await scope.CaptureErrorAsync($"update public.quarantine_entry set state = 'expired' where id = '{id}'");
            error.SqlState.ShouldBe("23514");
            error.MessageText.ShouldContain("quarantine");
        }

        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.quarantine_entry where id = '{id}'")).ShouldBe("active");

        // The owner (the migration role) may run the sweeper's transition.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await owner.ExecuteAsync($"update public.quarantine_entry set state = 'expired' where id = '{id}'")).ShouldBe(1);
            await owner.CommitAsync();
        }

        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.quarantine_entry where id = '{id}'")).ShouldBe("expired");
    }

    [Fact]
    public async Task AppRole_CannotMoveRemovedBackToActive()
    {
        await using var world = await StartAsync();
        var id = (await CreateAsync(world, Body(world))).Id();
        (await RemoveAsync(world, id)).Status.ShouldBe(HttpStatusCode.OK);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync($"update public.quarantine_entry set state = 'active' where id = '{id}'");

        error.SqlState.ShouldBe("23514");
        error.MessageText.ShouldContain("quarantine");
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.quarantine_entry where id = '{id}'")).ShouldBe("removed");
    }

    // ------------------------------------------------------------------ audit accounting

    [Fact]
    public async Task Audit_OneRowPerSuccessfulMutation_NoneOn403Or409Or404()
    {
        await using var world = await StartAsync();
        var viewer = await AddMemberAsync(world, "viewer");

        var id = (await CreateAsync(world, Body(world))).Id();
        (await AuditCountAsync(world, "quarantine.create")).ShouldBe(1);

        // 403: no write, no audit.
        (await CreateAsync(world, Body(world, caseId: world.Cases[1]), As(viewer, world.Org))).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await AuditCountAsync(world, "quarantine.create")).ShouldBe(1);

        // 409 duplicate case: still one audit row.
        (await CreateAsync(world, Body(world))).Status.ShouldBe(HttpStatusCode.Conflict);
        (await AuditCountAsync(world, "quarantine.create")).ShouldBe(1);

        (await RemoveAsync(world, id)).Status.ShouldBe(HttpStatusCode.OK);
        (await AuditCountAsync(world, "quarantine.remove")).ShouldBe(1);

        // 404 second remove: still one audit row.
        (await RemoveAsync(world, id)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await AuditCountAsync(world, "quarantine.remove")).ShouldBe(1);
    }

    [Fact]
    public async Task IdempotentReplay_AuditsOnce()
    {
        await using var world = await StartAsync();
        var key = NewKey();
        var body = Body(world);

        (await CreateAsync(world, body, key: key)).Status.ShouldBe(HttpStatusCode.Created);
        (await CreateAsync(world, body, key: key)).Status.ShouldBe(HttpStatusCode.Created);

        (await AuditCountAsync(world, "quarantine.create")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.quarantine_entry")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ the keyset list

    [Fact]
    public async Task List_IsStableAndOrderedByCreatedAtThenId_WithEqualCreatedAt()
    {
        await using var world = await StartAsync();
        var at = DateTimeOffset.UtcNow.AddMinutes(-5);
        for (var i = 0; i < 3; i++)
        {
            await InsertEntryAsync(world, "active", world.Cases[i], createdAt: at);
        }

        var first = await ListAsync(world, world.Project, "?limit=50");
        var second = await ListAsync(world, world.Project, "?limit=50");
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        Ids(first).ShouldBe(Ids(second));

        // The API order must match the database's own (created_at, id) order, so the id tiebreaker
        // is really in the statement and not left to the planner.
        var expected = await _fixture.ColumnAsync<Guid>(
            world.Database,
            $"select id from public.quarantine_entry where project_id = '{world.Project}' order by created_at, id");
        Ids(first).ShouldBe(expected);

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
        var a1 = (await CreateAsync(world, Body(world, caseId: world.Cases[0]))).Id();
        var a2 = (await CreateAsync(world, Body(world, caseId: world.Cases[1]))).Id();
        var projectB = await CreateProjectAsync(world.Client, world.User, world.Org, "other");
        var bCase = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit)
            values ('{bCase}', '{world.Org}', '{projectB}', '{Guid.CreateVersion7()}', 'b', 'playwright', 'abcdef0')
            """);
        var b1 = (await CreateAsync(world, Body(world, project: projectB, caseId: bCase))).Id();

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

    // ------------------------------------------------------------------ the critical list, edge cases

    [Fact]
    public async Task ARetiredRevision_IsNotTheActiveOne()
    {
        await using var world = await StartAsync();
        await InsertRevisionAsync(world, "retired", [world.Case.ToString("D")]);

        var created = await CreateAsync(world, Body(world));

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
    }

    [Fact]
    public async Task AnOwnerOfAnotherOrg_Is422Not500()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        var otherMembership = await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.membership where org_id = '{otherOrg}' limit 1");

        var answer = await CreateAsync(world, Body(world, owner: otherMembership));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(answer).ShouldBe(("owner", "unknown"));
    }

    // ------------------------------------------------------------------ RLS with no tenant context

    [Fact]
    public async Task Rls_NoOrgSet_ReadsZeroRows()
    {
        await using var world = await StartAsync();
        (await CreateAsync(world, Body(world))).Status.ShouldBe(HttpStatusCode.Created);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database));
        (await scope.ScalarAsync<int>("select count(*)::int from public.quarantine_entry")).ShouldBe(0);
    }
}
