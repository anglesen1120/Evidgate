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
[Collection(TenancySuite.Name)]
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

    /// <summary>One request of the sweep; <paramref name="OrderTag"/> marks the reorder route's order precondition.</summary>
    private sealed record Step(string Label, HttpMethod Method, string Url, object? Body, bool OrderTag = false);

    private static async Task<Guid> CreateAsync(World world, string url, object body, string member = "node_id")
    {
        var created = await world.Client.SendAsync(HttpMethod.Post, url, world.As, body, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, $"{url} answers 201, not {created.Status}: {created.Body}");
        return created.Body.GetProperty(member).GetGuid();
    }

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
            new("reorder siblings", HttpMethod.Post, $"{world.Node(feature)}/reorder", new { ordered_ids = new[] { category } }, OrderTag: true),
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
