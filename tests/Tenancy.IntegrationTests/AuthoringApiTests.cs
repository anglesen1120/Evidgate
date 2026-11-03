using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T2 AC19 across every authoring route rather than one at a time: each route whose §5 concurrency column says
/// <c>If-Match</c> refuses an absent tag with 422 <c>request_invalid</c> (<c>details[].path = /If-Match</c>) and a stale
/// one with 412 <c>precondition_failed</c> (the current version in <c>error.details</c>), and neither leaves a row, a
/// version or an audit entry behind.
/// </summary>
[Trait("Category", "Docker")]
public sealed class AuthoringApiTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AuthoringApiTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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

        public string Tree => $"/v1/projects/{Project}/authoring/tree";

        public string Node(Guid n) => $"{Nodes}/{n:D}";

        public string Items => $"/v1/projects/{Project}/authoring/checklist-items";

        public string Item(Guid i) => $"{Items}/{i:D}";

        public string Cases => $"/v1/projects/{Project}/authoring/manual-cases";

        public string Case(Guid m) => $"{Cases}/{m:D}";

        public string Versions => $"/v1/projects/{Project}/authoring/versions";

        public string Import => $"/v1/projects/{Project}/authoring/import";

        public string Export => $"/v1/projects/{Project}/authoring/export";

        public string Version(Guid v) => $"{Versions}/{v:D}";

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

        return new World(api, client, database, user, org, project);
    }

    /// <summary>One request of the sweep; <paramref name="OrderTag"/> marks the reorder route's order precondition.</summary>
    private sealed record Step(string Label, HttpMethod Method, string Url, object? Body, bool OrderTag = false);

    private static async Task<Guid> CreateAsync(World world, string url, object body, string member = "node_id")
    {
        var created = await world.Client.SendAsync(HttpMethod.Post, url, world.As, body, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, $"{url} answers 201, not {created.Status}: {created.Body}");
        return created.Body.GetProperty(member).GetGuid();
    }

    /// <summary>
    /// One route of the permission sweep: what it is called, the verb, the URL, a body and <c>If-Match</c> tag that pass
    /// the edge's own checks. §5 gates every write with <c>authoring:write</c>, so the verb decides which permission the
    /// route needs.
    /// </summary>
    private sealed record Route(string Label, HttpMethod Method, string Url, object? Body = null, string? IfMatch = null)
    {
        /// <summary>True for a mutating route (13.1.1 §5's <c>authoring:write</c>), false for a read.</summary>
        public bool IsWrite => Method != HttpMethod.Get;
    }

    /// <summary>
    /// 13.1.1 §5/AC10: every one of the twenty-four authoring routes asks for the permission §5 assigns it. A member
    /// holding only the <c>viewer</c> role (which carries <c>authoring:read</c>, never <c>authoring:write</c>) is refused
    /// 403 <c>forbidden</c> on the seventeen writes and served 200 on the seven reads; a member holding no role at all is
    /// refused 403 on a read too, so no route is ungated; the founder, who holds the permission, is never refused. The
    /// refused writes leave no entity row, no version row and no audit row behind.
    /// </summary>
    /// <remarks>
    /// The route delegates parse <c>If-Match</c> and member-check the body before <c>mediator.Send</c> (the
    /// <c>[Permission]</c> is the C2 authorization stage), so the sweep sends conditional requests that would be
    /// accepted if the caller held the permission: a 403 can then only be the permission, never the malformed edge.
    /// </remarks>
    [Fact]
    public async Task EveryRoute_RequiresItsPermission()
    {
        await using var world = await StartAsync();

        // The ladder, one item under the category and one case under the item (§4/AC1), so every route has a live target.
        var module = await CreateAsync(world, world.Nodes, new { kind = "module", title = "Checkout" });
        var feature = await CreateAsync(world, world.Nodes, new { parent_id = module, kind = "feature", title = "Payment" });
        var category = await CreateAsync(world, world.Nodes, new { parent_id = feature, kind = "category", title = "Cards" });
        var item = await CreateAsync(world, world.Items, new { node_id = category, title = "Item", priority = "high" }, "item_id");
        var manualCase = await CreateAsync(world, world.Cases, new
        {
            checklist_item_id = item,
            title = "Case",
            steps = new[] { new { no = 1, action = "open", expected = "shown" } },
            priority = "high",
        }, "case_id");

        // Route 23 (and 24) name one stored version: the ladder's newest row, read back through route 22 as the founder.
        var version = await FirstVersionAsync(world);

        var routes = new List<Route>
        {
            new("tree", HttpMethod.Get, world.Tree),
            new("create node", HttpMethod.Post, world.Nodes, new { parent_id = module, kind = "feature", title = "Refunds" }),
            new("patch node", HttpMethod.Patch, world.Node(feature), new { title = "Renamed feature" }, "\"1\""),
            new("reorder nodes", HttpMethod.Post, $"{world.Node(feature)}/reorder", new { ordered_ids = new[] { feature } }, "\"order:sha256:" + new string('0', 64) + "\""),
            new("delete node", HttpMethod.Delete, world.Node(category), null, "\"1\""),
            new("list items", HttpMethod.Get, world.Items),
            new("create item", HttpMethod.Post, world.Items, new { node_id = category, title = "Another item", priority = "low" }),
            new("patch item", HttpMethod.Patch, world.Item(item), new { title = "Renamed item" }, "\"1\""),
            new("delete item", HttpMethod.Delete, world.Item(item), null, "\"1\""),
            new("list cases", HttpMethod.Get, world.Cases),
            new("create case", HttpMethod.Post, world.Cases, new
            {
                checklist_item_id = item,
                title = "Another case",
                steps = new[] { new { no = 1, action = "open", expected = "shown" } },
                priority = "low",
            }),
            new("case detail", HttpMethod.Get, world.Case(manualCase)),
            new("patch case", HttpMethod.Patch, world.Case(manualCase), new { title = "Renamed case" }, "\"1\""),
            new("case state", HttpMethod.Post, $"{world.Case(manualCase)}/state", new { state = "approved" }, "\"1\""),
            new("case item move", HttpMethod.Post, $"{world.Case(manualCase)}/item", new { checklist_item_id = item }, "\"1\""),
            new("delete case", HttpMethod.Delete, world.Case(manualCase), null, "\"1\""),

            // T3's six routes: the four restores (§5 routes 6/11/18 take If-Match, route 24 only Idempotency-Key) and the
            // two version reads (22 and 23). The 403 assertion needs no tombstone: the C2 stage refuses before any state
            // is read, so a live id passes the edge's own header checks and the refusal can only be the permission.
            new("restore node", HttpMethod.Post, $"{world.Node(feature)}/restore", null, "\"1\""),
            new("restore item", HttpMethod.Post, $"{world.Item(item)}/restore", null, "\"1\""),
            new("restore case", HttpMethod.Post, $"{world.Case(manualCase)}/restore", null, "\"1\""),
            new("list versions", HttpMethod.Get, world.Versions),
            new("version detail", HttpMethod.Get, world.Version(version)),
            new("restore version", HttpMethod.Post, $"{world.Version(version)}/restore"),

            // 13.1.1-T5: route 25, the import/export pair. The import is a dry run of a valid one-row document, so the
            // founder's control request writes nothing while still proving the permission (a refused import must reach
            // the C2 stage, which is why the file has to be one the readers accept).
            new(
                "import",
                HttpMethod.Post,
                $"{world.Import}?format=json&dry_run=true",
                new
                {
                    format = "authoring-import",
                    version = 1,
                    rows = new object[]
                    {
                        new { kind = "node", key = "n1", node_kind = "module", title = "Imported module" },
                    },
                }),
            // The export is read as JSON: the table's helper parses every body as a document, and the permission, the
            // route and the projection are the same three formats' (CSV and XLSX differ only in the writer).
            new(
                "export",
                HttpMethod.Get,
                $"{world.Export}?format=json"),
        };

        routes.Count.ShouldBe(24, "13.1.1 §5 numbers twenty-four routes once 13.1.1-T5 adds route 25");
        routes.Count(route => route.IsWrite).ShouldBe(17, "§5 has seventeen write routes and seven reads");

        var viewer = await AddMemberAsync(world, "viewer");
        var stranger = await AddMemberAsync(world, roleCode: null);
        var asViewer = TenancyApiFactory.Resolved(user: viewer, orgHeader: world.Org);
        var asStranger = TenancyApiFactory.Resolved(user: stranger, orgHeader: world.Org);
        var before = await RowCountsAsync(world);

        foreach (var route in routes)
        {
            if (route.IsWrite)
            {
                // A viewer holds authoring:read only: every write is 403 forbidden, whatever the body says.
                var refused = await world.Client.SendAsync(route.Method, route.Url, asViewer, route.Body, NewKey(), route.IfMatch);

                refused.Status.ShouldBe(HttpStatusCode.Forbidden, $"{route.Label}: a viewer must not write, not {refused.Status}");
                refused.ErrorCode.ShouldBe("forbidden", route.Label);
                continue;
            }

            // A read is open to the viewer and closed to a member holding no role at all.
            var read = await world.Client.SendAsync(route.Method, route.Url, asViewer);

            read.Status.ShouldBe(HttpStatusCode.OK, $"{route.Label}: authoring:read must serve the viewer, not {read.Status}");

            var denied = await world.Client.SendAsync(route.Method, route.Url, asStranger);

            denied.Status.ShouldBe(HttpStatusCode.Forbidden, $"{route.Label}: a member with no authoring code must not read");
            denied.ErrorCode.ShouldBe("forbidden", route.Label);
        }

        (await RowCountsAsync(world)).ShouldBe(
            before,
            "no refused request may write a feature_node, checklist_item, manual_test_case, authoring_version or audit_log row");

        // The control: the founder holds authoring:write, so no route refuses the same requests on the permission.
        foreach (var route in routes)
        {
            var granted = await world.Client.SendAsync(route.Method, route.Url, world.As, route.Body, NewKey(), route.IfMatch);

            granted.Status.ShouldNotBe(HttpStatusCode.Forbidden, $"{route.Label}: the granted caller must not be refused");
            granted.Status.ShouldNotBe(HttpStatusCode.Unauthorized, route.Label);
        }
    }

    /// <summary>The newest stored version id of the world's project, read through route 22 as the founder.</summary>
    private static async Task<Guid> FirstVersionAsync(World world)
    {
        var page = await world.Client.SendAsync(HttpMethod.Get, world.Versions, world.As);
        page.Status.ShouldBe(HttpStatusCode.OK, page.ErrorMessage);

        return page.Body.GetProperty("items")[0].GetProperty("version_id").GetGuid();
    }

    /// <summary>An active member of the org, optionally holding one built-in role, granted by the founder through the API.</summary>
    private async Task<Guid> AddMemberAsync(World world, string? roleCode)
    {
        var user = Guid.CreateVersion7();
        var membership = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public."user" (id, email) values ('{user}', 'member-{user:N}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{membership}', '{world.Org}', '{user}', 'member-{user:N}@example.test', 'active')
            """);

        if (roleCode is not null)
        {
            var role = await _fixture.ScalarAsync<Guid>(
                world.Database, $"select id from public.role where code = '{roleCode}' and org_id is null");
            var granted = await world.Client.SendAsync(
                HttpMethod.Post, $"/v1/orgs/{world.Org}/memberships/{membership}/roles", world.As, new { role_id = role }, NewKey());

            granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);
        }

        return user;
    }

    /// <summary>The org's authoring row counts plus its audit rows, in a fixed order, for the "nothing was written" check.</summary>
    private async Task<IReadOnlyList<long>> RowCountsAsync(World world) =>
    [
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.feature_node where org_id = '{world.Org}'"),
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.checklist_item where org_id = '{world.Org}'"),
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.manual_test_case where org_id = '{world.Org}'"),
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.authoring_version where org_id = '{world.Org}'"),
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.audit_log where org_id = '{world.Org}'"),
    ];

    [Fact]
    public async Task IfMatch_Required_And_Stale()
    {
        await using var world = await StartAsync();

        // The ladder, then one item under the category and one case under the item (13.1.1 §4/AC1).
        var module = await CreateAsync(world, world.Nodes, new { kind = "module", title = "Checkout" });
        var feature = await CreateAsync(world, world.Nodes, new { parent_id = module, kind = "feature", title = "Payment" });
        var category = await CreateAsync(world, world.Nodes, new { parent_id = feature, kind = "category", title = "Cards" });
        var item = await CreateAsync(world, world.Items, new { node_id = category, title = "Item", priority = "high" }, "item_id");
        var manualCase = await CreateAsync(world, world.Cases, new
        {
            checklist_item_id = item,
            title = "Case",
            steps = new[] { new { no = 1, action = "open", expected = "shown" } },
            priority = "high",
        }, "case_id");

        // Every route of 13.1.1 §5 whose concurrency column says If-Match, with a body that would be accepted if the
        // precondition passed — so a 422 can only be the missing tag and a 412 only the stale one.
        var steps = new List<Step>
        {
            new("PATCH node", HttpMethod.Patch, world.Node(feature), new { title = "Renamed feature" }),
            new("DELETE node", HttpMethod.Delete, world.Node(category), null),
            new("reorder siblings", HttpMethod.Post, $"{world.Node(feature)}/reorder", new { ordered_ids = new[] { feature } }, OrderTag: true),
            new("PATCH item", HttpMethod.Patch, world.Item(item), new { title = "Renamed item" }),
            new("DELETE item", HttpMethod.Delete, world.Item(item), null),
            new("PATCH case", HttpMethod.Patch, world.Case(manualCase), new { title = "Renamed case" }),
            new("DELETE case", HttpMethod.Delete, world.Case(manualCase), null),
            new("state move", HttpMethod.Post, $"{world.Case(manualCase)}/state", new { state = "approved" }),
            new("item move", HttpMethod.Post, $"{world.Case(manualCase)}/item", new { checklist_item_id = item }),
        };

        foreach (var step in steps)
        {
            // AC19: absent is 422 request_invalid naming /If-Match, before the body is applied.
            var absent = await world.Client.SendAsync(step.Method, step.Url, world.As, step.Body, NewKey());
            absent.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"{step.Label}: an absent If-Match is 422, not {absent.Status}");
            absent.Body.GetProperty("error").GetProperty("details").EnumerateArray()
                .ShouldContain(entry => entry.GetProperty("path").GetString() == "/If-Match",
                    $"{step.Label}: the 422 names /If-Match");

            // AC19: a stale tag is 412 precondition_failed, with the current version in error.details.
            var stale = step.OrderTag
                ? "\"order:sha256:" + new string('0', 64) + "\""
                : "\"999\"";
            var refused = await world.Client.SendAsync(step.Method, step.Url, world.As, step.Body, NewKey(), stale);
            refused.Status.ShouldBe(HttpStatusCode.PreconditionFailed, $"{step.Label}: a stale If-Match is 412, not {refused.Status}");
            refused.Body.GetProperty("error").GetProperty("code").GetString().ShouldBe("precondition_failed", step.Label);
            refused.Body.GetProperty("error").GetProperty("details").GetArrayLength()
                .ShouldBeGreaterThan(0, $"{step.Label}: the current version rides in details");

            // Nothing changed: the case still has its first version, its title and its draft state, so no version row
            // and no state move was appended by either refusal.
            var after = await world.Client.SendAsync(HttpMethod.Get, world.Case(manualCase), world.As);
            after.Status.ShouldBe(HttpStatusCode.OK, $"{step.Label}: the entity survives the refusal");
            after.Body.GetProperty("case_id").GetGuid().ShouldBe(manualCase, step.Label);
            after.Body.GetProperty("title").GetString().ShouldBe("Case", $"{step.Label}: no title change was applied");
            after.Body.GetProperty("state").GetString().ShouldBe("draft", $"{step.Label}: no state move was applied");
            after.Body.GetProperty("version").GetInt32().ShouldBe(1, $"{step.Label}: no version row was appended");
        }

        // The control that proves the sweep sends the right tag: the same PATCH with the current version succeeds and
        // bumps the version, so the 412s above were the tag's fault rather than a blanket refusal.
        var accepted = await world.Client.SendAsync(steps[5].Method, steps[5].Url, world.As, steps[5].Body, NewKey(), "\"1\"");
        accepted.Status.ShouldBe(HttpStatusCode.OK);
        accepted.Body.GetProperty("changed").GetBoolean().ShouldBeTrue();
        accepted.Body.GetProperty("version").GetInt32().ShouldBe(2);
    }
}
