using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Routes 7-10 of 13.1.1 §5 through the real host and a real PostgreSQL: the checklist item create, list, edit and
/// soft delete, their 422 shapes (AC3's <c>/priority</c>, AC20's <c>/reason</c>), the §6 state matrix (AC20), the
/// <c>If-Match</c> rule (AC19), the AC5 no-op, the AC7 non-empty guard, the AC18 idempotency key and the permission
/// gate (AC17).
/// </summary>
/// <remarks>
/// The node chain is seeded with SQL because node <em>writes</em> are the node routes' subject (13.1.1 §5 routes 1-6),
/// while the items are created through the route under test: a checklist item's own version arithmetic (the writer's
/// <c>version_no</c> chain, AC5) only holds for rows the writer created, so seeding an item with SQL would test a state
/// the product cannot produce.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AuthoringChecklistTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AuthoringChecklistTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        Guid Module,
        Guid Feature,
        Guid Category) : IAsyncDisposable
    {
        /// <summary>The founder, whose <c>tenant_admin</c> membership holds <c>authoring:write</c> (AC17).</summary>
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        /// <summary>
        /// An authenticated service credential of the same org that holds no <c>authoring:write</c>, so the gate answers
        /// 403. The stand-in authenticator builds a service principal only from a permissions claim, so the credential
        /// claims a permission of another module: the evaluator refuses the write, the request is authorized, not anonymous.
        /// </summary>
        public Action<HttpRequestHeaders> WithoutPermission =>
            TenancyApiFactory.Resolved(serviceOrg: Org, serviceProject: Project, servicePermissions: ["authoring:read"]);

        public string Items => $"/v1/projects/{Project}/authoring/checklist-items";

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    [Fact]
    public async Task Create_ValidatesPriorityAndTheParentKind()
    {
        await using var world = await StartAsync();

        // AC3: an item hangs under a category or a sub-category, never under a feature.
        var underFeature = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Items,
            world.Admin,
            new { node_id = world.Feature, title = "Pay online", priority = "high" },
            NewKey());

        underFeature.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        underFeature.ErrorCode.ShouldBe("authoring_parent_invalid");
        Reasons(underFeature).ShouldContain("parent_kind_invalid");

        // AC3: the priority vocabulary is checked at the edge, naming the member the caller sent.
        var badPriority = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Items,
            world.Admin,
            new { node_id = world.Category, title = "Pay online", priority = "urgent" },
            NewKey());

        badPriority.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(badPriority).ShouldContain(detail => detail.Path == "/priority" && detail.Reason == "value_invalid");

        var created = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Items,
            world.Admin,
            new { node_id = world.Category, title = "Pay online", priority = "high" },
            NewKey());

        created.Status.ShouldBe(HttpStatusCode.Created);
        created.Text("state").ShouldBe("active");
        created.Body.GetProperty("version").GetInt32().ShouldBe(1);
        created.ETag.ShouldBe("\"1\"");

        var page = await world.Client.SendAsync(HttpMethod.Get, world.Items, world.Admin);
        page.Status.ShouldBe(HttpStatusCode.OK);
        var items = page.Body.GetProperty("items");
        items.GetArrayLength().ShouldBe(1);
        items[0].GetProperty("item_id").GetGuid().ShouldBe(created.Id("item_id"));
        items[0].GetProperty("priority").GetString().ShouldBe("high");
        items[0].GetProperty("source").GetString().ShouldBe("manual");
    }

    /// <summary>AC20: every pair of the §6 state matrix, driven through one route.</summary>
    /// <param name="scenario">Which pair the case exercises.</param>
    [Theory]
    [InlineData("active to active is a no-op")]
    [InlineData("retire needs a reason")]
    [InlineData("retire with a reason is accepted")]
    [InlineData("a retired item never comes back")]
    [InlineData("a retired item is never edited")]
    public async Task StateMatrix_AnswersEveryTransition(string scenario)
    {
        await using var world = await StartAsync();
        var item = await CreateItemAsync(world, "Pay online");

        var answer = scenario switch
        {
            "active to active is a no-op" => await PatchAsync(world, item, 1, new { state = "active" }),

            "retire needs a reason" => await PatchAsync(world, item, 1, new { state = "retired" }),

            "retire with a reason is accepted" => await RetireAsync(world, item),

            "a retired item never comes back" => await ReactivateAsync(world, item),

            _ => await EditRetiredAsync(world, item),
        };

        switch (scenario)
        {
            case "active to active is a no-op":
                answer.Status.ShouldBe(HttpStatusCode.OK);
                answer.Body.GetProperty("changed").GetBoolean().ShouldBeFalse();
                answer.Body.GetProperty("version").GetInt32().ShouldBe(1);
                (await VersionCountAsync(world, item)).ShouldBe(1);
                break;

            case "retire needs a reason":
                answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
                Details(answer).ShouldContain(detail => detail.Path == "/reason" && detail.Reason == "required");
                (await VersionCountAsync(world, item)).ShouldBe(1);
                break;

            default:
                // The accepted retire is asserted by its helper; the two refusals are the §6 matrix's other half.
                if (scenario is "a retired item never comes back" or "a retired item is never edited")
                {
                    answer.Status.ShouldBe(HttpStatusCode.Conflict);
                    answer.ErrorCode.ShouldBe("invalid_transition");
                    (await VersionCountAsync(world, item)).ShouldBe(2);
                }
                else
                {
                    answer.Status.ShouldBe(HttpStatusCode.OK);
                    answer.Body.GetProperty("changed").GetBoolean().ShouldBeTrue();
                    answer.Body.GetProperty("version").GetInt32().ShouldBe(2);
                    (await VersionCountAsync(world, item)).ShouldBe(2);
                }

                break;
        }
    }

    [Fact]
    public async Task IfMatch_IsRequiredAndAStaleOneIs412WithTheCurrentVersion()
    {
        await using var world = await StartAsync();
        var item = await CreateItemAsync(world, "Pay online");

        var absent = await world.Client.SendAsync(
            HttpMethod.Patch,
            $"{world.Items}/{item}",
            world.Admin,
            new { title = "Pay by card" },
            NewKey());

        absent.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(absent).ShouldContain(detail => detail.Path == "/If-Match" && detail.Reason == "required");

        var first = await PatchAsync(world, item, 1, new { title = "Pay by card" });
        first.Status.ShouldBe(HttpStatusCode.OK);
        first.Body.GetProperty("version").GetInt32().ShouldBe(2);

        var stale = await PatchAsync(world, item, 1, new { title = "Pay by transfer" });

        stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        stale.ErrorCode.ShouldBe("precondition_failed");
        // Hand-over (c), OI-13-130: every authoring 412 names the current version under the rooted pointer /version.
        Details(stale).ShouldContain(detail => detail.Path == "/version" && detail.Reason == "2");

        // AC19: nothing changed — the refused edit wrote no version row.
        (await VersionCountAsync(world, item)).ShouldBe(2);
    }

    [Fact]
    public async Task Mutations_RequireAnIdempotencyKeyAndTheWritePermission()
    {
        await using var world = await StartAsync();

        var noKey = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Items,
            world.Admin,
            new { node_id = world.Category, title = "Pay online", priority = "high" });

        noKey.Status.ShouldBe(HttpStatusCode.BadRequest);
        Details(noKey).ShouldContain(detail => detail.Path == "/Idempotency-Key" && detail.Reason == "required");

        var noPermission = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Items,
            world.WithoutPermission,
            new { node_id = world.Category, title = "Pay online", priority = "high" },
            NewKey());

        noPermission.Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NodeId_IsNotAPatchMember()
    {
        await using var world = await StartAsync();
        var item = await CreateItemAsync(world, "Pay online");

        var answer = await PatchAsync(world, item, 1, new { node_id = world.Category, title = "Pay by card" });

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(answer).ShouldContain(detail => detail.Path == "/node_id" && detail.Reason == "unknown_field");
        (await VersionCountAsync(world, item)).ShouldBe(1);
    }

    [Fact]
    public async Task AnotherOrgsItem_IsNotFound()
    {
        await using var world = await StartAsync();
        var item = await CreateItemAsync(world, "Pay online");

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "other");
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "checkout");

        var answer = await world.Client.SendAsync(
            HttpMethod.Patch,
            $"/v1/projects/{otherProject}/authoring/checklist-items/{item}",
            TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg),
            new { title = "Pay by card" },
            NewKey(),
            "\"1\"");

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        (await VersionCountAsync(world, item)).ShouldBe(1);
    }

    [Fact]
    public async Task Delete_SoftDeletesALeaf_AndRefusesAnItemThatStillHoldsCases()
    {
        await using var world = await StartAsync();
        var item = await CreateItemAsync(world, "Pay online");
        var caseId = Guid.CreateVersion7();

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            await scope.ExecuteAsync(
                """
                insert into public.manual_test_case
                    (id, org_id, project_id, checklist_item_id, code, title, steps, priority, state, source)
                values (@id, @org_id, @project_id, @item_id, 'SHOP-001', 'Pay by card',
                        '{"steps":[{"no":1,"action":"Pay","expected":"Paid"}]}'::jsonb, 'high', 'draft', 'manual')
                """,
                ("id", caseId), ("org_id", world.Org), ("project_id", world.Project), ("item_id", item));
            await scope.CommitAsync();
        }

        var refused = await world.Client.SendAsync(
            HttpMethod.Delete, $"{world.Items}/{item}", world.Admin, null, NewKey(), "\"1\"");

        refused.Status.ShouldBe(HttpStatusCode.Conflict);
        refused.ErrorCode.ShouldBe("authoring_not_empty");
        Reasons(refused).ShouldContain("item_has_cases");

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            await scope.ExecuteAsync(
                "update public.manual_test_case set deleted_at = now() where id = @id",
                ("id", caseId));
            await scope.CommitAsync();
        }

        var deleted = await world.Client.SendAsync(
            HttpMethod.Delete, $"{world.Items}/{item}", world.Admin, null, NewKey(), "\"1\"");

        deleted.Status.ShouldBe(HttpStatusCode.NoContent);

        var page = await world.Client.SendAsync(HttpMethod.Get, world.Items, world.Admin);
        page.Body.GetProperty("items").GetArrayLength().ShouldBe(0);

        var gone = await PatchAsync(world, item, 2, new { title = "Pay by transfer" });
        gone.Status.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// AC10 / 00 §9.3: a project the tenant cannot see answers 404 on <b>every</b> checklist route. The list is a 404
    /// rather than an empty page (an empty page would confirm the project exists), and a create is the project's 404
    /// rather than the writer's 422 <c>parent_not_found</c> for the seeded node.
    /// </summary>
    [Fact]
    public async Task UnknownProject_IsNotFoundOnEveryChecklistRoute()
    {
        await using var world = await StartAsync();
        var stranger = $"/v1/projects/{Guid.CreateVersion7()}/authoring/checklist-items";

        var list = await world.Client.SendAsync(HttpMethod.Get, stranger, world.Admin);
        list.Status.ShouldBe(HttpStatusCode.NotFound);
        list.ErrorCode.ShouldBe("not_found");

        var create = await world.Client.SendAsync(
            HttpMethod.Post,
            stranger,
            world.Admin,
            new { node_id = world.Category, title = "Pay online", priority = "high" },
            NewKey());
        create.Status.ShouldBe(HttpStatusCode.NotFound);
        create.ErrorCode.ShouldBe("not_found");

        // The item exists in the caller's own project: addressing it under an unknown one is absent, never found.
        var item = await CreateItemAsync(world, "Pay online");

        var patch = await world.Client.SendAsync(
            HttpMethod.Patch,
            $"{stranger}/{item}",
            world.Admin,
            new { title = "Pay by card" },
            NewKey(),
            "\"1\"");
        patch.Status.ShouldBe(HttpStatusCode.NotFound);
        patch.ErrorCode.ShouldBe("not_found");

        var delete = await world.Client.SendAsync(
            HttpMethod.Delete,
            $"{stranger}/{item}",
            world.Admin,
            idempotencyKey: NewKey(),
            ifMatch: "\"1\"");
        delete.Status.ShouldBe(HttpStatusCode.NotFound);
        delete.ErrorCode.ShouldBe("not_found");
    }

    // ------------------------------------------------------------------ helpers

    private static IReadOnlyList<(string Path, string Reason)> Details(ApiAnswer answer) =>
        answer.Body.TryGetProperty("error", out var error) && error.TryGetProperty("details", out var details)
            ? [.. details.EnumerateArray().Select(detail => (
                detail.GetProperty("path").GetString() ?? string.Empty,
                detail.GetProperty("reason").GetString() ?? string.Empty))]
            : [];

    private static IReadOnlyList<string> Reasons(ApiAnswer answer) => [.. Details(answer).Select(detail => detail.Reason)];

    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        var module = Guid.CreateVersion7();
        var feature = Guid.CreateVersion7();
        var category = Guid.CreateVersion7();

        // The node chain of AC1, seeded directly: node creation is routes 1-6' subject, and nothing in an item write
        // depends on a node's version chain (the parent read only checks kind, project and liveness).
        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await InsertNodeAsync(scope, module, org, project, null, "module", "Shop", 0);
            await InsertNodeAsync(scope, feature, org, project, module, "feature", "Checkout", 0);
            await InsertNodeAsync(scope, category, org, project, feature, "category", "Payment", 0);
            await scope.CommitAsync();
        }

        return new World(api, client, database, user, org, project, module, feature, category);
    }

    private static Task<int> InsertNodeAsync(
        TenantScope scope,
        Guid id,
        Guid org,
        Guid project,
        Guid? parent,
        string kind,
        string title,
        int position) => scope.ExecuteAsync(
        """
        insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position)
        values (@id, @org_id, @project_id, @parent_id, @kind, @title, @position)
        """,
        ("id", id), ("org_id", org), ("project_id", project), ("parent_id", (object?)parent ?? DBNull.Value),
        ("kind", kind), ("title", title), ("position", position));

    private static async Task<Guid> CreateItemAsync(World world, string title)
    {
        var created = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Items,
            world.Admin,
            new { node_id = world.Category, title, priority = "high" },
            NewKey());

        created.Status.ShouldBe(HttpStatusCode.Created);

        return created.Id("item_id");
    }

    private static Task<ApiAnswer> PatchAsync(World world, Guid item, int version, object body) =>
        world.Client.SendAsync(
            HttpMethod.Patch,
            $"{world.Items}/{item}",
            world.Admin,
            body,
            NewKey(),
            $"\"{version}\"");

    private static Task<ApiAnswer> RetireAsync(World world, Guid item) =>
        world.Client.SendAsync(
            HttpMethod.Patch,
            $"{world.Items}/{item}",
            world.Admin,
            new { state = "retired", reason = "superseded by the new checkout flow" },
            NewKey(),
            "\"1\"");

    private static async Task<ApiAnswer> ReactivateAsync(World world, Guid item)
    {
        (await RetireAsync(world, item)).Status.ShouldBe(HttpStatusCode.OK);

        return await PatchAsync(world, item, 2, new { state = "active" });
    }

    private static async Task<ApiAnswer> EditRetiredAsync(World world, Guid item)
    {
        (await RetireAsync(world, item)).Status.ShouldBe(HttpStatusCode.OK);

        return await PatchAsync(world, item, 2, new { title = "Pay by card" });
    }

    /// <summary>The number of <c>authoring_version</c> rows of one item (AC5's one row per change).</summary>
    private async Task<int> VersionCountAsync(World world, Guid item) => await _fixture.ScalarAsync<int>(
        world.Database,
        $"select count(*)::integer from public.authoring_version where entity_kind = 'checklist_item' and entity_id = '{item:D}'");
}
