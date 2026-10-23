using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T2.2 routes 1-5 through the real host and a real PostgreSQL: the AC1 ladder, AC2's sibling order and two-phase
/// renumber, AC19's order precondition, AC7's delete guards, AC18's headers and AC10's cross-tenant 404. The tests pin the
/// HTTP contract of §5, never internals.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class AuthoringTreeTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AuthoringTreeTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> As => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public string NodeUrl(Guid node) => $"/v1/projects/{Project}/authoring/nodes/{node:D}";

        public string ReorderUrl(Guid node) => $"/v1/projects/{Project}/authoring/nodes/{node:D}/reorder";

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

    private static Guid CreatedId(AuthoringCalls.Answer answer)
    {
        answer.Status.ShouldBe(HttpStatusCode.Created);
        return answer.Member("node_id").GetGuid();
    }

    [Fact]
    public async Task ParentRules_RefuseWithReason()
    {
        await using var world = await StartAsync();

        var module = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "Checkout"));

        // A feature under a module is the one accepted step of the ladder.
        var feature = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, module, "feature", "Payment"));

        // A module under a module: the ladder only places `feature` there (AC1).
        var wrongKind = await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, module, "module", "Nested module");
        wrongKind.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        wrongKind.Reasons().ShouldContain("parent_kind_invalid");

        // A parent no route ever returned is the same refusal with the not-found reason, never a 404.
        var unknownParent = await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, Guid.CreateVersion7(), "feature", "Orphan");
        unknownParent.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        unknownParent.Reasons().ShouldContain("parent_not_found");

        // Deleted parent: the root is soft-deleted first, then a child is asked for under it.
        var doomed = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "Doomed"));
        var tree = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project, nodeId: doomed);
        (await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Delete,
            world.NodeUrl(doomed),
            json: null,
            ifMatch: tree.ETag is null ? null : "\"" + tree.Member("node").GetProperty("version").GetInt32() + "\"")).Status
            .ShouldBe(HttpStatusCode.NoContent);

        var deletedParent = await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, doomed, "feature", "After delete");
        deletedParent.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        deletedParent.Reasons().ShouldContain("parent_deleted");

        // The fifth level: category -> sub_category is the deepest pair the ladder allows.
        var category = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, feature, "category", "Card"));
        var sub = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, category, "sub_category", "Declined"));
        var fifth = await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, sub, "sub_category", "Too deep");
        fifth.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        fifth.Reasons().ShouldContain("depth_exceeded");
    }

    [Fact]
    public async Task SiblingPositions_AreUnique_AfterReorder()
    {
        await using var world = await StartAsync();

        var first = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "A"));
        var second = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "B"));
        var third = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "C"));

        var before = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project);
        before.Status.ShouldBe(HttpStatusCode.OK);
        before.ETag.ShouldNotBeNull();

        var items = before.Member("items").EnumerateArray().ToList();
        items.Select(item => item.GetProperty("node_id").GetGuid()).ShouldBe([first, second, third]);
        items.Select(item => item.GetProperty("position").GetInt32()).ShouldBe([0, 1, 2]);

        // Reversed: the two-phase renumber gives exactly 0..n-1 and a total order (AC2).
        var reordered = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Post,
            world.ReorderUrl(second),
            JsonSerializer.Serialize(new { ordered_ids = new[] { third, second, first } }),
            ifMatch: before.ETag);

        reordered.Status.ShouldBe(HttpStatusCode.OK);
        reordered.Member("changed").GetBoolean().ShouldBeTrue();

        var after = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project);
        var afterItems = after.Member("items").EnumerateArray().ToList();
        afterItems.Select(item => item.GetProperty("node_id").GetGuid()).ShouldBe([third, second, first]);
        afterItems.Select(item => item.GetProperty("position").GetInt32()).ShouldBe([0, 1, 2]);
        afterItems.Select(item => item.GetProperty("position").GetInt32()).Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public async Task Create_WithTakenPosition_422()
    {
        await using var world = await StartAsync();

        _ = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "A", position: 0));

        var taken = await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "B", position: 0);

        taken.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        taken.Body!.Value.GetProperty("error").GetProperty("code").GetString().ShouldBe("request_invalid");
        taken.Reasons().ShouldContain("position_taken");
    }

    [Fact]
    public async Task ReorderNodes_TwoStatements()
    {
        await using var world = await StartAsync();

        var first = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "A"));
        var second = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "B"));
        var third = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "C"));

        var before = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project);
        var versions = before.Member("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("node_id").GetGuid(), item => item.GetProperty("version").GetInt32());

        // Swap the first two siblings: `third` keeps position 2, so it is the row the renumber must not touch. (Any
        // rotation would move every row — an id list is the new order of the whole group, not a "move this one".)
        var reordered = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Post,
            world.ReorderUrl(first),
            JsonSerializer.Serialize(new { ordered_ids = new[] { second, first, third } }),
            ifMatch: before.ETag);
        reordered.Status.ShouldBe(HttpStatusCode.OK);

        var after = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project);
        var afterVersions = after.Member("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("node_id").GetGuid(), item => item.GetProperty("version").GetInt32());

        // AC2's renumber is two UPDATE statements in one transaction over the moved rows only, so the 0004 trigger bumps
        // a moved row exactly twice — and a row that did not move keeps the single bump of its create. A third statement
        // (or a per-change call into the writer's UpdateAsync) would show as a different delta.
        afterVersions[first].ShouldBe(versions[first] + 2);
        afterVersions[second].ShouldBe(versions[second] + 2);
        afterVersions[third].ShouldBe(versions[third]);
    }

    [Fact]
    public async Task Reorder_StaleOrderEtags_412()
    {
        await using var world = await StartAsync();

        var first = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "A"));
        var second = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "B"));

        var stale = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project);

        // A sibling arrives after the caller read the group: the tag no longer describes it.
        var third = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "C"));

        var refused = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Post,
            world.ReorderUrl(first),
            JsonSerializer.Serialize(new { ordered_ids = new[] { second, first, third } }),
            ifMatch: stale.ETag);

        refused.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        refused.Body!.Value.GetProperty("error").GetProperty("code").GetString().ShouldBe("precondition_failed");

        var unchanged = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project);
        unchanged.Member("items").EnumerateArray()
            .Select(item => item.GetProperty("node_id").GetGuid())
            .ShouldBe([first, second, third]);
    }

    [Fact]
    public async Task Delete_Guards_AndSoftDelete()
    {
        await using var world = await StartAsync();

        var module = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "Checkout"));
        var feature = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, module, "feature", "Payment"));

        var moduleTree = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project, nodeId: module);
        var moduleVersion = "\"" + moduleTree.Member("node").GetProperty("version").GetInt32() + "\"";

        var refused = await AuthoringCalls.SendAsync(world.Client, world.As, HttpMethod.Delete, world.NodeUrl(module), json: null, ifMatch: moduleVersion);
        refused.Status.ShouldBe(HttpStatusCode.Conflict);
        refused.Body!.Value.GetProperty("error").GetProperty("code").GetString().ShouldBe("authoring_not_empty");
        refused.Reasons().ShouldContain("node_has_children");

        var featureTree = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project, nodeId: feature);
        var featureVersion = "\"" + featureTree.Member("node").GetProperty("version").GetInt32() + "\"";

        var deleted = await AuthoringCalls.SendAsync(world.Client, world.As, HttpMethod.Delete, world.NodeUrl(feature), json: "{\"reason\":\"duplicate\"}", ifMatch: featureVersion);
        deleted.Status.ShouldBe(HttpStatusCode.NoContent);

        var children = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project, parentId: module);
        children.Member("items").GetArrayLength().ShouldBe(0);

        // The tree hides the tombstone; its row and its history stay (AC7).
        (await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project, nodeId: feature)).Status.ShouldBe(HttpStatusCode.NotFound);

        var again = await AuthoringCalls.SendAsync(world.Client, world.As, HttpMethod.Delete, world.NodeUrl(feature), json: null, ifMatch: featureVersion);
        again.Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task NodeRoutes_RequirePermissionHeadersAndTenant()
    {
        await using var world = await StartAsync();

        // 400 without an Idempotency-Key on a mutating route (AC18).
        var noKey = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Post,
            $"/v1/projects/{world.Project}/authoring/nodes",
            JsonSerializer.Serialize(new { kind = "module", title = "A" }),
            withIdempotencyKey: false);
        noKey.Status.ShouldBe(HttpStatusCode.BadRequest);
        noKey.Reasons().ShouldContain("required");

        var created = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "A"));

        // 422 without an If-Match on a mutation (AC19).
        var noMatch = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Patch,
            world.NodeUrl(created),
            JsonSerializer.Serialize(new { title = "B" }));
        noMatch.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        noMatch.Reasons().ShouldContain("required");

        // 403 for a caller with no membership of the tenant (AC10's other half: the scope gates run first).
        var outsider = Guid.CreateVersion7();
        var forbidden = await AuthoringCalls.SendAsync(
            world.Client,
            TenancyApiFactory.Resolved(user: outsider, orgHeader: world.Org),
            HttpMethod.Post,
            $"/v1/projects/{world.Project}/authoring/nodes",
            JsonSerializer.Serialize(new { kind = "module", title = "B" }));
        forbidden.Status.ShouldBe(HttpStatusCode.Forbidden);

        // Another tenant's project id is a 404, never a 403 (AC10).
        var otherOrg = await CreateOrgAsync(world.Client, world.User, "other");
        var crossTenant = await AuthoringCalls.SendAsync(
            world.Client,
            TenancyApiFactory.Resolved(user: world.User, orgHeader: otherOrg),
            HttpMethod.Get,
            $"/v1/projects/{world.Project}/authoring/tree",
            withIdempotencyKey: false);
        crossTenant.Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Tree_NodeReadCarriesPathAndCounts()
    {
        await using var world = await StartAsync();

        var module = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", "Checkout"));
        var feature = CreatedId(await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, module, "feature", "Payment"));

        var tree = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project, nodeId: feature, include: "path,counts");

        tree.Status.ShouldBe(HttpStatusCode.OK);
        tree.ETag.ShouldNotBeNull();
        tree.ETag!.ShouldStartWith("\"order:sha256:");
        tree.Member("node").GetProperty("parent_id").GetGuid().ShouldBe(module);
        tree.Member("node").GetProperty("child_count").GetInt32().ShouldBe(0);
        tree.Member("path").EnumerateArray().Select(entry => entry.GetProperty("node_id").GetGuid()).ShouldBe([module]);
        tree.Member("items").GetArrayLength().ShouldBe(0);

        var unknownInclude = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project, include: "links");
        unknownInclude.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        unknownInclude.Reasons().ShouldContain("invalid");
    }
}
