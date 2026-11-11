using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Domain.Changes;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The queue and the two read routes of 13.1.2 over the real host: the pending write leaves every item untouched,
/// the selection read answers the batch ETag and the stale flags, and the refusals of the state table hold.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class AuthoringChangeTests(PostgresTenancyFixture fixture)
{
    [Fact]
    public async Task Queue_CommentDeleteMergeSplit_LeaveEveryAffectedItemUntouched()
    {
        await using var world = await StartAsync(5);

        var comment = await QueueAsync(world, world.Items[0], "comment", """{"body":"Needs a screenshot"}""");
        var deletion = await QueueAsync(world, world.Items[1], "delete", "{}");
        var merge = await QueueAsync(
            world, world.Items[3], "merge", MergeJson(world.Items[3], world.Items[2]));
        var split = await QueueAsync(world, world.Items[4], "split", SplitJson("Log in", "Log out"));

        foreach (var answer in new[] { comment, deletion, merge, split })
        {
            answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
            answer.Text("state").ShouldBe("pending");
            answer.Body.GetProperty("base_version").GetInt32().ShouldBe(1);
            answer.Body.GetProperty("created_at").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.MinValue);
            answer.ETag.ShouldBe("\"1\"", "the 201 carries the new row's own version");
        }

        // AC1: no item moved and no authoring_version row exists for any of them.
        (await fixture.ScalarAsync<int>(world.Database,
            $"select count(*)::int from public.authoring_version where org_id = '{world.Org}'")).ShouldBe(0);
        (await fixture.ScalarAsync<int>(world.Database,
            $"select count(*)::int from public.checklist_item where org_id = '{world.Org}' and version = 1")).ShouldBe(5);
        (await fixture.ScalarAsync<int>(world.Database,
            $"select count(*)::int from public.authoring_change where org_id = '{world.Org}' and state = 'pending'")).ShouldBe(4);
        (await AuditCountAsync(world, "authoring.change.queue")).ShouldBe(4);

        var detail = await world.Client.SendAsync(
            HttpMethod.Get, world.ChangePath(comment.Id("change_id")), world.Caller);
        detail.Status.ShouldBe(HttpStatusCode.OK, detail.ErrorMessage);
        detail.Text("state").ShouldBe("pending");
        detail.Body.GetProperty("payload").GetProperty("body").GetString().ShouldBe("Needs a screenshot");
        detail.ETag.ShouldNotBeNull();
    }

    [Fact]
    public async Task Queue_RefusesADeletedTargetItemOrPayloadItem()
    {
        await using var world = await StartAsync(3);
        await fixture.ExecuteAsync(world.Database,
            $"update public.checklist_item set deleted_at = now() where id = '{world.Items[2]}'");

        AssertInvalid(
            await QueueAsync(world, world.Items[2], "comment", """{"body":"note"}"""),
            "/target_id",
            "target_deleted");
        AssertInvalid(
            await QueueAsync(world, world.Items[0], "merge", MergeJson(world.Items[0], world.Items[2])),
            "/payload/source_item_ids/1",
            "target_deleted");
        var absent = await QueueAsync(world, Guid.CreateVersion7(), "delete", "{}");
        absent.Status.ShouldBe(HttpStatusCode.NotFound);
        absent.ErrorCode.ShouldBe("not_found");

        (await ChangeCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Queue_DeleteOfAnItemWithLiveCases_IsAuthoringNotEmpty()
    {
        await using var world = await StartAsync(2);
        await AuthoringLinkTestData.SeedManualCaseAsync(
            fixture, world.Database, new AuthoringLinkTree(world.Org, world.Project, world.Items[1]));

        var answer = await QueueAsync(world, world.Items[1], "delete", "{}");

        answer.Status.ShouldBe(HttpStatusCode.Conflict, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("authoring_not_empty");
        answer.Body.GetProperty("error").GetProperty("details")[0].GetProperty("reason").GetString()
            .ShouldBe("item_has_cases");
        (await ChangeCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Queue_OverAnOverlappingPendingStructuralChange_Is409()
    {
        await using var world = await StartAsync(3);
        (await QueueAsync(world, world.Items[2], "merge", MergeJson(world.Items[2], world.Items[1])))
            .Status.ShouldBe(HttpStatusCode.Created);

        // A merge covers both its survivor and its sources, so a delete of either target is refused.
        AssertAlreadyPending(
            await QueueAsync(world, world.Items[1], "delete", "{}"), "/target_id");
        AssertAlreadyPending(
            await QueueAsync(world, world.Items[2], "delete", "{}"), "/target_id");
        AssertAlreadyPending(
            await QueueAsync(world, world.Items[2], "split", SplitJson("A", "B")), "/target_id");
        AssertAlreadyPending(
            await QueueAsync(world, world.Items[0], "merge", MergeJson(world.Items[0], world.Items[1])),
            "/target_id");

        // The same guard is enforced by the trigger, so a writer that bypasses the handler is refused too.
        var violation = await Should.ThrowAsync<PostgresException>(() => fixture.ExecuteAsync(
            world.Database,
            $$"""
            insert into public.authoring_change
                (id, org_id, project_id, target_id, change_type, payload, base_version, affected_versions,
                 author_membership_id)
            values ('{{Guid.CreateVersion7()}}', '{{world.Org}}', '{{world.Project}}', '{{world.Items[1]}}', 'delete',
                '{}'::jsonb, 1, jsonb_build_object('{{world.Items[1]}}', 1), '{{world.Membership}}')
            """));
        violation.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        (await PendingCountAsync(world)).ShouldBe(1);
    }

    /// <summary>
    /// The apply selection is read from the raw array: a member that is not a uuid is refused at its own index rather
    /// than dropped (which would apply a selection the caller never sent), and the 200 bound is the array's own length,
    /// so an oversized selection with a malformed member is still refused as oversized.
    /// </summary>
    [Fact]
    public async Task Apply_RefusesANonUuidMemberAtIndexAndAnOversizedRawSelection()
    {
        await using var world = await StartAsync(1);

        var nonUuid = await ApplyAsync(world, new
        {
            change_ids = new object[] { Guid.CreateVersion7(), "not-a-uuid" },
        });
        AssertInvalid(nonUuid, "/change_ids/1", "payload_invalid");

        var tooMany = Enumerable.Range(0, AuthoringChangeLimits.SelectionMax + 1)
            .Select(_ => Guid.CreateVersion7())
            .ToArray();
        AssertInvalid(await ApplyAsync(world, new { change_ids = tooMany }), "/change_ids", "too_many_changes");

        var oversizedWithJunk = await ApplyAsync(world, new
        {
            change_ids = new object[] { "not-a-uuid" }.Concat(tooMany.Cast<object>()).ToArray(),
        });
        AssertInvalid(oversizedWithJunk, "/change_ids", "too_many_changes");
        (await ChangeCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Queue_AtThePendingCap_Is429WithRetryAfter()
    {
        await using var world = await StartAsync(2);
        await FillPendingAsync(world, 500);

        var answer = await QueueAsync(world, world.Items[0], "comment", """{"body":"one too many"}""");

        answer.Status.ShouldBe((HttpStatusCode)429, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("quota_exceeded");
        answer.Headers.RetryAfter.ShouldNotBeNull();
        answer.Body.GetProperty("error").GetProperty("details")[0].GetProperty("reason").GetString()
            .ShouldBe("reached");
        (await PendingCountAsync(world)).ShouldBe(500);
    }

    [Fact]
    public async Task Queue_At499Pending_TwoConcurrentRequests_OneWinsOne429()
    {
        await using var world = await StartAsync(2);
        await FillPendingAsync(world, 499);

        var answers = await Task.WhenAll(
            QueueAsync(world, world.Items[0], "comment", """{"body":"first"}"""),
            QueueAsync(world, world.Items[1], "comment", """{"body":"second"}"""));

        answers.Count(answer => answer.Status == HttpStatusCode.Created).ShouldBe(1);
        var refused = answers.Single(answer => answer.Status != HttpStatusCode.Created);
        refused.Status.ShouldBe((HttpStatusCode)429, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("quota_exceeded");
        (await PendingCountAsync(world)).ShouldBe(500);
    }

    [Fact]
    public async Task Queue_ReplaysTheFirstAnswerAndRefusesKeyReuse()
    {
        await using var world = await StartAsync(2);
        const string key = "queue-replay-key";

        var first = await QueueAsync(world, world.Items[0], "comment", """{"body":"one"}""", key);
        var replay = await QueueAsync(world, world.Items[0], "comment", """{"body":"one"}""", key);
        var reused = await QueueAsync(world, world.Items[0], "comment", """{"body":"two"}""", key);

        first.Status.ShouldBe(HttpStatusCode.Created);
        replay.Status.ShouldBe(HttpStatusCode.Created);
        JsonElement.DeepEquals(first.Body, replay.Body).ShouldBeTrue();
        reused.Status.ShouldBe(HttpStatusCode.Conflict);
        reused.ErrorCode.ShouldBe("idempotency_conflict");
        (await ChangeCountAsync(world)).ShouldBe(1);
        (await AuditCountAsync(world, "authoring.change.queue")).ShouldBe(1);
    }

    [Fact]
    public async Task Queue_EnforcesTheMergeAndSplitBounds()
    {
        await using var world = await StartAsync(2);
        var sources = string.Join(
            ',', Enumerable.Range(0, 50).Select(_ => $"\"{Guid.CreateVersion7()}\""));
        var items = string.Join(',', Enumerable.Range(0, 21).Select(index => $$"""{"title":"Item {{index}}"}"""));

        AssertInvalid(
            await QueueAsync(
                world,
                world.Items[0],
                "merge",
                $$"""{"source_item_ids":[{{sources}},"{{world.Items[0]}}"],"survivor_item_id":"{{world.Items[0]}}"}"""),
            "/payload/source_item_ids",
            "merge_sources_max");
        AssertInvalid(
            await QueueAsync(world, world.Items[0], "split", $$"""{"new_items":[{{items}}]}"""),
            "/payload/new_items",
            "split_items_max");
        var stranger = Guid.CreateVersion7();
        AssertInvalid(
            await QueueAsync(
                world,
                world.Items[0],
                "merge",
                $$"""{"source_item_ids":["{{world.Items[0]}}","{{world.Items[1]}}"],"survivor_item_id":"{{stranger}}"}"""),
            "/payload/survivor_item_id",
            "survivor_not_in_sources");
    }

    [Fact]
    public async Task List_FiltersOrdersAndPaginatesByKeyset()
    {
        await using var world = await StartAsync(4);
        var queued = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            queued.Add((await QueueAsync(world, world.Items[index], "comment",
                $$"""{"body":"note {{index}}"}""")).Id("change_id"));
        }

        (await QueueAsync(world, world.Items[3], "delete", "{}")).Status.ShouldBe(HttpStatusCode.Created);

        var all = await ListAsync(world, "");
        all.Status.ShouldBe(HttpStatusCode.OK, all.ErrorMessage);
        all.ETag.ShouldBeNull("a filtered page is not a selection");
        var ids = RowIds(all);
        ids.Count.ShouldBe(4);
        ids.Take(3).ShouldBe(queued, "queue order is (created_at, id) ascending");

        RowIds(await ListAsync(world, "?change_type=comment")).Count.ShouldBe(3);
        RowIds(await ListAsync(world, $"?target_id={world.Items[0]:D}")).ShouldBe([queued[0]]);

        var first = await ListAsync(world, "?limit=2");
        var next = await ListAsync(world, $"?limit=2&cursor={Uri.EscapeDataString(first.Text("next_cursor") ?? string.Empty)}");
        RowIds(first).Count.ShouldBe(2);
        RowIds(next).Count.ShouldBe(2);
        RowIds(next).Intersect(RowIds(first)).ShouldBeEmpty();

        AssertInvalid(await ListAsync(world, "?state=bogus"), "/state", "invalid");
        // The page size is read by the shared paging helper, which blames a value outside 1..200 as a range problem.
        AssertInvalid(await ListAsync(world, "?limit=201"), "/limit", "out_of_range");
        AssertInvalid(await ListAsync(world, "?cursor=not-a-cursor"), "/cursor", "malformed");
        AssertInvalid(
            await ListAsync(world, $"?ids={queued[0]:D}&state=pending"),
            "/ids",
            "ids_with_filters");
    }

    [Fact]
    public async Task List_Selection_CarriesTheBatchEtagAndStaleFlags()
    {
        await using var world = await StartAsync(3);
        var comment = (await QueueAsync(world, world.Items[0], "comment", """{"body":"note"}""")).Id("change_id");
        var merge = (await QueueAsync(world, world.Items[2], "merge", MergeJson(world.Items[2], world.Items[1])))
            .Id("change_id");
        var url = $"?ids={comment:D}&ids={merge:D}";

        var before = await ListAsync(world, url);

        before.Status.ShouldBe(HttpStatusCode.OK, before.ErrorMessage);
        // The tag names the hash's algorithm: `order:sha256:<hex>`, the same grammar the apply route parses.
        (before.ETag ?? string.Empty).ShouldMatch("^\"order:sha256:[0-9a-f]{64}\"$");
        before.Body.GetProperty("items").EnumerateArray().All(item => !item.GetProperty("stale").GetBoolean())
            .ShouldBeTrue();

        // A 13.1.1 edit of the merge's source after queueing moves that item's version: the change is stale.
        await fixture.ExecuteAsync(world.Database,
            $"update public.checklist_item set version = version + 1, updated_at = now() where id = '{world.Items[1]}'");
        var after = await ListAsync(world, $"?ids={comment:D},{merge:D}");

        after.ETag.ShouldNotBe(before.ETag);
        var flags = after.Body.GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("change_id").GetGuid(), item => item.GetProperty("stale").GetBoolean());
        flags[comment].ShouldBeFalse();
        flags[merge].ShouldBeTrue();
    }

    [Fact]
    public async Task Get_ForeignUnknownAndCrossProjectIds_AreOne404()
    {
        await using var world = await StartAsync(1);
        await using var other = await StartAsync(1);
        var mine = (await QueueAsync(world, world.Items[0], "comment", """{"body":"mine"}""")).Id("change_id");
        var theirs = (await QueueAsync(other, other.Items[0], "comment", """{"body":"theirs"}""")).Id("change_id");

        var foreign = await world.Client.SendAsync(HttpMethod.Get, world.ChangePath(theirs), world.Caller);
        var stranger = await world.Client.SendAsync(HttpMethod.Get, world.ChangePath(Guid.CreateVersion7()), world.Caller);
        var inProject = await world.Client.SendAsync(HttpMethod.Get, world.ChangePath(mine), world.Caller);

        inProject.Status.ShouldBe(HttpStatusCode.OK);
        foreign.Status.ShouldBe(HttpStatusCode.NotFound);
        stranger.Status.ShouldBe(HttpStatusCode.NotFound);
        WithoutRequestId(foreign).ShouldBe(WithoutRequestId(stranger));

        // Another project of the same organization answers the same way.
        var sibling = Guid.CreateVersion7();
        await fixture.ExecuteAsync(world.Database,
            $"insert into public.project (id, org_id, name, slug) values ('{sibling}', '{world.Org}', 'Sibling', 'sib-{sibling:N}')");
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{sibling:D}/authoring/changes/{mine:D}", world.Caller))
            .Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Discard_MovesPendingOnceAndRefusesAnyOtherState()
    {
        await using var world = await StartAsync(2);
        var pending = (await QueueAsync(world, world.Items[0], "comment", """{"body":"drop me"}""")).Id("change_id");

        var discarded = await world.Client.SendAsync(
            HttpMethod.Post, $"{world.ChangePath(pending)}/discard", world.Caller, null, NewKey());
        discarded.Status.ShouldBe(HttpStatusCode.OK, discarded.ErrorMessage);
        discarded.Text("state").ShouldBe("discarded");
        discarded.Body.TryGetProperty("discarded_at", out _).ShouldBeTrue();

        var again = await world.Client.SendAsync(
            HttpMethod.Post, $"{world.ChangePath(pending)}/discard", world.Caller, null, NewKey());
        again.Status.ShouldBe(HttpStatusCode.Conflict);
        again.ErrorCode.ShouldBe("invalid_transition");
        again.Body.GetProperty("error").GetProperty("details")[0].GetProperty("reason").GetString()
            .ShouldBe("change_not_pending");

        var applied = (await QueueAsync(world, world.Items[1], "comment", """{"body":"kept"}""")).Id("change_id");
        await fixture.ExecuteAsync(world.Database,
            $"""
            update public.authoring_change
               set state = 'applied', applied_at = now(), applied_batch_id = gen_random_uuid(),
                   applied_by_membership_id = '{world.Membership}'
             where id = '{applied}'
            """);
        (await world.Client.SendAsync(
            HttpMethod.Post, $"{world.ChangePath(applied)}/discard", world.Caller, null, NewKey()))
            .Status.ShouldBe(HttpStatusCode.Conflict);
        (await fixture.ScalarAsync<int>(world.Database,
            $"select count(*)::int from public.authoring_version where org_id = '{world.Org}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Queue_NeedsWriteAndTheReadRoutesNeedRead()
    {
        await using var world = await StartAsync(1);
        var viewer = await AddMemberAsync(world, "viewer");
        var asViewer = TenancyApiFactory.Resolved(user: viewer, orgHeader: world.Org);

        var write = await world.Client.SendAsync(
            HttpMethod.Post, world.ChangesPath, asViewer,
            new { target_kind = "checklist_item", target_id = world.Items[0], change_type = "comment", payload = new { body = "no" } },
            NewKey());
        write.Status.ShouldBe(HttpStatusCode.Forbidden);
        write.ErrorCode.ShouldBe("forbidden");
        (await world.Client.SendAsync(HttpMethod.Get, world.ChangesPath, asViewer)).Status.ShouldBe(HttpStatusCode.OK);
        (await ChangeCountAsync(world)).ShouldBe(0);

        // A caller whose request is narrowed to another project is refused before any row is read.
        var sibling = Guid.CreateVersion7();
        await fixture.ExecuteAsync(world.Database,
            $"insert into public.project (id, org_id, name, slug) values ('{sibling}', '{world.Org}', 'Elsewhere', 'else-{sibling:N}')");
        var narrowed = TenancyApiFactory.Caller(org: world.Org, user: world.User, project: sibling);
        var mismatch = await world.Client.SendAsync(
            HttpMethod.Post, world.ChangesPath, narrowed,
            new { target_kind = "checklist_item", target_id = world.Items[0], change_type = "comment", payload = new { body = "no" } },
            NewKey());
        mismatch.Status.ShouldBe(HttpStatusCode.Forbidden);
        mismatch.ErrorCode.ShouldBe("project_scope_mismatch");
    }

    [Fact]
    public async Task List_StatementsRunInAReadOnlyScope()
    {
        await using var world = await StartAsync(1);
        var queued = (await QueueAsync(world, world.Items[0], "comment", """{"body":"note"}""")).Id("change_id");

        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        var db = services.ServiceProvider.GetRequiredService<ITenantDb>();
        await using var scope = await scopes.BeginAsync(world.Org, world.Project, true, TestContext.Current.CancellationToken);

        var rows = await db.QueryAsync<AuthoringChangeSql.ChangeRow>(
            AuthoringChangeSql.Selection,
            new { org_id = world.Org, project_id = world.Project, ids = new[] { queued } },
            TestContext.Current.CancellationToken);
        rows.ShouldHaveSingleItem();

        var single = await db.QuerySingleOrDefaultAsync<AuthoringChangeSql.ChangeRow>(
            AuthoringChangeSql.Get,
            new { org_id = world.Org, project_id = world.Project, change_id = queued },
            TestContext.Current.CancellationToken);
        single.ShouldNotBeNull();

        // The same scope refuses a write: this is the BEGIN READ ONLY the read routes run inside.
        var refused = await Should.ThrowAsync<PostgresException>(() => db.ExecuteAsync(
            $"update public.checklist_item set title = title where id = '{world.Items[0]}'",
            null,
            TestContext.Current.CancellationToken));
        refused.SqlState.ShouldBe(PostgresErrorCodes.ReadOnlySqlTransaction);
    }
}
