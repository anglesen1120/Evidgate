using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T2.4/AC18 across the twelve mutating authoring routes instead of one route at a time: each successful command
/// appends exactly one <c>audit_log</c> row, the row carries the handler's <c>[AuditedAction]</c> name, the caller as its
/// <c>user</c> actor and the changed entity as its object, and it never carries an entity's title or step text.
/// </summary>
/// <remarks>
/// The action table below is the contract of 13.1.1 §5/AC18 (<c>authoring.&lt;entity&gt;.&lt;verb&gt;</c>) and the
/// <c>[AuditedAction]</c> of each handler, so a renamed attribute or an action that never reaches the trail fails here.
/// The audit row itself is read as the database stores it (<c>public.audit_log</c>, 0015:25-50), because the trail's
/// chain is the record and the API's own export is another story's.
/// </remarks>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class AuthoringAuditTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AuthoringAuditTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> As => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public string Tree => $"/v1/projects/{Project}/authoring/tree";

        public string Nodes => $"/v1/projects/{Project}/authoring/nodes";

        public string Node(Guid n) => $"{Nodes}/{n:D}";

        public string Items => $"/v1/projects/{Project}/authoring/checklist-items";

        public string Item(Guid i) => $"{Items}/{i:D}";

        public string Cases => $"/v1/projects/{Project}/authoring/manual-cases";

        public string Case(Guid m) => $"{Cases}/{m:D}";

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
    /// AC18: every mutating route of 13.1.1 §5 appends exactly one <c>audit_log</c> row — the org's, the caller's, the
    /// handler's action and the changed entity's object — and no entity title or step text rides along.
    /// </summary>
    [Fact]
    public async Task EveryMutation_WritesOneAuditRow()
    {
        await using var world = await StartAsync();

        // Targets of their own per route: the ladder, two extra nodes (one patched, one deleted), an item, a second item
        // as the move's target, an item written and deleted by routes 9/10, and a case mutated by routes 15/17/19/16.
        var module = await CreateAsync(world, world.Nodes, new { kind = "module", title = "Checkout" }, "node_id");
        var feature = await CreateAsync(world, world.Nodes, new { parent_id = module, kind = "feature", title = "Payment" }, "node_id");
        var category = await CreateAsync(world, world.Nodes, new { parent_id = feature, kind = "category", title = "Cards" }, "node_id");
        var patched = await CreateAsync(world, world.Nodes, new { parent_id = module, kind = "feature", title = "Refunds" }, "node_id");
        var removed = await CreateAsync(world, world.Nodes, new { parent_id = module, kind = "feature", title = "Archive" }, "node_id");
        var item = await CreateAsync(world, world.Items, new { node_id = category, title = "Item", priority = "high" }, "item_id");
        var target = await CreateAsync(world, world.Items, new { node_id = category, title = "Target item", priority = "low" }, "item_id");
        var writable = await CreateAsync(world, world.Items, new { node_id = category, title = "Writable item", priority = "medium" }, "item_id");
        var manualCase = await CreateAsync(world, world.Cases, new
        {
            checklist_item_id = item,
            title = "Pay by card",
            steps = new[] { new { no = 1, action = "Open the cart", expected = "The cart is shown" } },
            priority = "high",
        }, "case_id");

        // Route 2 — POST /nodes.
        await OneAuditRowAsync(
            world,
            "route 2 create node",
            () => world.Client.SendAsync(
                HttpMethod.Post, world.Nodes, world.As,
                new { parent_id = module, kind = "feature", title = "Receipts" }, NewKey()),
            HttpStatusCode.Created,
            "authoring.node.create",
            "feature_node",
            answer => answer.Body.GetProperty("node_id").GetGuid().ToString("D"));

        // Route 3 — PATCH /nodes/{n}.
        var nodeVersion = await NodeVersionAsync(world, patched);
        await OneAuditRowAsync(
            world,
            "route 3 patch node",
            () => world.Client.SendAsync(
                HttpMethod.Patch, world.Node(patched), world.As, new { title = "Refunds and returns" }, NewKey(), $"\"{nodeVersion}\""),
            HttpStatusCode.OK,
            "authoring.node.update",
            "feature_node",
            _ => patched.ToString("D"));

        // Route 4 — POST /nodes/{n}/reorder, with the order tag of the group as the tree route issues it.
        await OneAuditRowAsync(
            world,
            "route 4 reorder nodes",
            async () =>
            {
                var group = await world.Client.SendAsync(HttpMethod.Get, $"{world.Tree}?parent_id={module}", world.As);

                group.Status.ShouldBe(HttpStatusCode.OK, group.ErrorMessage);
                var ids = group.Body.GetProperty("items").EnumerateArray()
                    .Select(row => row.GetProperty("node_id").GetGuid())
                    .ToList();
                ids.Reverse();

                return await world.Client.SendAsync(
                    HttpMethod.Post, $"{world.Node(patched)}/reorder", world.As, new { ordered_ids = ids }, NewKey(), group.ETag);
            },
            HttpStatusCode.OK,
            "authoring.node.reorder",
            "feature_node",
            _ => patched.ToString("D"));

        // Route 5 — DELETE /nodes/{n}: the reorder above renumbered the group, so the tag is read again.
        var removedVersion = await NodeVersionAsync(world, removed);
        await OneAuditRowAsync(
            world,
            "route 5 delete node",
            () => world.Client.SendAsync(
                HttpMethod.Delete, world.Node(removed), world.As, new { reason = "no longer needed" }, NewKey(), $"\"{removedVersion}\""),
            HttpStatusCode.NoContent,
            "authoring.node.delete",
            "feature_node",
            _ => removed.ToString("D"));

        // Route 8 — POST /checklist-items.
        await OneAuditRowAsync(
            world,
            "route 8 create item",
            () => world.Client.SendAsync(
                HttpMethod.Post, world.Items, world.As,
                new { node_id = category, title = "Second item", priority = "critical" }, NewKey()),
            HttpStatusCode.Created,
            "authoring.checklist_item.create",
            "checklist_item",
            answer => answer.Body.GetProperty("item_id").GetGuid().ToString("D"));

        // Route 9 — PATCH /checklist-items/{i}.
        var itemPatch = await OneAuditRowAsync(
            world,
            "route 9 patch item",
            () => world.Client.SendAsync(
                HttpMethod.Patch, world.Item(writable), world.As, new { title = "Renamed item" }, NewKey(), "\"1\""),
            HttpStatusCode.OK,
            "authoring.checklist_item.update",
            "checklist_item",
            _ => writable.ToString("D"));

        // Route 10 — DELETE /checklist-items/{i}, at the version the patch answered.
        await OneAuditRowAsync(
            world,
            "route 10 delete item",
            () => world.Client.SendAsync(
                HttpMethod.Delete, world.Item(writable), world.As, new { reason = "duplicate" }, NewKey(),
                $"\"{itemPatch.Body.GetProperty("version").GetInt32()}\""),
            HttpStatusCode.NoContent,
            "authoring.checklist_item.delete",
            "checklist_item",
            _ => writable.ToString("D"));

        // Route 13 — POST /manual-cases.
        await OneAuditRowAsync(
            world,
            "route 13 create case",
            () => world.Client.SendAsync(
                HttpMethod.Post, world.Cases, world.As,
                new
                {
                    checklist_item_id = item,
                    title = "Refund by card",
                    steps = new[] { new { no = 1, action = "Refund the card", expected = "The refund is accepted" } },
                    priority = "medium",
                },
                NewKey()),
            HttpStatusCode.Created,
            "authoring.manual_case.create",
            "manual_test_case",
            answer => answer.Body.GetProperty("case_id").GetGuid().ToString("D"));

        // Route 15 — PATCH /manual-cases/{m}.
        var casePatch = await OneAuditRowAsync(
            world,
            "route 15 patch case",
            () => world.Client.SendAsync(
                HttpMethod.Patch, world.Case(manualCase), world.As, new { title = "Pay by card, saved" }, NewKey(), "\"1\""),
            HttpStatusCode.OK,
            "authoring.manual_case.update",
            "manual_test_case",
            _ => manualCase.ToString("D"));

        // Route 17 — POST /manual-cases/{m}/state (draft → approved, a real move, so the row is recorded).
        var state = await OneAuditRowAsync(
            world,
            "route 17 case state",
            () => world.Client.SendAsync(
                HttpMethod.Post, $"{world.Case(manualCase)}/state", world.As, new { state = "approved" }, NewKey(),
                $"\"{casePatch.Body.GetProperty("version").GetInt32()}\""),
            HttpStatusCode.OK,
            "authoring.manual_case.state",
            "manual_test_case",
            _ => manualCase.ToString("D"));

        // Route 19 — POST /manual-cases/{m}/item (a move to another item, so it is not a no-op).
        var move = await OneAuditRowAsync(
            world,
            "route 19 case move",
            () => world.Client.SendAsync(
                HttpMethod.Post, $"{world.Case(manualCase)}/item", world.As, new { checklist_item_id = target }, NewKey(),
                $"\"{state.Body.GetProperty("version").GetInt32()}\""),
            HttpStatusCode.OK,
            "authoring.manual_case.move",
            "manual_test_case",
            _ => manualCase.ToString("D"));

        // Route 16 — DELETE /manual-cases/{m}.
        await OneAuditRowAsync(
            world,
            "route 16 delete case",
            () => world.Client.SendAsync(
                HttpMethod.Delete, world.Case(manualCase), world.As, new { reason = "superseded" }, NewKey(),
                $"\"{move.Body.GetProperty("version").GetInt32()}\""),
            HttpStatusCode.NoContent,
            "authoring.manual_case.delete",
            "manual_test_case",
            _ => manualCase.ToString("D"));

        // AC18's second half: the trail records what changed, never what it said — no title and no step text anywhere.
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"""
            select count(*) from public.audit_log
            where org_id = '{world.Org}'
              and coalesce(metadata::text, '') ~ '(Refunds|Payment|Cards|Item|Pay by card|Open the cart|Writable)'
            """))
            .ShouldBe(0, "an audit row never carries an entity's title or its step text");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Runs one audited call and pins the row it appended: exactly one new row for the org, and the org's newest row is
    /// <c>action|object_type|object_id|actor_type|actor_id</c> with the caller as a <c>user</c> actor and no metadata.
    /// </summary>
    private async Task<ApiAnswer> OneAuditRowAsync(
        World world,
        string label,
        Func<Task<ApiAnswer>> call,
        HttpStatusCode expected,
        string action,
        string objectType,
        Func<ApiAnswer, string> objectId)
    {
        var before = await AuditCountAsync(world);
        var answer = await call();
        var after = await AuditCountAsync(world);

        answer.Status.ShouldBe(expected, $"{label} answers {expected}, not {answer.Status}: {answer.ErrorMessage}");
        after.ShouldBe(before + 1, $"{label} must append exactly one audit row");

        var row = await _fixture.ScalarAsync<string>(
            world.Database,
            $"""
            select action || '|' || coalesce(object_type, '-') || '|' || coalesce(object_id, '-') || '|'
                   || actor_type || '|' || actor_id
            from public.audit_log where org_id = '{world.Org}' order by seq desc limit 1
            """);

        row.ShouldBe($"{action}|{objectType}|{objectId(answer)}|user|{world.User:D}", $"{label}: the row names the action and its object");

        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select coalesce(metadata::text, '') from public.audit_log where org_id = '{world.Org}' order by seq desc limit 1"))
            .ShouldBeEmpty($"{label}: the authoring rows describe the entity, never its content");

        return answer;
    }

    private async Task<long> AuditCountAsync(World world) =>
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.audit_log where org_id = '{world.Org}'");

    /// <summary>The node's stored version, read through route 1 (<c>?node_id=</c>), for the next <c>If-Match</c>.</summary>
    private static async Task<int> NodeVersionAsync(World world, Guid nodeId)
    {
        var detail = await world.Client.SendAsync(HttpMethod.Get, $"{world.Tree}?node_id={nodeId}", world.As);

        detail.Status.ShouldBe(HttpStatusCode.OK, detail.ErrorMessage);

        return detail.Body.GetProperty("node").GetProperty("version").GetInt32();
    }

    /// <summary>Creates one entity through its route, asserting the 201 and answering the member the test needs.</summary>
    private static async Task<Guid> CreateAsync(World world, string url, object body, string member)
    {
        var created = await world.Client.SendAsync(HttpMethod.Post, url, world.As, body, NewKey());

        created.Status.ShouldBe(HttpStatusCode.Created, $"{url} answers 201, not {created.Status}: {created.ErrorMessage}");

        return created.Body.GetProperty(member).GetGuid();
    }
}
