using System.Net;
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
public sealed partial class QuarantineTests
{
    private readonly PostgresTenancyFixture _fixture;

    public QuarantineTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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
        var rivalMaintainer = await AddMemberAsync(world, otherOrg, "test_maintainer");
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
