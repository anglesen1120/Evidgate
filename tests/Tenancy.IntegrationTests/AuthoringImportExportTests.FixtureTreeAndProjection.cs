using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.AuthoringImportCalls;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringImportExportTests
{
    // ------------------------------------------------------------------ the fixture tree

    /// <summary>
    /// Authors the tree of AC14 in project A: two modules at the root, the four-level ladder of AC1, items under
    /// categories, cases with multi-step documents — one step carrying <c>data</c> — a decomposed unicode title and
    /// formula-leading titles, so every field the projection compares is exercised.
    /// </summary>
    /// <param name="world">The world whose source project receives the tree.</param>
    private static async Task FillAsync(World world)
    {
        var checkout = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, null, "module", "Checkout");
        var payment = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, checkout, "feature", "Payment");
        var cards = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, payment, "category", "Cards");
        var secure = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, cards, "sub_category", "3-D Secure");

        var payByCard = await CreateItemAsync(world, world.AsSource, world.SourceAuthoring, secure, "Pay by card", "A card payment", "high");
        await CreateCaseAsync(
            world, world.AsSource, world.SourceAuthoring, payByCard, "Card succeeds", "high",
            Step(1, "Open the cart", "The cart is shown"),
            Step(2, "Add a card", "The card is listed", "4242"),
            Step(3, "Pay", "The order is placed"));
        await CreateCaseAsync(world, world.AsSource, world.SourceAuthoring, payByCard, "Card declines", "critical", Step(1, "Pay with 4000", "A decline is shown"));

        var refund = await CreateItemAsync(world, world.AsSource, world.SourceAuthoring, cards, "Refund a card", null, "critical");
        await CreateCaseAsync(world, world.AsSource, world.SourceAuthoring, refund, "Refund is issued", "medium", Step(1, "Refund", "The refund is issued"));

        var wallets = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, payment, "category", "Wallets");
        var wallet = await CreateItemAsync(world, world.AsSource, world.SourceAuthoring, wallets, "Pay by wallet", "A wallet payment", "medium");
        await CreateCaseAsync(world, world.AsSource, world.SourceAuthoring, wallet, "Wallet succeeds", "medium", Step(1, "Pay by wallet", "The order is placed"));

        var warehouse = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, null, "module", "Warehouse");
        var stock = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, warehouse, "feature", "Stock");
        var count = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, stock, "category", "Count");
        var countItem = await CreateItemAsync(world, world.AsSource, world.SourceAuthoring, count, "Count stock", null, "low");
        await CreateCaseAsync(world, world.AsSource, world.SourceAuthoring, countItem, "Count is exact", "low", Step(1, "Count", "The count matches"));

        // The unicode and formula titles of AC14/AC16, on the one ladder that still accepts children.
        var accents = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, warehouse, "feature", "Ca\u0300 phe\u0302");
        var formulas = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, accents, "category", "=1+1");
        var formulaItem = await CreateItemAsync(world, world.AsSource, world.SourceAuthoring, formulas, "Item +SUM(A1)", null, "medium");
        await CreateCaseAsync(world, world.AsSource, world.SourceAuthoring, formulaItem, "+SUM(A1) case", "medium", Step(1, "Evaluate", "Nothing is evaluated"));
    }

    /// <summary>One case step; <paramref name="data"/> is omitted when null (the schema refuses a non-string <c>data</c>).</summary>
    /// <param name="no">The 1-based step number.</param>
    /// <param name="action">The action text.</param>
    /// <param name="expected">The expected result.</param>
    /// <param name="data">The optional data text.</param>
    private static Dictionary<string, object> Step(int no, string action, string expected, string? data = null)
    {
        var step = new Dictionary<string, object> { ["no"] = no, ["action"] = action, ["expected"] = expected };

        if (data is not null)
        {
            step["data"] = data;
        }

        return step;
    }

    /// <summary>Creates one node through its route and returns its id.</summary>
    /// <param name="world">The world.</param>
    /// <param name="authorize">The caller.</param>
    /// <param name="authoring">The project's authoring base URL.</param>
    /// <param name="parent">The parent node, or null for a root module.</param>
    /// <param name="kind">The node kind.</param>
    /// <param name="title">The title as the author typed it.</param>
    private static async Task<Guid> CreateNodeAsync(
        World world,
        Action<HttpRequestHeaders> authorize,
        string authoring,
        Guid? parent,
        string kind,
        string title)
    {
        object body = parent is { } parentId
            ? new { parent_id = parentId, kind, title }
            : new { kind, title };

        var created = await world.Client.SendAsync(HttpMethod.Post, $"{authoring}/nodes", authorize, body, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, $"{kind} '{title}': {created.ErrorMessage}");

        return created.Id("node_id");
    }

    /// <summary>Creates one checklist item through its route and returns its id.</summary>
    /// <param name="world">The world.</param>
    /// <param name="authorize">The caller.</param>
    /// <param name="authoring">The project's authoring base URL.</param>
    /// <param name="node">The owning node.</param>
    /// <param name="title">The title.</param>
    /// <param name="description">The description, or null.</param>
    /// <param name="priority">The priority.</param>
    private static async Task<Guid> CreateItemAsync(
        World world,
        Action<HttpRequestHeaders> authorize,
        string authoring,
        Guid node,
        string title,
        string? description,
        string priority)
    {
        object body = description is null
            ? new { node_id = node, title, priority }
            : new { node_id = node, title, description, priority };

        var created = await world.Client.SendAsync(HttpMethod.Post, $"{authoring}/checklist-items", authorize, body, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, $"item '{title}': {created.ErrorMessage}");

        return created.Id("item_id");
    }

    /// <summary>Creates one manual case through its route and returns its id and allocated code.</summary>
    /// <param name="world">The world.</param>
    /// <param name="authorize">The caller.</param>
    /// <param name="authoring">The project's authoring base URL.</param>
    /// <param name="item">The owning item.</param>
    /// <param name="title">The title.</param>
    /// <param name="priority">The priority.</param>
    /// <param name="steps">The steps, as the schema's step objects.</param>
    private static async Task<(Guid Id, string Code)> CreateCaseAsync(
        World world,
        Action<HttpRequestHeaders> authorize,
        string authoring,
        Guid item,
        string title,
        string priority,
        params Dictionary<string, object>[] steps)
    {
        var created = await world.Client.SendAsync(
            HttpMethod.Post,
            $"{authoring}/manual-cases",
            authorize,
            new { checklist_item_id = item, title, steps, priority },
            NewKey());

        created.Status.ShouldBe(HttpStatusCode.Created, $"case '{title}': {created.ErrorMessage}");

        var code = created.Text("code");
        code.ShouldNotBeNull($"case '{title}' must come back with its allocated code");

        return (created.Id("case_id"), code!);
    }

    // ------------------------------------------------------------------ the export-normalised projection

    /// <summary>One node as the key assignment needs it.</summary>
    /// <param name="Id">The entity id.</param>
    /// <param name="Parent">The parent node, or null for a root.</param>
    private sealed record NodeRow(Guid Id, Guid? Parent);

    /// <summary>One item as the key assignment needs it.</summary>
    /// <param name="Id">The entity id.</param>
    /// <param name="Node">The owning node.</param>
    private sealed record ItemRow(Guid Id, Guid Node);

    /// <summary>One case as the key assignment needs it.</summary>
    /// <param name="Id">The entity id.</param>
    /// <param name="Item">The owning item.</param>
    private sealed record CaseRow(Guid Id, Guid Item);

    /// <summary>
    /// The export-normalised projection of each entity's latest <c>authoring_version</c> snapshot in one project, in the
    /// export's own key order: the §4 field list of each entity with its three project-local uuid members replaced by the row's export key
    /// (<c>n1…</c>, <c>i1…</c>, <c>c1…</c> numbered in depth-first tree order). Two projects are equal when their lists
    /// are, whatever ids the database assigned.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="project">The project.</param>
    private async Task<List<string>> ProjectionAsync(string database, Guid project)
    {
        var nodes = (await _fixture.ColumnAsync<string>(
                database,
                $"""
                select json_build_object('id', id, 'parent', parent_id)::text from public.feature_node
                where project_id = '{project:D}' order by position, id
                """))
            .Select(row =>
            {
                using var document = JsonDocument.Parse(row);
                var root = document.RootElement;
                return new NodeRow(
                    root.GetProperty("id").GetGuid(),
                    root.GetProperty("parent").ValueKind == JsonValueKind.Null ? null : root.GetProperty("parent").GetGuid());
            })
            .ToList();

        var items = (await _fixture.ColumnAsync<string>(
                database,
                $"""
                select json_build_object('id', id, 'node', feature_node_id)::text from public.checklist_item
                where project_id = '{project:D}' order by position, id
                """))
            .Select(row =>
            {
                using var document = JsonDocument.Parse(row);
                var root = document.RootElement;
                return new ItemRow(root.GetProperty("id").GetGuid(), root.GetProperty("node").GetGuid());
            })
            .ToList();

        var cases = (await _fixture.ColumnAsync<string>(
                database,
                $"""
                select json_build_object('id', id, 'item', checklist_item_id)::text from public.manual_test_case
                where project_id = '{project:D}' order by code, id
                """))
            .Select(row =>
            {
                using var document = JsonDocument.Parse(row);
                var root = document.RootElement;
                return new CaseRow(root.GetProperty("id").GetGuid(), root.GetProperty("item").GetGuid());
            })
            .ToList();

        var contents = new Dictionary<Guid, JsonElement>();
        foreach (var row in await _fixture.ColumnAsync<string>(
            database,
            $"""
            select json_build_object('id', entity_id, 'content', content)::text
            from (select distinct on (entity_id) entity_id, content from public.authoring_version
                  where project_id = '{project:D}' order by entity_id, version_no desc) snapshots
            """))
        {
            using var document = JsonDocument.Parse(row);
            contents[document.RootElement.GetProperty("id").GetGuid()] = document.RootElement.GetProperty("content").Clone();
        }

        var children = nodes.Where(node => node.Parent is not null).GroupBy(node => node.Parent!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        var itemsByNode = items.GroupBy(item => item.Node).ToDictionary(group => group.Key, group => group.ToList());
        var casesByItem = cases.GroupBy(testCase => testCase.Item).ToDictionary(group => group.Key, group => group.ToList());

        var keys = new Dictionary<Guid, string>();
        var order = new List<(string Kind, Guid Id)>();
        var nodeNumber = 0;
        var itemNumber = 0;
        var caseNumber = 0;

        void Visit(Guid node)
        {
            keys[node] = "n" + (++nodeNumber).ToString(System.Globalization.CultureInfo.InvariantCulture);
            order.Add(("node", node));

            foreach (var item in itemsByNode.GetValueOrDefault(node) ?? [])
            {
                keys[item.Id] = "i" + (++itemNumber).ToString(System.Globalization.CultureInfo.InvariantCulture);
                order.Add(("item", item.Id));

                foreach (var testCase in casesByItem.GetValueOrDefault(item.Id) ?? [])
                {
                    keys[testCase.Id] = "c" + (++caseNumber).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    order.Add(("case", testCase.Id));
                }
            }

            foreach (var child in children.GetValueOrDefault(node) ?? [])
            {
                Visit(child.Id);
            }
        }

        foreach (var root in nodes.Where(node => node.Parent is null))
        {
            Visit(root.Id);
        }

        var projection = new List<string>(order.Count);

        foreach (var (kind, id) in order)
        {
            var content = contents[id];
            var view = new JsonObject { ["kind"] = kind };

            switch (kind)
            {
                case "node":
                    view["node_kind"] = Member(content, "kind");
                    view["title"] = Member(content, "title");
                    view["position"] = Member(content, "position");
                    view["parent"] = Reference(content, "parent_id", keys);
                    break;

                case "item":
                    view["node"] = Reference(content, "feature_node_id", keys);
                    view["title"] = Member(content, "title");
                    view["description"] = Member(content, "description");
                    view["priority"] = Member(content, "priority");
                    view["state"] = Member(content, "state");
                    view["position"] = Member(content, "position");
                    break;

                default:
                    view["item"] = Reference(content, "checklist_item_id", keys);
                    view["code"] = Member(content, "code");
                    view["title"] = Member(content, "title");
                    view["description"] = Member(content, "description");
                    view["precondition"] = Member(content, "precondition");
                    view["steps"] = Member(content, "steps");
                    view["priority"] = Member(content, "priority");
                    view["state"] = Member(content, "state");
                    break;
            }

            projection.Add(view.ToJsonString());
        }

        return projection;
    }

    /// <summary>One member of a snapshot, copied as a JSON node (null included).</summary>
    /// <param name="content">The snapshot.</param>
    /// <param name="name">The member name.</param>
    private static JsonNode? Member(JsonElement content, string name) =>
        content.GetProperty(name).ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(content.GetProperty(name).GetRawText());

    /// <summary>One project-local uuid member of a snapshot, replaced by the export key of the entity it names.</summary>
    /// <param name="content">The snapshot.</param>
    /// <param name="name">The member name.</param>
    /// <param name="keys">The entity-to-key map of the project.</param>
    private static JsonNode? Reference(JsonElement content, string name, Dictionary<Guid, string> keys)
    {
        var member = content.GetProperty(name);

        return member.ValueKind == JsonValueKind.Null ? null : keys[member.GetGuid()];
    }

    /// <summary>The rows one workbook carries, read by the module's own reader.</summary>
    /// <param name="bytes">The workbook.</param>
    private static IReadOnlyList<Evidgate.Modules.Authoring.Application.Import.ImportRow> XlsxRows(byte[] bytes)
    {
        var parse = ReadXlsx(bytes);

        parse.TooLarge.ShouldBeFalse("an exported workbook is inside the caps");
        parse.Issues.ShouldBeEmpty("the module's own reader parses the module's own workbook");
        parse.Rows.Count.ShouldBeGreaterThan(0);

        return parse.Rows;
    }
}
