using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Independent QA of 13.1.1-T2's checklist-item and manual-case routes (7-10, 12-17, 19): the AC3 parent kind and
/// priority rules, the AC7/AC20 item guards and state refusals at their reason boundaries, AC21's retire-reason rule, the
/// steps document at 0/1/100/101 entries and at the 2 000-character field cap, AC22's move guards and reason bound, the
/// list-query bounds (<c>q</c>, <c>limit</c>, <c>cursor</c>), AC18's replay conflict and key bound, and the 404 every verb
/// answers for a soft-deleted entity (AC10).
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class AuthoringQaItemsCasesTests
{
    private readonly PostgresTenancyFixture _fixture;

    /// <summary>Wires the fixture.</summary>
    /// <param name="fixture">The shared PostgreSQL fixture.</param>
    public AuthoringQaItemsCasesTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        Guid Category,
        Guid SiblingCategory) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> As => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

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
        var org = await CreateOrgAsync(client, user, "qa-items");
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var asFounder = TenancyApiFactory.Resolved(user: user, orgHeader: org);
        var nodes = $"/v1/projects/{project}/authoring/nodes";

        var module = (await client.SendAsync(
            HttpMethod.Post, nodes, asFounder, new { kind = "module", title = "Checkout" }, NewKey())).Id("node_id");
        var feature = (await client.SendAsync(
            HttpMethod.Post, nodes, asFounder, new { parent_id = module, kind = "feature", title = "Payment" }, NewKey())).Id("node_id");
        var category = (await client.SendAsync(
            HttpMethod.Post, nodes, asFounder, new { parent_id = feature, kind = "category", title = "Cards" }, NewKey())).Id("node_id");
        var sibling = (await client.SendAsync(
            HttpMethod.Post, nodes, asFounder, new { parent_id = feature, kind = "category", title = "Wallets" }, NewKey())).Id("node_id");

        return new World(api, client, database, user, org, project, category, sibling);
    }

    private static async Task<Guid> CreatedAsync(ApiAnswer answer, string member)
    {
        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);

        return answer.Id(member);
    }

    private static async Task<Guid> CreateItemAsync(World world, Guid node, string title = "An item")
    {
        var created = await world.Client.SendAsync(
            HttpMethod.Post, world.Items, world.As, new { node_id = node, title, priority = "high" }, NewKey());

        return await CreatedAsync(created, "item_id");
    }

    private static async Task<Guid> CreateCaseAsync(World world, Guid item, int count = 1, int actionLength = 5)
    {
        var created = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Cases,
            world.As,
            new
            {
                checklist_item_id = item,
                title = "Case",
                steps = Enumerable.Range(1, count)
                    .Select(no => new { no, action = new string('a', actionLength), expected = "shown" })
                    .ToArray(),
                priority = "high",
            },
            NewKey());

        return await CreatedAsync(created, "case_id");
    }

    private static List<(string? Path, string? Reason)> Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => (
                detail.TryGetProperty("path", out var path) ? path.GetString() : null,
                detail.TryGetProperty("reason", out var reason) ? reason.GetString() : null))];

    /// <summary>
    /// AC3: an item attaches to a category or a sub-category and nowhere else, its priority is one of the four values the
    /// CHECK also enforces, and its title is bounded — each refusal names the member it refuses.
    /// </summary>
    [Fact]
    public async Task ItemParentKindPriorityAndTitle_AreRefusedByName()
    {
        await using var world = await StartAsync();

        var nodes = $"/v1/projects/{world.Project}/authoring/nodes";
        var module = (await world.Client.SendAsync(
            HttpMethod.Post, nodes, world.As, new { kind = "module", title = "Root" }, NewKey())).Id("node_id");
        var feature = (await world.Client.SendAsync(
            HttpMethod.Post, nodes, world.As, new { parent_id = module, kind = "feature", title = "Feature" }, NewKey())).Id("node_id");
        var subCategory = (await world.Client.SendAsync(
            HttpMethod.Post, nodes, world.As, new { parent_id = world.Category, kind = "sub_category", title = "Sub" }, NewKey())).Id("node_id");

        foreach (var (node, label) in new[] { (module, "under module"), (feature, "under feature") })
        {
            var refused = await world.Client.SendAsync(
                HttpMethod.Post, world.Items, world.As, new { node_id = node, title = "Item", priority = "high" }, NewKey());

            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, label);
            refused.ErrorCode.ShouldBe("authoring_parent_invalid", label);
            Details(refused).ShouldContain(detail => detail.Reason == "parent_kind_invalid", label);
        }

        // Both accepted parent kinds, the control.
        _ = await CreateItemAsync(world, world.Category);
        _ = await CreateItemAsync(world, subCategory);

        var priority = await world.Client.SendAsync(
            HttpMethod.Post, world.Items, world.As, new { node_id = world.Category, title = "Item", priority = "urgent" }, NewKey());
        priority.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        priority.ErrorCode.ShouldBe("request_invalid");
        Details(priority).ShouldContain(detail => detail.Path == "/priority");

        var title = await world.Client.SendAsync(
            HttpMethod.Post, world.Items, world.As, new { node_id = world.Category, title = new string('t', 501), priority = "low" }, NewKey());
        title.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(title).ShouldContain(detail => detail.Path == "/title");

        // The missing priority is its own answer, not a silent default.
        var missing = await world.Client.SendAsync(
            HttpMethod.Post, world.Items, world.As, new { node_id = world.Category, title = "Item" }, NewKey());
        missing.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(missing).ShouldContain(detail => detail.Path == "/priority");
    }

    /// <summary>
    /// AC7/AC20/§6: an item that still holds a live case is <c>409 item_has_cases</c>; retiring needs a reason of 1..500
    /// characters; a retired item refuses every edit and every return to a live state with <c>409 invalid_transition</c>;
    /// once the case is gone the delete succeeds and the tombstone answers 404 on every verb (AC10).
    /// </summary>
    [Fact]
    public async Task ItemGuardsRetireAndSoftDelete_AnswerPerSection6()
    {
        await using var world = await StartAsync();

        var item = await CreateItemAsync(world, world.Category);
        var manualCase = await CreateCaseAsync(world, item);

        var guarded = await world.Client.SendAsync(HttpMethod.Delete, world.Item(item), world.As, null, NewKey(), "\"1\"");
        guarded.Status.ShouldBe(HttpStatusCode.Conflict);
        guarded.ErrorCode.ShouldBe("authoring_not_empty");
        Details(guarded).ShouldContain(detail => detail.Reason == "item_has_cases");

        var noReason = await world.Client.SendAsync(
            HttpMethod.Patch, world.Item(item), world.As, new { state = "retired" }, NewKey(), "\"1\"");
        noReason.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        noReason.ErrorCode.ShouldBe("request_invalid");
        Details(noReason).ShouldContain(detail => detail.Path == "/reason" && detail.Reason == "required");

        var longReason = await world.Client.SendAsync(
            HttpMethod.Patch, world.Item(item), world.As, new { state = "retired", reason = new string('r', 501) }, NewKey(), "\"1\"");
        longReason.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(longReason).ShouldContain(detail => detail.Path == "/reason");

        var retired = await world.Client.SendAsync(
            HttpMethod.Patch, world.Item(item), world.As,
            new { state = "retired", reason = new string('r', 500) }, NewKey(), "\"1\"");
        retired.Status.ShouldBe(HttpStatusCode.OK, retired.ErrorMessage);
        retired.Body.GetProperty("changed").GetBoolean().ShouldBeTrue();

        // §6: `retired` is terminal for edits — a title change and a return to a live state are both 409.
        var edited = await world.Client.SendAsync(
            HttpMethod.Patch, world.Item(item), world.As, new { title = "Renamed" }, NewKey(), "\"2\"");
        edited.Status.ShouldBe(HttpStatusCode.Conflict);
        edited.ErrorCode.ShouldBe("invalid_transition");

        var revived = await world.Client.SendAsync(
            HttpMethod.Patch, world.Item(item), world.As, new { state = "active" }, NewKey(), "\"2\"");
        revived.Status.ShouldBe(HttpStatusCode.Conflict);
        revived.ErrorCode.ShouldBe("invalid_transition");

        // The case can still be deleted (it holds nothing), and then the item's delete goes through.
        (await world.Client.SendAsync(HttpMethod.Delete, world.Case(manualCase), world.As, null, NewKey(), "\"1\""))
            .Status.ShouldBe(HttpStatusCode.NoContent);

        var deleted = await world.Client.SendAsync(HttpMethod.Delete, world.Item(item), world.As, null, NewKey(), "\"2\"");
        deleted.Status.ShouldBe(HttpStatusCode.NoContent, deleted.ErrorMessage);

        foreach (var second in new[]
        {
            await world.Client.SendAsync(HttpMethod.Delete, world.Item(item), world.As, null, NewKey(), "\"3\""),
            await world.Client.SendAsync(HttpMethod.Patch, world.Item(item), world.As, new { title = "x" }, NewKey(), "\"3\""),
        })
        {
            second.Status.ShouldBe(HttpStatusCode.NotFound);
            second.ErrorCode.ShouldBe("not_found");
        }

        var listed = await world.Client.SendAsync(HttpMethod.Get, $"{world.Items}?node_id={world.Category}", world.As);
        listed.Status.ShouldBe(HttpStatusCode.OK, listed.ErrorMessage);
        listed.Body.GetProperty("items").EnumerateArray()
            .Select(row => row.GetProperty("item_id").GetGuid())
            .ShouldNotContain(item, "a soft-deleted item is hidden from the list");
    }

    /// <summary>
    /// Routes 13/15 at the steps document's bounds (contracts/schemas/authoring-steps.v1.json): 0 and 101 entries are
    /// refused, 100 are accepted, and a 2 000-character field passes while 2 001 is refused — always as a 422 and never
    /// after a code number was burned.
    /// </summary>
    [Fact]
    public async Task StepsDocument_AtItsEntryAndLengthBounds()
    {
        await using var world = await StartAsync();
        var item = await CreateItemAsync(world, world.Category);

        var before = await _fixture.ScalarAsync<long>(
            world.Database, $"select count(*) from public.manual_test_case where org_id = '{world.Org}'");

        foreach (var empty in new[] { 0, 101 })
        {
            var refused = await world.Client.SendAsync(
                HttpMethod.Post, world.Cases, world.As,
                new
                {
                    checklist_item_id = item,
                    title = "Case",
                    steps = Enumerable.Range(1, empty)
                        .Select(no => new { no, action = "open", expected = "shown" })
                        .ToArray(),
                    priority = "high",
                },
                NewKey());

            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"{empty} steps");
            refused.ErrorCode.ShouldBe("request_invalid", $"{empty} steps");
        }

        // The entry cap is 100 (steps ≤ 100, each action/expected ≤ 2 000): a 100-step case is accepted, a 2 001
        // character action is not, and the 64 KiB body cap is not what refuses either (AC16 is the body, the
        // steps schema is the field).
        _ = await CreateCaseAsync(world, item, count: 100);

        var longAction = await world.Client.SendAsync(
            HttpMethod.Post, world.Cases, world.As,
            new
            {
                checklist_item_id = item,
                title = "Long action",
                steps = new[] { new { no = 1, action = new string('a', 2_001), expected = "shown" } },
                priority = "high",
            },
            NewKey());

        longAction.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        longAction.ErrorCode.ShouldBe("request_invalid");
        Details(longAction).ShouldContain(detail => detail.Path != null && detail.Path.StartsWith("/steps", StringComparison.Ordinal));

        var atFieldCap = await world.Client.SendAsync(
            HttpMethod.Post, world.Cases, world.As,
            new
            {
                checklist_item_id = item,
                title = "At the action cap",
                steps = new[] { new { no = 1, action = new string('a', 2_000), expected = new string('e', 2_000) } },
                priority = "high",
            },
            NewKey());

        atFieldCap.Status.ShouldBe(HttpStatusCode.Created, atFieldCap.ErrorMessage);

        (await _fixture.ScalarAsync<long>(
            world.Database, $"select count(*) from public.manual_test_case where org_id = '{world.Org}'"))
            .ShouldBe(before + 2, "only the accepted 100-step and at-cap cases wrote a row");
    }

    /// <summary>
    /// AC22's move route: a foreign, an unknown and a soft-deleted target item are <c>422 authoring_parent_invalid</c> with
    /// the AC1 reason, a reason longer than 500 characters is <c>422 /reason</c>, moving onto the item the case already sits
    /// under writes nothing, and a successful move keeps the code and bumps the version.
    /// </summary>
    [Fact]
    public async Task CaseMove_RefusalsAndNoOp()
    {
        await using var world = await StartAsync();

        var source = await CreateItemAsync(world, world.Category, "Source");
        var target = await CreateItemAsync(world, world.Category, "Target");
        var manualCase = await CreateCaseAsync(world, source);
        var detail = await world.Client.SendAsync(HttpMethod.Get, world.Case(manualCase), world.As);
        var code = detail.Text("code").ShouldNotBeNull();

        // The case already sits under its item: the no-op of AC5 returns the unchanged version and writes nothing.
        var noop = await world.Client.SendAsync(
            HttpMethod.Post, $"{world.Case(manualCase)}/item", world.As, new { checklist_item_id = source }, NewKey(), "\"1\"");
        noop.Status.ShouldBe(HttpStatusCode.OK, noop.ErrorMessage);
        noop.Body.GetProperty("version").GetInt32().ShouldBe(1);

        var longReason = await world.Client.SendAsync(
            HttpMethod.Post, $"{world.Case(manualCase)}/item", world.As,
            new { checklist_item_id = target, reason = new string('r', 501) }, NewKey(), "\"1\"");
        longReason.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(longReason).ShouldContain(detail => detail.Path == "/reason");

        var unknown = await world.Client.SendAsync(
            HttpMethod.Post, $"{world.Case(manualCase)}/item", world.As,
            new { checklist_item_id = Guid.CreateVersion7() }, NewKey(), "\"1\"");
        unknown.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        unknown.ErrorCode.ShouldBe("authoring_parent_invalid");
        Details(unknown).ShouldContain(detail => detail.Reason == "parent_not_found");

        // A sibling project's item is the same parent_not_found: a foreign id and an unknown one are indistinguishable.
        var siblingProject = await CreateProjectAsync(world.Client, world.User, world.Org, "sibling");
        var siblingNodes = $"/v1/projects/{siblingProject}/authoring/nodes";
        var siblingModule = (await world.Client.SendAsync(
            HttpMethod.Post, siblingNodes, world.As, new { kind = "module", title = "Other root" }, NewKey())).Id("node_id");
        var siblingFeature = (await world.Client.SendAsync(
            HttpMethod.Post, siblingNodes, world.As, new { parent_id = siblingModule, kind = "feature", title = "F" }, NewKey())).Id("node_id");
        var siblingCategory = (await world.Client.SendAsync(
            HttpMethod.Post, siblingNodes, world.As, new { parent_id = siblingFeature, kind = "category", title = "C" }, NewKey())).Id("node_id");
        var siblingItem = (await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/projects/{siblingProject}/authoring/checklist-items", world.As,
            new { node_id = siblingCategory, title = "Elsewhere", priority = "low" }, NewKey())).Id("item_id");

        var foreign = await world.Client.SendAsync(
            HttpMethod.Post, $"{world.Case(manualCase)}/item", world.As,
            new { checklist_item_id = siblingItem }, NewKey(), "\"1\"");
        foreign.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        foreign.ErrorCode.ShouldBe("authoring_parent_invalid");
        Details(foreign).ShouldContain(detail => detail.Reason == "parent_not_found");

        // The delete target guard: a soft-deleted item answers parent_deleted (AC22/§6).
        var doomed = await CreateItemAsync(world, world.Category, "Doomed");
        (await world.Client.SendAsync(HttpMethod.Delete, world.Item(doomed), world.As, null, NewKey(), "\"1\""))
            .Status.ShouldBe(HttpStatusCode.NoContent);

        var deletedTarget = await world.Client.SendAsync(
            HttpMethod.Post, $"{world.Case(manualCase)}/item", world.As, new { checklist_item_id = doomed }, NewKey(), "\"1\"");
        deletedTarget.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        deletedTarget.ErrorCode.ShouldBe("authoring_parent_invalid");
        Details(deletedTarget).ShouldContain(detail => detail.Reason == "parent_deleted");

        // A successful move keeps the code and bumps the version by one.
        var moved = await world.Client.SendAsync(
            HttpMethod.Post, $"{world.Case(manualCase)}/item", world.As,
            new { checklist_item_id = target, reason = "reorganise" }, NewKey(), "\"1\"");
        moved.Status.ShouldBe(HttpStatusCode.OK, moved.ErrorMessage);
        moved.Body.GetProperty("version").GetInt32().ShouldBe(2);
        moved.Body.GetProperty("checklist_item_id").GetGuid().ShouldBe(target);
        (await world.Client.SendAsync(HttpMethod.Get, world.Case(manualCase), world.As)).Text("code").ShouldBe(code);
    }

    /// <summary>
    /// The list routes' query bounds: <c>q</c> is a literal, case-insensitive substring (a <c>%</c>, a <c>_</c> and a
    /// <c>\</c> match nothing rather than everything) and is capped at 200 code points, <c>limit</c> is 1..200, and a
    /// cursor this route never issued is <c>422</c>.
    /// </summary>
    [Fact]
    public async Task ListQueryBounds_AreRefusedAndQIsLiteral()
    {
        await using var world = await StartAsync();
        var item = await CreateItemAsync(world, world.Category);
        _ = await CreateCaseAsync(world, item);

        var percent = await world.Client.SendAsync(HttpMethod.Get, $"{world.Cases}?q=%25", world.As);
        percent.Status.ShouldBe(HttpStatusCode.OK, percent.ErrorMessage);
        percent.Body.GetProperty("items").GetArrayLength().ShouldBe(0, "% is a literal character, not a wildcard");

        foreach (var hostile in new[] { "_", "\\" })
        {
            var answer = await world.Client.SendAsync(
                HttpMethod.Get, $"{world.Cases}?q={Uri.EscapeDataString(hostile)}", world.As);
            answer.Status.ShouldBe(HttpStatusCode.OK, hostile);
            answer.Body.GetProperty("items").GetArrayLength().ShouldBe(0, hostile);
        }

        var fullLength = await world.Client.SendAsync(
            HttpMethod.Get, $"{world.Cases}?q={new string('q', 200)}", world.As);
        fullLength.Status.ShouldBe(HttpStatusCode.OK, "200 code points is inside the q bound");

        var tooLong = await world.Client.SendAsync(
            HttpMethod.Get, $"{world.Cases}?q={new string('q', 201)}", world.As);
        tooLong.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(tooLong).ShouldContain(detail => detail.Path == "/q");

        foreach (var limit in new[] { "0", "201", "abc" })
        {
            var refused = await world.Client.SendAsync(HttpMethod.Get, $"{world.Cases}?limit={limit}", world.As);
            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, limit);
            Details(refused).ShouldContain(detail => detail.Path == "/limit", limit);
        }

        (await world.Client.SendAsync(HttpMethod.Get, $"{world.Cases}?limit=200", world.As)).Status.ShouldBe(HttpStatusCode.OK);

        foreach (var list in new[] { world.Cases, world.Items })
        {
            var cursor = await world.Client.SendAsync(HttpMethod.Get, $"{list}?cursor=tampered-cursor", world.As);
            cursor.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, list);
            Details(cursor).ShouldContain(detail => detail.Path == "/cursor", list);
        }

        // The node filter is a uuid or it is refused — never a silently unfiltered page.
        var badNode = await world.Client.SendAsync(HttpMethod.Get, $"{world.Items}?node_id=not-a-uuid", world.As);
        badNode.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(badNode).ShouldContain(detail => detail.Path == "/node_id");
    }

    /// <summary>
    /// AC18/AC10 at the case routes: a replay of one <c>Idempotency-Key</c> with a different body is
    /// <c>409 idempotency_conflict</c>, a key longer than 128 characters is <c>400 request_invalid</c> on
    /// <c>/Idempotency-Key</c>, and a soft-deleted case answers 404 on the detail, the patch, the state move, the item move
    /// and a second delete.
    /// </summary>
    [Fact]
    public async Task IdempotencyConflict_And404OnEveryVerbAfterDelete()
    {
        await using var world = await StartAsync();
        var item = await CreateItemAsync(world, world.Category);
        var manualCase = await CreateCaseAsync(world, item);

        var key = NewKey();
        var body = new
        {
            checklist_item_id = item,
            title = "Replay",
            steps = new[] { new { no = 1, action = "open", expected = "shown" } },
            priority = "high",
        };

        var first = await world.Client.SendAsync(HttpMethod.Post, world.Cases, world.As, body, key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        var replay = await world.Client.SendAsync(HttpMethod.Post, world.Cases, world.As, body, key);
        replay.Status.ShouldBe(first.Status);
        replay.Id("case_id").ShouldBe(first.Id("case_id"), "a replay returns the stored answer");

        var different = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Cases,
            world.As,
            new
            {
                checklist_item_id = item,
                title = "Different",
                steps = new[] { new { no = 1, action = "open", expected = "shown" } },
                priority = "high",
            },
            key);
        different.Status.ShouldBe(HttpStatusCode.Conflict);
        different.ErrorCode.ShouldBe("idempotency_conflict");

        var longKey = await world.Client.SendAsync(HttpMethod.Post, world.Cases, world.As, body, new string('k', 129));
        longKey.Status.ShouldBe(HttpStatusCode.BadRequest);
        longKey.ErrorCode.ShouldBe("request_invalid");
        Details(longKey).ShouldContain(detail => detail.Path == "/Idempotency-Key");

        (await world.Client.SendAsync(HttpMethod.Delete, world.Case(manualCase), world.As, null, NewKey(), "\"1\""))
            .Status.ShouldBe(HttpStatusCode.NoContent);

        foreach (var second in new[]
        {
            await world.Client.SendAsync(HttpMethod.Get, world.Case(manualCase), world.As),
            await world.Client.SendAsync(HttpMethod.Patch, world.Case(manualCase), world.As, new { title = "x" }, NewKey(), "\"2\""),
            await world.Client.SendAsync(HttpMethod.Post, $"{world.Case(manualCase)}/state", world.As, new { state = "approved" }, NewKey(), "\"2\""),
            await world.Client.SendAsync(HttpMethod.Post, $"{world.Case(manualCase)}/item", world.As, new { checklist_item_id = item }, NewKey(), "\"2\""),
            await world.Client.SendAsync(HttpMethod.Delete, world.Case(manualCase), world.As, null, NewKey(), "\"2\""),
        })
        {
            second.Status.ShouldBe(HttpStatusCode.NotFound);
            second.ErrorCode.ShouldBe("not_found");
        }

        // AC21: a retire without a reason is the reason field's 422, and a retired case refuses edits (§6).
        var fresh = await CreateCaseAsync(world, item);
        var noReason = await world.Client.SendAsync(
            HttpMethod.Post, $"{world.Case(fresh)}/state", world.As, new { state = "retired" }, NewKey(), "\"1\"");
        noReason.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(noReason).ShouldContain(detail => detail.Path == "/reason" && detail.Reason == "required");

        (await world.Client.SendAsync(
            HttpMethod.Post, world.Case(fresh) + "/state", world.As, new { state = "retired", reason = "duplicate" }, NewKey(), "\"1\""))
            .Status.ShouldBe(HttpStatusCode.OK);

        var revived = await world.Client.SendAsync(
            HttpMethod.Post, world.Case(fresh) + "/state", world.As, new { state = "draft" }, NewKey(), "\"2\"");
        revived.Status.ShouldBe(HttpStatusCode.Conflict);
        revived.ErrorCode.ShouldBe("invalid_transition");
    }
}
