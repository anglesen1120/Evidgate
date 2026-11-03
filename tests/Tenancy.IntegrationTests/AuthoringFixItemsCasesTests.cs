using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The three item and manual case findings of the 13.1.1-T2 review, each proven through the real host against a real
/// PostgreSQL: a soft-deleted project answers 404 on <b>every</b> item and case route (AC10), a create the writer would
/// refuse burns no case code (AC4), and the row locks a command takes before it reaches the writer are bounded by the
/// writer's own 5 s <c>lock_timeout</c> and answer the retryable <c>503 unavailable</c> instead of hanging (§5, 00 §9.2).
/// </summary>
/// <remarks>
/// The rows the routes must not touch are seeded as the owner through the fixture (org, project, feature node, and — in
/// the code-allocation case — items in states no route can produce); everything the routes do produce starts from their
/// own endpoints, so the version chain and the code counter are the ones the product writes.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AuthoringFixItemsCasesTests
{
    private readonly PostgresTenancyFixture _fixture;

    /// <summary>Wires the fixture.</summary>
    /// <param name="fixture">The shared PostgreSQL fixture.</param>
    public AuthoringFixItemsCasesTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        Guid Category) : IAsyncDisposable
    {
        /// <summary>The caller's headers: the org of the project plus a user id the granting host accepts.</summary>
        public Action<HttpRequestHeaders> As => TenancyApiFactory.Caller(org: Org, user: User);

        /// <summary>Route 7-10's collection.</summary>
        public string Items => string.Create(CultureInfo.InvariantCulture, $"/v1/projects/{Project:D}/authoring/checklist-items");

        /// <summary>Route 9-10's member.</summary>
        public string Item(Guid item) => Items + "/" + item.ToString("D", CultureInfo.InvariantCulture);

        /// <summary>Route 12-17 and 19's collection.</summary>
        public string Cases => string.Create(CultureInfo.InvariantCulture, $"/v1/projects/{Project:D}/authoring/manual-cases");

        /// <summary>Route 14-17 and 19's member.</summary>
        public string Case(Guid manualCase) => Cases + "/" + manualCase.ToString("D", CultureInfo.InvariantCulture);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>
    /// AC10 / security S-5: a soft-deleted project hides its items and its cases on <b>every</b> route, reads included —
    /// the rows are still stored and the project id still matches, so only the liveness probe stands between a deleted
    /// project and a readable, editable tree. Nothing the eleven routes attempt may be written.
    /// </summary>
    [Fact]
    public async Task SoftDeletedProject_Answers404OnEveryItemAndCaseRoute()
    {
        await using var world = await StartAsync();
        var item = await CreateItemAsync(world, "Checkout");
        var other = await CreateItemAsync(world, "Refunds");
        var manualCase = await CreateCaseAsync(world, item);

        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.project set status = 'deleted' where id = '{world.Project:D}'");

        var routes = new (HttpMethod Method, string Path, object? Body, string? IfMatch)[]
        {
            (HttpMethod.Get, world.Items, null, null),
            (HttpMethod.Post, world.Items, new { node_id = world.Category, title = "After the delete", priority = "high" }, null),
            (HttpMethod.Patch, world.Item(item), new { title = "After the delete" }, "\"1\""),
            (HttpMethod.Delete, world.Item(item), null, "\"1\""),
            (HttpMethod.Get, world.Cases, null, null),
            (HttpMethod.Post, world.Cases, CaseBody(item, "After the delete"), null),
            (HttpMethod.Get, world.Case(manualCase), null, null),
            (HttpMethod.Patch, world.Case(manualCase), new { title = "After the delete" }, "\"1\""),
            (HttpMethod.Post, world.Case(manualCase) + "/state", new { state = "approved" }, "\"1\""),
            (HttpMethod.Post, world.Case(manualCase) + "/item", new { checklist_item_id = other }, "\"1\""),
            (HttpMethod.Delete, world.Case(manualCase), null, "\"1\""),
        };

        foreach (var (method, path, body, ifMatch) in routes)
        {
            var answer = await world.Client.SendAsync(method, path, world.As, body, NewKey(), ifMatch);

            answer.Status.ShouldBe(HttpStatusCode.NotFound, string.Create(CultureInfo.InvariantCulture, $"{method} {path}"));
            answer.ErrorCode.ShouldBe("not_found", string.Create(CultureInfo.InvariantCulture, $"{method} {path}"));
        }

        // Not one of them wrote: the item kept its title and version, and the case kept its item, state and version.
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select title from public.checklist_item where id = '{item:D}'")).ShouldBe("Checkout");

        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select version from public.checklist_item where id = '{item:D}'")).ShouldBe(1);

        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select state from public.manual_test_case where id = '{manualCase:D}'")).ShouldBe("draft");

        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select version from public.manual_test_case where id = '{manualCase:D}'")).ShouldBe(1);

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.manual_test_case where id = '{manualCase:D}' and checklist_item_id = '{other:D}'"))
            .ShouldBe(0);
    }

    /// <summary>
    /// AC4 / performance F4: a create whose target item the writer would refuse is refused <em>before</em> the code
    /// allocation, so a bogus <c>checklist_item_id</c> spends no number and opens no second pooled connection for a
    /// counter bump nobody will keep — the first real case of the project is still <c>-001</c>.
    /// </summary>
    /// <remarks>
    /// The refusals are the writer's own, so the wire contract does not change with the earlier probe: an unknown project
    /// member and another project's item are both <c>422 authoring_parent_invalid</c> / <c>parent_not_found</c> at
    /// <c>/checklist_item_id</c>, a soft-deleted item is <c>parent_deleted</c>, and a retired item is the
    /// <c>409 invalid_transition</c> the writer answers for every write under it.
    /// </remarks>
    [Fact]
    public async Task RefusedCreate_BurnsNoCode_AndTheFirstRealCaseIs001()
    {
        await using var world = await StartAsync();
        var item = await CreateItemAsync(world, "Checkout");
        var (retired, deleted, foreign) = await SeedRefusedTargetsAsync(world);

        var refused = new (Guid Target, string Because, HttpStatusCode Status, string Code, string? Reason)[]
        {
            (Guid.CreateVersion7(), "an item of no project",
                HttpStatusCode.UnprocessableEntity, "authoring_parent_invalid", "parent_not_found"),
            (foreign, "another project's item",
                HttpStatusCode.UnprocessableEntity, "authoring_parent_invalid", "parent_not_found"),
            (deleted, "a soft-deleted item",
                HttpStatusCode.UnprocessableEntity, "authoring_parent_invalid", "parent_deleted"),
            (retired, "a retired item", HttpStatusCode.Conflict, "invalid_transition", null),
        };

        foreach (var (target, because, status, code, reason) in refused)
        {
            var answer = await world.Client.SendAsync(
                HttpMethod.Post, world.Cases, world.As, CaseBody(target, "Burns nothing"), NewKey());

            answer.Status.ShouldBe(status, because);
            answer.ErrorCode.ShouldBe(code, because);

            if (reason is not null)
            {
                Details(answer).ShouldContain(
                    detail => detail.Path == "/checklist_item_id" && detail.Reason == reason,
                    because);
            }
        }

        // None of the four refusals wrote a row.
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.manual_test_case where org_id = '{world.Org:D}'")).ShouldBe(0);

        // And none spent a number: the first real case is the project's first code.
        var accepted = await world.Client.SendAsync(
            HttpMethod.Post, world.Cases, world.As, CaseBody(item, "The first real case"), NewKey());

        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
        accepted.Text("code")!.EndsWith("-001", StringComparison.Ordinal)
            .ShouldBeTrue($"the code {accepted.Text("code")} is the project's first allocation");
    }

    /// <summary>
    /// Performance F1 / security S-3: the <c>If-Match</c> row lock a PATCH takes <em>before</em> the writer sets its own
    /// <c>lock_timeout</c> is bounded too. With the item's row held <c>for update</c> on a second connection the route
    /// answers the retryable <c>503 unavailable</c> and writes nothing — it neither hangs nor surfaces a 500.
    /// </summary>
    [Fact]
    public async Task HeldItemRow_MakesAPatchAnswer503_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var item = await CreateItemAsync(world, "Checkout");

        await using var holder = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await holder.ScalarAsync<int>(
            "select 1 from public.checklist_item where id = @item_id for update",
            ("item_id", item))).ShouldBe(1);

        var answer = await world.Client
            .SendAsync(HttpMethod.Patch, world.Item(item), world.As, new { title = "Held" }, NewKey(), "\"1\"")
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        answer.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        answer.ErrorCode.ShouldBe("unavailable");

        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select title from public.checklist_item where id = '{item:D}'")).ShouldBe("Checkout");

        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select version from public.checklist_item where id = '{item:D}'")).ShouldBe(1);
    }

    /// <summary>
    /// Performance F1 / security S-3, the move half: the target item's lock is the first statement of route 19 and runs
    /// before the writer's own bound, so it carries the same 5 s <c>lock_timeout</c> and the same retryable 503.
    /// </summary>
    [Fact]
    public async Task HeldTargetItemRow_MakesAMoveAnswer503_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var item = await CreateItemAsync(world, "Checkout");
        var target = await CreateItemAsync(world, "Refunds");
        var manualCase = await CreateCaseAsync(world, item);

        await using var holder = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await holder.ScalarAsync<int>(
            "select 1 from public.checklist_item where id = @item_id for update",
            ("item_id", target))).ShouldBe(1);

        var answer = await world.Client
            .SendAsync(
                HttpMethod.Post,
                world.Case(manualCase) + "/item",
                world.As,
                new { checklist_item_id = target },
                NewKey(),
                "\"1\"")
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        answer.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        answer.ErrorCode.ShouldBe("unavailable");

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.manual_test_case where id = '{manualCase:D}' and checklist_item_id = '{target:D}'"))
            .ShouldBe(0);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The <c>error.details</c> rows of a refusal as <c>(path, reason)</c> pairs.</summary>
    private static IReadOnlyList<(string? Path, string? Reason)> Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => (
                detail.TryGetProperty("path", out var path) ? path.GetString() : null,
                detail.TryGetProperty("reason", out var reason) ? reason.GetString() : null))];

    /// <summary>A valid route 13 body with one step and the §6 initial state's priority.</summary>
    private static JsonElement CaseBody(Guid item, string title) => Element(
        """{"checklist_item_id":"__ITEM__","title":"__TITLE__","steps":[{"no":1,"action":"Open the cart","expected":"The cart is shown"}],"priority":"high"}"""
            .Replace("__ITEM__", item.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__TITLE__", title, StringComparison.Ordinal));

    /// <summary>A detached element: the document is disposed, so the element outlives this helper.</summary>
    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    /// <summary>One org and one project with a single <c>category</c> node, seeded as the owner.</summary>
    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var category = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.organization (id, name, slug) values ('{org:D}', 'fix org', 'fix-{org:N}');
            insert into public.project (id, org_id, name, slug) values ('{project:D}', '{org:D}', 'shop', 'shop');
            insert into public.feature_node (id, org_id, project_id, kind, title, position)
            values ('{category:D}', '{org:D}', '{project:D}', 'category', 'Checkout', 0);
            """);

        return new World(api, client, database, user, org, project, category);
    }

    /// <summary>An item through route 8, so its version chain and position are the product's own.</summary>
    private static async Task<Guid> CreateItemAsync(World world, string title)
    {
        var created = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Items,
            world.As,
            new { node_id = world.Category, title, priority = "high" },
            NewKey());

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id("item_id");
    }

    /// <summary>A case through route 13: the allocation and the writer's own version row.</summary>
    private static async Task<Guid> CreateCaseAsync(World world, Guid item)
    {
        var created = await world.Client.SendAsync(
            HttpMethod.Post, world.Cases, world.As, CaseBody(item, "Pay with a card"), NewKey());

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id("case_id");
    }

    /// <summary>
    /// The three items no route can produce, seeded as the owner: a retired one, a soft-deleted one, and one of a
    /// <b>sibling project</b> of the same organization (the AC10 case a project-scoped read must not see).
    /// </summary>
    /// <returns>The retired, deleted and foreign item ids.</returns>
    private async Task<(Guid Retired, Guid Deleted, Guid Foreign)> SeedRefusedTargetsAsync(World world)
    {
        var retired = Guid.CreateVersion7();
        var deleted = Guid.CreateVersion7();
        var foreignProject = Guid.CreateVersion7();
        var foreignNode = Guid.CreateVersion7();
        var foreign = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.checklist_item (id, org_id, project_id, feature_node_id, title, priority, state, source, position)
            values ('{retired:D}', '{world.Org:D}', '{world.Project:D}', '{world.Category:D}', 'Retired', 'high', 'retired', 'manual', 10),
                   ('{deleted:D}', '{world.Org:D}', '{world.Project:D}', '{world.Category:D}', 'Deleted', 'high', 'active', 'manual', 11);
            update public.checklist_item set deleted_at = now(), version = version + 1 where id = '{deleted:D}';
            insert into public.project (id, org_id, name, slug) values ('{foreignProject:D}', '{world.Org:D}', 'other', 'other');
            insert into public.feature_node (id, org_id, project_id, kind, title, position)
            values ('{foreignNode:D}', '{world.Org:D}', '{foreignProject:D}', 'category', 'Elsewhere', 0);
            insert into public.checklist_item (id, org_id, project_id, feature_node_id, title, priority, state, source, position)
            values ('{foreign:D}', '{world.Org:D}', '{foreignProject:D}', '{foreignNode:D}', 'Elsewhere', 'high', 'active', 'manual', 0);
            """);

        return (retired, deleted, foreign);
    }
}
