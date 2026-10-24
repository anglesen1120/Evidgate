using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The fixed input refusals of 13.1.1-T2's authoring surface (review findings security S-1/S-2 and S-8): U+0000 in any
/// body text or in <c>q</c> is a 422 at its pointer instead of a PostgreSQL 22021/22P05 surfacing as a 500, a tampered
/// cursor never reaches the keyset comparison of a <c>text</c> column, and every <c>details[].path</c> is a rooted
/// pointer. Each refusal is proven to write nothing: the row counts and the case-code allocation are unchanged.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class AuthoringFixInputTests
{
    private readonly PostgresTenancyFixture _fixture;

    /// <summary>Wires the fixture.</summary>
    /// <param name="fixture">The shared PostgreSQL fixture.</param>
    public AuthoringFixInputTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        Guid Category) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> As => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public string Nodes => $"/v1/projects/{Project}/authoring/nodes";

        public string Node(Guid n) => $"{Nodes}/{n:D}";

        public string Tree => $"/v1/projects/{Project}/authoring/tree";

        public string Items => $"/v1/projects/{Project}/authoring/checklist-items";

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
        var org = await CreateOrgAsync(client, user, "fix-input");
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var asFounder = TenancyApiFactory.Resolved(user: user, orgHeader: org);

        var module = (await client.SendAsync(
            HttpMethod.Post, $"/v1/projects/{project}/authoring/nodes", asFounder, new { kind = "module", title = "Checkout" }, NewKey())).Id("node_id");
        var feature = (await client.SendAsync(
            HttpMethod.Post, $"/v1/projects/{project}/authoring/nodes", asFounder,
            new { parent_id = module, kind = "feature", title = "Payment" }, NewKey())).Id("node_id");
        var category = (await client.SendAsync(
            HttpMethod.Post, $"/v1/projects/{project}/authoring/nodes", asFounder,
            new { parent_id = feature, kind = "category", title = "Cards" }, NewKey())).Id("node_id");

        return new World(api, client, database, user, org, project, category);
    }

    /// <summary>The <c>details[].path</c> and <c>details[].reason</c> pairs of an error answer.</summary>
    private static List<(string? Path, string? Reason)> Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => (
                detail.TryGetProperty("path", out var path) ? path.GetString() : null,
                detail.TryGetProperty("reason", out var reason) ? reason.GetString() : null))];

    /// <summary>Sends an already-serialised JSON body, the only way to send an escaped U+0000 verbatim.</summary>
    private static async Task<ApiAnswer> RawAsync(
        World world,
        HttpMethod method,
        string url,
        string json,
        string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, url);
        world.As(request.Headers);
        request.Headers.Add("Idempotency-Key", NewKey());
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        using var response = await world.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    /// <summary>A cursor in the shape route 12 issues (the <c>ManualCaseCursor</c> encoding) over arbitrary halves.</summary>
    private static string OpaqueCursor(string code, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(code + "\n" + id.ToString("D")))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private Task<long> CountAsync(World world, string sql) => _fixture.ScalarAsync<long>(world.Database, sql);

    private static void RefusedAt(ApiAnswer answer, string path, string reason)
    {
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        Details(answer).ShouldContain(detail => detail.Path == path && detail.Reason == reason);
    }

    /// <summary>
    /// U+0000 in a node title, a checklist item description or a member <b>name</b> is a 422 <c>invalid_text</c> at that
    /// pointer (the stable placeholder for a name), and neither a row nor a version is written.
    /// </summary>
    [Fact]
    public async Task NulInBodyText_Is422AtThePointer_AndWritesNothing()
    {
        await using var world = await StartAsync();

        var nodesBefore = await CountAsync(world, "select count(*) from public.feature_node");
        var itemsBefore = await CountAsync(world, "select count(*) from public.checklist_item");
        var versionsBefore = await CountAsync(world, "select count(*) from public.authoring_version");

        var title = await RawAsync(world, HttpMethod.Post, world.Nodes, """{"kind":"module","title":"a\u0000b"}""");
        RefusedAt(title, "/title", "invalid_text");

        var name = await RawAsync(world, HttpMethod.Post, world.Nodes, """{"kind":"module","ti\u0000tle":"x"}""");
        name.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, name.ErrorMessage);
        Details(name).ShouldContain(detail => detail.Path == "/<invalid-member-name>" && detail.Reason == "invalid_text");

        var description = await RawAsync(
            world,
            HttpMethod.Post,
            world.Items,
            $$"""{"node_id":"{{world.Category:D}}","title":"An item","description":"d\u0000d","priority":"high"}""");
        RefusedAt(description, "/description", "invalid_text");

        var itemTitle = await RawAsync(
            world,
            HttpMethod.Post,
            world.Items,
            $$"""{"node_id":"{{world.Category:D}}","title":"An\u0000item","priority":"high"}""");
        RefusedAt(itemTitle, "/title", "invalid_text");

        (await CountAsync(world, "select count(*) from public.feature_node")).ShouldBe(nodesBefore, "no node row was written");
        (await CountAsync(world, "select count(*) from public.checklist_item")).ShouldBe(itemsBefore, "no item row was written");
        (await CountAsync(world, "select count(*) from public.authoring_version")).ShouldBe(versionsBefore, "no version row was written");
    }

    /// <summary>
    /// U+0000 in a step's text, a case title or a delete reason is refused at the leaf pointer, and the refusals burn no
    /// case code: the first case the project stores is still <c>-001</c>.
    /// </summary>
    [Fact]
    public async Task NulInStepsTitleOrReason_Is422_AndBurnsNoCode()
    {
        await using var world = await StartAsync();

        var item = (await world.Client.SendAsync(
            HttpMethod.Post, world.Items, world.As, new { node_id = world.Category, title = "An item", priority = "high" }, NewKey())).Id("item_id");
        var itemId = item.ToString("D");

        var steps = await RawAsync(
            world,
            HttpMethod.Post,
            world.Cases,
            $$"""{"checklist_item_id":"{{itemId}}","title":"Case","steps":[{"no":1,"action":"a\u0000b","expected":"shown"}],"priority":"high"}""");
        RefusedAt(steps, "/steps/0/action", "invalid_text");

        var title = await RawAsync(
            world,
            HttpMethod.Post,
            world.Cases,
            $$"""{"checklist_item_id":"{{itemId}}","title":"a\u0000b","steps":[{"no":1,"action":"a","expected":"shown"}],"priority":"high"}""");
        RefusedAt(title, "/title", "invalid_text");

        (await CountAsync(world, "select count(*) from public.manual_test_case")).ShouldBe(0L, "neither refusal stored a case");

        var created = await world.Client.SendAsync(
            HttpMethod.Post,
            world.Cases,
            world.As,
            new
            {
                checklist_item_id = item,
                title = "Case",
                steps = new[] { new { no = 1, action = "open", expected = "shown" } },
                priority = "high",
            },
            NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        created.Text("code")!.EndsWith("-001", StringComparison.Ordinal)
            .ShouldBeTrue("no refusal above allocated a code, so this case is the project's first");

        var caseId = created.Id("case_id");
        var reason = await RawAsync(world, HttpMethod.Delete, world.Case(caseId), """{"reason":"r\u0000r"}""", ifMatch: "1");
        RefusedAt(reason, "/reason", "invalid_text");

        (await CountAsync(world, "select count(*) from public.manual_test_case where deleted_at is null"))
            .ShouldBe(1L, "the refused delete wrote nothing");
    }

    /// <summary>
    /// <c>q</c> carrying U+0000 and a cursor that decodes to text the route never issued are 422 answers on the pointer
    /// of the query member; a cursor the route really issued is still accepted, so the guard is not a blanket refusal.
    /// </summary>
    [Fact]
    public async Task NulInQueryAndTamperedCursor_Are422()
    {
        await using var world = await StartAsync();

        var nulQuery = await world.Client.SendAsync(HttpMethod.Get, $"{world.Cases}?q=%00", world.As);
        RefusedAt(nulQuery, "/q", "invalid_text");

        var nulCursor = OpaqueCursor("SH\u0000OP-001", Guid.CreateVersion7());
        var cursor = await world.Client.SendAsync(HttpMethod.Get, $"{world.Cases}?cursor={nulCursor}", world.As);
        cursor.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, cursor.ErrorMessage);
        Details(cursor).ShouldContain(detail => detail.Path == "/cursor");

        // A code outside the stored grammar (lower case) is equally not a cursor this route issued.
        var lowerCase = OpaqueCursor("shop-001", Guid.CreateVersion7());
        var malformed = await world.Client.SendAsync(HttpMethod.Get, $"{world.Cases}?cursor={lowerCase}", world.As);
        malformed.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, malformed.ErrorMessage);
        Details(malformed).ShouldContain(detail => detail.Path == "/cursor");

        // The sibling list cursors are structural already; a NUL in either is still the route's own 422.
        foreach (var list in new[] { world.Items, world.Tree })
        {
            var refused = await world.Client.SendAsync(HttpMethod.Get, $"{list}?cursor=%00", world.As);
            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, list);
            Details(refused).ShouldContain(detail => detail.Path == "/cursor", list);
        }

        // The positive control: a real page issues a cursor and the same route reads it back, so the grammar check
        // cannot have turned every second page into a 422.
        var item = (await world.Client.SendAsync(
            HttpMethod.Post, world.Items, world.As, new { node_id = world.Category, title = "An item", priority = "high" }, NewKey())).Id("item_id");

        foreach (var suffix in new[] { "two", "three" })
        {
            (await world.Client.SendAsync(HttpMethod.Post, world.Cases, world.As, new
            {
                checklist_item_id = item,
                title = $"Case {suffix}",
                steps = new[] { new { no = 1, action = "open", expected = "shown" } },
                priority = "high",
            }, NewKey())).Status.ShouldBe(HttpStatusCode.Created);
        }

        var first = await world.Client.SendAsync(HttpMethod.Get, $"{world.Cases}?limit=1", world.As);
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        var next = first.Text("next_cursor");
        next.ShouldNotBeNullOrEmpty("two cases with limit=1 must issue a cursor");

        var second = await world.Client.SendAsync(
            HttpMethod.Get, $"{world.Cases}?limit=1&cursor={Uri.EscapeDataString(next!)}", world.As);
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        second.Body.GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    /// <summary>
    /// Every <c>details[].path</c> of this surface is a rooted JSON pointer: the tree and list query members and the
    /// <c>If-Match</c> of a stale write all answer <c>/name</c>, never a bare name.
    /// </summary>
    [Fact]
    public async Task DetailPaths_AreRootedPointers()
    {
        await using var world = await StartAsync();

        var badNode = await world.Client.SendAsync(HttpMethod.Get, $"{world.Tree}?node_id=not-a-uuid", world.As);
        badNode.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(badNode).ShouldContain(detail => detail.Path == "/node_id" && detail.Reason == "invalid");

        var badParent = await world.Client.SendAsync(HttpMethod.Get, $"{world.Tree}?parent_id=not-a-uuid", world.As);
        badParent.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(badParent).ShouldContain(detail => detail.Path == "/parent_id" && detail.Reason == "invalid");

        var both = await world.Client.SendAsync(
            HttpMethod.Get, $"{world.Tree}?node_id={Guid.CreateVersion7():D}&parent_id={Guid.CreateVersion7():D}", world.As);
        both.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(both).ShouldContain(detail => detail.Path == "/node_id" && detail.Reason == "conflict");

        var badItemNode = await world.Client.SendAsync(HttpMethod.Get, $"{world.Items}?node_id=not-a-uuid", world.As);
        badItemNode.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(badItemNode).ShouldContain(detail => detail.Path == "/node_id" && detail.Reason == "invalid");

        var node = (await world.Client.SendAsync(
            HttpMethod.Post, world.Nodes, world.As, new { kind = "module", title = "Patchable" }, NewKey())).Id("node_id");
        var stale = await world.Client.SendAsync(
            HttpMethod.Patch, world.Node(node), world.As, new { title = "Renamed" }, NewKey(), ifMatch: "\"7\"");
        stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        stale.ErrorCode.ShouldBe("precondition_failed");
        Details(stale).ShouldContain(detail => detail.Path == "/If-Match" && detail.Reason == "stale");
    }
}
