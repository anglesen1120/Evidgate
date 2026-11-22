using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.Modules.Authoring.Domain;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The queue and apply instruments through the real handlers: a queued change counts once under its type, an apply
/// counts under the outcome its refusal maps to (412 stale, 409 conflict) with a batch size only when it commits, and a
/// refusal outside that vocabulary counts nothing.
/// </summary>
[Trait("Category", "Docker")]
[Collection(ExclusiveTenancySuite.Name)]
public sealed class AuthoringMetricTests(PostgresTenancyFixture fixture)
{
    private const string Queued = EvidgateTelemetry.AuthoringChangeQueuedMetricName;
    private const string Apply = EvidgateTelemetry.AuthoringChangeApplyMetricName;
    private const string BatchSize = EvidgateTelemetry.AuthoringChangeApplyBatchSizeMetricName;

    [Fact]
    public async Task Queue_EachChangeType_CountsOnceUnderItsOwnType()
    {
        await using var world = await StartAsync(5);
        using var capture = new AuthoringMetricCapture();

        await QueueOkAsync(world, world.Items[0], "comment", """{"body":"note"}""");
        await QueueOkAsync(world, world.Items[1], "delete", "{}");
        await QueueOkAsync(world, world.Items[3], "merge", MergeJson(world.Items[3], world.Items[2]));
        await QueueOkAsync(world, world.Items[4], "split", """{"new_items":[{"title":"A"},{"title":"B"}]}""");

        capture.Taken.ShouldBe(
        [
            $"{Queued}|1|change_type=comment",
            $"{Queued}|1|change_type=delete",
            $"{Queued}|1|change_type=merge",
            $"{Queued}|1|change_type=split",
        ]);
    }

    [Fact]
    public async Task Queue_RefusedOverlapOrUnknownTarget_CountsOnlyTheAcceptedChange()
    {
        await using var world = await StartAsync(2);
        using var capture = new AuthoringMetricCapture();
        await QueueOkAsync(world, world.Items[1], "delete", "{}");

        var overlap = await QueueAsync(world, world.Items[1], "delete", "{}");
        var unknown = await QueueAsync(world, Guid.CreateVersion7(), "delete", "{}");

        overlap.Status.ShouldBe(HttpStatusCode.Conflict, overlap.ErrorMessage);
        unknown.Status.ShouldBe(HttpStatusCode.NotFound, unknown.ErrorMessage);
        capture.Taken.ShouldBe([$"{Queued}|1|change_type=delete"]);
    }

    [Fact]
    public async Task Apply_Committed_CountsAppliedWithTheBatchSize()
    {
        await using var world = await StartAsync(2);
        var first = await QueueOkAsync(world, world.Items[0], "delete", "{}");
        var second = await QueueOkAsync(world, world.Items[1], "delete", "{}");
        using var capture = new AuthoringMetricCapture();

        var applied = await ApplyAsync(world, [first, second], Etag(world.Items[0], world.Items[1]));

        applied.Status.ShouldBe(HttpStatusCode.OK, applied.ErrorMessage);
        // The histogram has no tags on purpose: the trailing separator is its empty tag list.
        capture.Taken.ShouldBe([$"{Apply}|1|outcome=applied", $"{BatchSize}|2|"]);
    }

    [Fact]
    public async Task Apply_SingleChange_RecordsABatchSizeOfOne()
    {
        await using var world = await StartAsync(1);
        var only = await QueueOkAsync(world, world.Items[0], "comment", """{"body":"note"}""");
        using var capture = new AuthoringMetricCapture();

        var applied = await ApplyAsync(world, [only], Etag(world.Items[0]));

        applied.Status.ShouldBe(HttpStatusCode.OK, applied.ErrorMessage);
        capture.Taken.ShouldBe([$"{Apply}|1|outcome=applied", $"{BatchSize}|1|"]);
    }

    [Fact]
    public async Task Apply_StaleIfMatch_CountsStaleAndNoBatchSize()
    {
        await using var world = await StartAsync(1);
        var change = await QueueOkAsync(world, world.Items[0], "delete", "{}");
        using var capture = new AuthoringMetricCapture();

        var refused = await ApplyAsync(world, [change], "0f0e0d0c0b0a09080706050403020100");

        refused.Status.ShouldBe(HttpStatusCode.PreconditionFailed, refused.ErrorMessage);

        // Control in the same world: the right tag applies, so the refusal was the tag and nothing else.
        var control = await ApplyAsync(world, [change], Etag(world.Items[0]));
        control.Status.ShouldBe(HttpStatusCode.OK, control.ErrorMessage);
        capture.Taken.ShouldBe([$"{Apply}|1|outcome=stale", $"{Apply}|1|outcome=applied", $"{BatchSize}|1|"]);
    }

    [Fact]
    public async Task Apply_ChangeNoLongerPending_CountsConflictAndNoBatchSize()
    {
        await using var world = await StartAsync(1);
        var change = await QueueOkAsync(world, world.Items[0], "comment", """{"body":"note"}""");
        var discarded = await world.Client.SendAsync(
            HttpMethod.Post, $"{world.ChangesPath}/{change:D}/discard", world.Caller, null, NewKey());
        discarded.Status.ShouldBe(HttpStatusCode.OK, discarded.ErrorMessage);
        using var capture = new AuthoringMetricCapture();

        var refused = await ApplyAsync(world, [change], Etag(world.Items[0]));

        refused.Status.ShouldBe(HttpStatusCode.Conflict, refused.ErrorMessage);
        capture.Taken.ShouldBe([$"{Apply}|1|outcome=conflict"]);
    }

    /// <summary>
    /// Records current behaviour, not a ruling: a delete refused because its target gained a live case is a 409, and the
    /// handler counts every 409 as <c>conflict</c>, although the plan words that outcome as "the change is no longer
    /// pending". A later decision on the label or the wording may change this pin.
    /// </summary>
    [Fact]
    public async Task Apply_DeleteTargetWithLiveCase_CurrentlyCountsConflict()
    {
        await using var world = await StartAsync(2);
        var change = await QueueOkAsync(world, world.Items[0], "delete", "{}");
        var control = await QueueOkAsync(world, world.Items[1], "delete", "{}");
        await fixture.ExecuteAsync(world.Database, $"""
            insert into public.manual_test_case (id, org_id, project_id, checklist_item_id, code, title, steps, priority, state, source)
            values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', '{world.Items[0]}', 'MET-001', 'Case',
                    jsonb_build_object('steps', jsonb_build_array(jsonb_build_object('no', 1, 'action', 'Open', 'expected', 'Shown'))),
                    'high', 'draft', 'manual')
            """);
        using var capture = new AuthoringMetricCapture();

        var refused = await ApplyAsync(world, [change], Etag(world.Items[0]));
        var allowed = await ApplyAsync(world, [control], Etag(world.Items[1]));

        refused.Status.ShouldBe(HttpStatusCode.Conflict, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("authoring_not_empty");
        allowed.Status.ShouldBe(HttpStatusCode.OK, allowed.ErrorMessage);
        capture.Taken.ShouldBe([$"{Apply}|1|outcome=conflict", $"{Apply}|1|outcome=applied", $"{BatchSize}|1|"]);
    }

    [Fact]
    public async Task Apply_UnknownChangeId_CountsNothing()
    {
        await using var world = await StartAsync(1);
        using var capture = new AuthoringMetricCapture();

        var refused = await ApplyAsync(world, [Guid.CreateVersion7()], Etag(world.Items[0]));

        refused.Status.ShouldBe(HttpStatusCode.NotFound, refused.ErrorMessage);
        capture.Taken.ShouldBeEmpty();
    }

    [Fact]
    public async Task Apply_EmptySelection_CountsNothing()
    {
        await using var world = await StartAsync(1);
        using var capture = new AuthoringMetricCapture();

        var refused = await ApplyAsync(world, [], Etag(world.Items[0]));

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        capture.Taken.ShouldBeEmpty();
    }

    [Fact]
    public async Task Apply_MissingIfMatch_CountsNothing()
    {
        await using var world = await StartAsync(1);
        var change = await QueueOkAsync(world, world.Items[0], "delete", "{}");
        using var capture = new AuthoringMetricCapture();

        var refused = await world.Client.SendAsync(
            HttpMethod.Post, world.ApplyPath, world.Caller, new { change_ids = new[] { change } }, NewKey());

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("request_invalid");
        capture.Taken.ShouldBeEmpty();
    }

    private static string Etag(params Guid[] items) =>
        AuthoringHashes.OrderHash(items.Select(item => (item, 1)).ToArray());

    private static string MergeJson(Guid survivor, Guid other) =>
        $$"""{"source_item_ids":["{{survivor}}","{{other}}"],"survivor_item_id":"{{survivor}}"}""";

    private static Task<ApiAnswer> QueueAsync(World world, Guid target, string changeType, string payload) =>
        world.Client.SendAsync(
            HttpMethod.Post,
            world.ChangesPath,
            world.Caller,
            new
            {
                target_kind = "checklist_item",
                target_id = target,
                change_type = changeType,
                payload = JsonDocument.Parse(payload).RootElement.Clone(),
            },
            NewKey());

    private static async Task<Guid> QueueOkAsync(World world, Guid target, string changeType, string payload)
    {
        var queued = await QueueAsync(world, target, changeType, payload);
        queued.Status.ShouldBe(HttpStatusCode.Created, queued.ErrorMessage);

        return queued.Id("change_id");
    }

    private static Task<ApiAnswer> ApplyAsync(World world, Guid[] changeIds, string etag) =>
        world.Client.SendAsync(
            HttpMethod.Post, world.ApplyPath, world.Caller, new { change_ids = changeIds }, NewKey(), $"\"{etag}\"");

    private async Task<World> StartAsync(int itemCount)
    {
        var database = await fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "metrics-" + Guid.CreateVersion7().ToString("N"));
        var project = await CreateProjectAsync(client, user, org, "metrics-" + Guid.CreateVersion7().ToString("N"));
        var caller = TenancyApiFactory.Resolved(user: user, orgHeader: org);
        var module = Guid.CreateVersion7();
        var feature = Guid.CreateVersion7();
        var category = Guid.CreateVersion7();

        await fixture.ExecuteAsync(database, $"""
            insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position)
            values ('{module}', '{org}', '{project}', null, 'module', 'Shop', 0),
                   ('{feature}', '{org}', '{project}', '{module}', 'feature', 'Checkout', 0),
                   ('{category}', '{org}', '{project}', '{feature}', 'category', 'Payment', 0);
            """);

        // Items go through the real create route so each has the version-1 history an apply extends.
        var items = new List<Guid>();
        for (var index = 0; index < itemCount; index++)
        {
            var created = await client.SendAsync(
                HttpMethod.Post,
                $"/v1/projects/{project:D}/authoring/checklist-items",
                caller,
                new { node_id = category, title = $"Item {index}", priority = "high" },
                NewKey());
            created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
            items.Add(created.Id("item_id"));
        }

        return new World(database, api, client, org, project, items, caller);
    }

    private sealed record World(
        string Database,
        TenancyApiFactory Api,
        HttpClient Client,
        Guid Org,
        Guid Project,
        IReadOnlyList<Guid> Items,
        Action<HttpRequestHeaders> Caller) : IAsyncDisposable
    {
        internal string ChangesPath => $"/v1/projects/{Project:D}/authoring/changes";

        internal string ApplyPath => $"{ChangesPath}:apply";

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }
}
