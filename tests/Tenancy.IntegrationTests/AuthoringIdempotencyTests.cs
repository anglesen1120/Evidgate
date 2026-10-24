using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T2.4/AC18 and 00 §9.1: an <c>Idempotency-Key</c> replay of a create answers the first request's status and
/// body and writes nothing a second time — no entity row, no <c>authoring_version</c> row and no <c>audit_log</c> row —
/// while a different body under the same key is refused 409 <c>idempotency_conflict</c>.
/// </summary>
/// <remarks>
/// The three create routes are the ones whose second write would be visible twice: a node, an item whose position is
/// appended and a case whose code comes from <c>authoring_code_counter</c>, so a replay that ran the handler again would
/// allocate a second code. The replayed body is compared with <see cref="JsonElement.DeepEquals"/>, not as text, because
/// the stored response is jsonb and PostgreSQL does not keep member order.
/// </remarks>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class AuthoringIdempotencyTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AuthoringIdempotencyTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> As => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public string Nodes => $"/v1/projects/{Project}/authoring/nodes";

        public string Items => $"/v1/projects/{Project}/authoring/checklist-items";

        public string Cases => $"/v1/projects/{Project}/authoring/manual-cases";

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

    /// <summary>
    /// AC18: the same key and body twice is one write with one answer; a different body under that key is
    /// 409 <c>idempotency_conflict</c> and writes nothing.
    /// </summary>
    [Fact]
    public async Task Replay_ReturnsSameAnswer_NoSecondWrite()
    {
        await using var world = await StartAsync();

        var module = await CreateAsync(world, world.Nodes, new { kind = "module", title = "Checkout" }, "node_id");
        var feature = await CreateAsync(world, world.Nodes, new { parent_id = module, kind = "feature", title = "Payment" }, "node_id");
        var category = await CreateAsync(world, world.Nodes, new { parent_id = feature, kind = "category", title = "Cards" }, "node_id");
        var item = await CreateAsync(world, world.Items, new { node_id = category, title = "Item", priority = "high" }, "item_id");

        // Route 2: a node whose create would append a second sibling and a second version row if it ran twice.
        var nodeKey = NewKey();
        var nodeBody = new { parent_id = module, kind = "feature", title = "Refunds" };
        var nodeFirst = await world.Client.SendAsync(HttpMethod.Post, world.Nodes, world.As, nodeBody, nodeKey);
        var nodeSecond = await world.Client.SendAsync(HttpMethod.Post, world.Nodes, world.As, nodeBody, nodeKey);
        var nodeId = nodeFirst.Body.GetProperty("node_id").GetGuid();

        nodeFirst.Status.ShouldBe(HttpStatusCode.Created, nodeFirst.ErrorMessage);
        nodeSecond.Status.ShouldBe(HttpStatusCode.Created, nodeSecond.ErrorMessage);
        JsonElement.DeepEquals(nodeFirst.Body, nodeSecond.Body)
            .ShouldBeTrue($"the replay answers the first body, not {nodeSecond.Body.GetRawText()}");
        (await CountAsync(world, "feature_node", $"id = '{nodeId}'")).ShouldBe(1, "a replayed create writes one node row");
        (await CountAsync(world, "authoring_version", $"entity_id = '{nodeId}'")).ShouldBe(1, "a replayed create appends one version row");
        (await AuditedAsync(world, "authoring.node.create", nodeId)).ShouldBe(1, "a replayed create appends one audit row");

        // Route 8: an item, whose position is appended after its siblings.
        var itemKey = NewKey();
        var itemBody = new { node_id = category, title = "Second item", priority = "medium" };
        var itemFirst = await world.Client.SendAsync(HttpMethod.Post, world.Items, world.As, itemBody, itemKey);
        var itemSecond = await world.Client.SendAsync(HttpMethod.Post, world.Items, world.As, itemBody, itemKey);
        var itemId = itemFirst.Body.GetProperty("item_id").GetGuid();

        itemFirst.Status.ShouldBe(HttpStatusCode.Created, itemFirst.ErrorMessage);
        itemSecond.Status.ShouldBe(HttpStatusCode.Created, itemSecond.ErrorMessage);
        JsonElement.DeepEquals(itemFirst.Body, itemSecond.Body)
            .ShouldBeTrue($"the replay answers the first body, not {itemSecond.Body.GetRawText()}");
        (await CountAsync(world, "checklist_item", $"id = '{itemId}'")).ShouldBe(1, "a replayed create writes one item row");
        (await CountAsync(world, "authoring_version", $"entity_id = '{itemId}'")).ShouldBe(1, "a replayed create appends one version row");
        (await AuditedAsync(world, "authoring.checklist_item.create", itemId)).ShouldBe(1, "a replayed create appends one audit row");

        // Route 13: a case, whose second code allocation would also be visible in authoring_code_counter.
        var caseKey = NewKey();
        var caseBody = new
        {
            checklist_item_id = item,
            title = "Pay by card",
            steps = new[] { new { no = 1, action = "Open the cart", expected = "The cart is shown" } },
            priority = "high",
        };
        var caseFirst = await world.Client.SendAsync(HttpMethod.Post, world.Cases, world.As, caseBody, caseKey);
        var caseSecond = await world.Client.SendAsync(HttpMethod.Post, world.Cases, world.As, caseBody, caseKey);
        var caseId = caseFirst.Body.GetProperty("case_id").GetGuid();

        caseFirst.Status.ShouldBe(HttpStatusCode.Created, caseFirst.ErrorMessage);
        caseSecond.Status.ShouldBe(HttpStatusCode.Created, caseSecond.ErrorMessage);
        JsonElement.DeepEquals(caseFirst.Body, caseSecond.Body)
            .ShouldBeTrue($"the replay answers the first body, not {caseSecond.Body.GetRawText()}");
        (await CountAsync(world, "manual_test_case", $"id = '{caseId}'")).ShouldBe(1, "a replayed create writes one case row");
        (await CountAsync(world, "authoring_version", $"entity_id = '{caseId}'")).ShouldBe(1, "a replayed create appends one version row");
        (await AuditedAsync(world, "authoring.manual_case.create", caseId)).ShouldBe(1, "a replayed create appends one audit row");

        // A different body under the node key is not a replay: 409 idempotency_conflict, and nothing is written.
        var nodesBefore = await CountAsync(world, "feature_node", $"org_id = '{world.Org}'");
        var conflict = await world.Client.SendAsync(
            HttpMethod.Post, world.Nodes, world.As, new { parent_id = module, kind = "feature", title = "A different node" }, nodeKey);

        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        (await CountAsync(world, "feature_node", $"org_id = '{world.Org}'"))
            .ShouldBe(nodesBefore, "a conflicting key writes no node row");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Rows of one authoring table matching <paramref name="where"/>, read as the superuser (RLS-free).</summary>
    private async Task<long> CountAsync(World world, string table, string where) =>
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.{table} where {where}");

    /// <summary>The audit rows one action recorded for one object.</summary>
    private async Task<long> AuditedAsync(World world, string action, Guid objectId) =>
        await _fixture.ScalarAsync<long>(
            world.Database,
            $"""
            select count(*) from public.audit_log
            where org_id = '{world.Org}' and action = '{action}' and object_id = '{objectId:D}'
            """);

    /// <summary>Creates one entity through its route for the ladder the case create needs.</summary>
    private static async Task<Guid> CreateAsync(World world, string url, object body, string member)
    {
        var created = await world.Client.SendAsync(HttpMethod.Post, url, world.As, body, NewKey());

        created.Status.ShouldBe(HttpStatusCode.Created, $"{url} answers 201, not {created.Status}: {created.ErrorMessage}");

        return created.Body.GetProperty(member).GetGuid();
    }
}
