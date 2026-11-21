using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class QuarantineQaTests
{
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
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.quarantine_entry where org_id = '{world.Org}'")).ShouldBe(1);
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
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
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
