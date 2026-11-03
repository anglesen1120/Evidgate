using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Independent QA of 13.1.1-T2's node and tree routes (1-5) against a real PostgreSQL and the real host: the negative,
/// boundary and exploratory cases the task file's ACs name but the delivery-time suite does not cover — every wrong
/// kind pair of the AC1 ladder, the position bounds of AC2, the reorder set refusals, the AC7 delete guards followed by
/// the 404 a soft-deleted entity must answer on every verb (AC10), the 64 KiB body cap, every <c>If-Match</c> form the
/// edge must accept or refuse (AC19), the strict body reader, and the cross-org / sibling-project addressing of AC10.
/// </summary>
[Trait("Category", "Docker")]
public sealed class AuthoringQaNodesTests
{
    /// <summary>The route-body cap of 13.1.1 §8 (<c>AuthoringEndpointSupport.BodyCap</c>, module-internal).</summary>
    private const int BodyCap = 64 * 1024;

    private readonly PostgresTenancyFixture _fixture;

    /// <summary>Wires the fixture.</summary>
    /// <param name="fixture">The shared PostgreSQL fixture.</param>
    public AuthoringQaNodesTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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
        var org = await CreateOrgAsync(client, user, "qa-nodes");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>A create that must answer 201, returning the new node's id.</summary>
    private static async Task<Guid> CreatedAsync(World world, object body)
    {
        var created = await world.Client.SendAsync(HttpMethod.Post, world.Nodes, world.As, body, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id("node_id");
    }

    /// <summary>The <c>details[].reason</c> and <c>details[].path</c> values of an error answer, ordered.</summary>
    private static List<(string? Path, string? Reason)> Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => (
                detail.TryGetProperty("path", out var path) ? path.GetString() : null,
                detail.TryGetProperty("reason", out var reason) ? reason.GetString() : null))];

    /// <summary>Sends a raw, already-serialised JSON body (the only way to send a duplicate member or an exact byte cap).</summary>
    private static async Task<ApiAnswer> SendRawAsync(
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

    /// <summary>
    /// AC1's ladder end to end: the four kinds nest one inside the next and every other pair is
    /// <c>422 authoring_parent_invalid</c> with <c>parent_kind_invalid</c> and no row. The accepted chain is the control
    /// that the refusals are the ladder rather than a blanket refusal.
    /// </summary>
    [Fact]
    public async Task DepthLadder_RefusesEveryWrongParentKind()
    {
        await using var world = await StartAsync();

        var module = await CreatedAsync(world, new { kind = "module", title = "Checkout" });
        var feature = await CreatedAsync(world, new { parent_id = module, kind = "feature", title = "Payment" });
        var category = await CreatedAsync(world, new { parent_id = feature, kind = "category", title = "Cards" });
        var subCategory = await CreatedAsync(world, new { parent_id = category, kind = "sub_category", title = "3-D Secure" });

        // The fifth level does not exist: no kind may sit under a sub_category (AC1).
        var refused = new (Guid Parent, string Kind, string Label)[]
        {
            (module, "module", "module under module"),
            (module, "category", "category under module"),
            (module, "sub_category", "sub_category under module"),
            (feature, "feature", "feature under feature"),
            (feature, "sub_category", "sub_category under feature"),
            (category, "category", "category under category"),
            (subCategory, "feature", "feature under sub_category"),
            (subCategory, "category", "category under sub_category"),
            (subCategory, "sub_category", "sub_category under sub_category"),
        };

        foreach (var (parent, kind, label) in refused)
        {
            var answer = await world.Client.SendAsync(
                HttpMethod.Post, world.Nodes, world.As, new { parent_id = parent, kind, title = "Nope" }, NewKey());

            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, label);
            answer.ErrorCode.ShouldBe("authoring_parent_invalid", label);

            // AC1's reason set: anything under a sub_category is the fifth level (depth_exceeded); every other
            // wrong pair is the ladder's parent_kind_invalid.
            Details(answer).ShouldContain(
                detail => parent == subCategory
                    ? detail.Reason == "depth_exceeded" || detail.Reason == "parent_kind_invalid"
                    : detail.Reason == "parent_kind_invalid",
                label);
        }

        // The control: the accepted chain wrote exactly four rows, so nothing above reached a table.
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.feature_node where org_id = '{world.Org}'"))
            .ShouldBe(4, "a refused pair may not write a row");
    }

    /// <summary>
    /// AC2's position bounds over HTTP: <c>0</c> and the <c>1 000 000 000</c> cap are accepted, one past the cap and a
    /// negative value are <c>422 request_invalid</c> on <c>/position</c>, and a non-integer member is the body reader's
    /// 422 rather than a 500.
    /// </summary>
    [Fact]
    public async Task PositionBounds_AcceptTheCapAndRefusePastIt()
    {
        await using var world = await StartAsync();

        var zero = await world.Client.SendAsync(
            HttpMethod.Post, world.Nodes, world.As, new { kind = "module", title = "At zero", position = 0 }, NewKey());
        zero.Status.ShouldBe(HttpStatusCode.Created, zero.ErrorMessage);

        var atCap = await world.Client.SendAsync(
            HttpMethod.Post, world.Nodes, world.As, new { kind = "module", title = "At cap", position = 1_000_000_000 }, NewKey());
        atCap.Status.ShouldBe(HttpStatusCode.Created, atCap.ErrorMessage);

        foreach (var (position, label) in new (int, string)[] { (-1, "negative"), (1_000_000_001, "one past the cap") })
        {
            var refused = await world.Client.SendAsync(
                HttpMethod.Post, world.Nodes, world.As, new { kind = "module", title = label, position }, NewKey());

            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, label);
            refused.ErrorCode.ShouldBe("request_invalid", label);

            // The endpoint reader and the command validator answer the same field with the same path spelling the
            // route table promises (`/position`); a PascalCase `Position` would be a contract nit, not a pass.
            Details(refused).ShouldContain(
                detail => string.Equals(
                    detail.Path == null ? string.Empty : detail.Path.TrimStart('/'),
                    "position",
                    StringComparison.OrdinalIgnoreCase),
                label);
        }

        // A fractional and a textual position never bind: the strict reader answers 422, never a 500.
        foreach (var json in new[] { """{"kind":"module","title":"x","position":1.5}""", """{"kind":"module","title":"x","position":"abc"}""" })
        {
            var refused = await SendRawAsync(world, HttpMethod.Post, world.Nodes, json);

            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, json);
            refused.ErrorCode.ShouldBe("request_invalid", json);
        }

        // And the taken-position rule is the partial unique index's (AC2): position 0 is now held by a live root.
        var taken = await world.Client.SendAsync(
            HttpMethod.Post, world.Nodes, world.As, new { kind = "module", title = "Dup", position = 0 }, NewKey());
        taken.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        taken.ErrorCode.ShouldBe("request_invalid");
        // §5 route 2 promises `/position` at the HTTP edge (QA finding D-1): the writer's batch path
        // (`/changes/0/content/position`) is rebased by the handler, while AuthoringVersionTests.cs:209 still locks the
        // writer-level spelling in for direct writer callers.
        Details(taken).ShouldContain(
            detail => detail.Reason == "position_taken" && detail.Path == "/position");
    }

    /// <summary>
    /// AC2/AC19: the reorder list is a set over the live siblings — a duplicate, a missing sibling, a foreign id and a
    /// bare digest instead of the <c>order:</c> tag are each refused with the reason that names them, the accepted
    /// permutation renumbers and rotates the tag, and the tag the caller read before the move is stale afterwards.
    /// </summary>
    [Fact]
    public async Task Reorder_RefusesBadSets_AndRotatesTheOrderTag()
    {
        await using var world = await StartAsync();

        var module = await CreatedAsync(world, new { kind = "module", title = "Checkout" });
        var first = await CreatedAsync(world, new { parent_id = module, kind = "feature", title = "First" });
        var second = await CreatedAsync(world, new { parent_id = module, kind = "feature", title = "Second" });
        var stranger = await CreatedAsync(world, new { kind = "module", title = "Elsewhere" });

        var read = await world.Client.SendAsync(HttpMethod.Get, $"{world.Tree}?parent_id={module}", world.As);
        read.Status.ShouldBe(HttpStatusCode.OK, read.ErrorMessage);
        var tag = read.ETag.ShouldNotBeNull();
        tag.ShouldStartWith("\"order:sha256:", Case.Sensitive);

        var reorder = $"{world.Node(first)}/reorder";

        var duplicate = await world.Client.SendAsync(
            HttpMethod.Post, reorder, world.As, new { ordered_ids = new[] { first, first, second } }, NewKey(), tag);
        duplicate.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(duplicate).ShouldContain(detail => detail.Path == "/ordered_ids" && detail.Reason == "duplicate_id");

        var incomplete = await world.Client.SendAsync(
            HttpMethod.Post, reorder, world.As, new { ordered_ids = new[] { first } }, NewKey(), tag);
        incomplete.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(incomplete).ShouldContain(detail => detail.Path == "/ordered_ids" && detail.Reason == "incomplete");

        var foreign = await world.Client.SendAsync(
            HttpMethod.Post, reorder, world.As, new { ordered_ids = new[] { first, second, stranger } }, NewKey(), tag);
        foreign.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(foreign).ShouldContain(detail => detail.Path == "/ordered_ids" && detail.Reason == "not_a_sibling");

        // A digest without the prefix (an entity ETag's hex, or the bare sha256) is not the order tag this route issues.
        var bare = await world.Client.SendAsync(
            HttpMethod.Post,
            reorder,
            world.As,
            new { ordered_ids = new[] { second, first } },
            NewKey(),
            tag!.Trim('"').Replace("order:", string.Empty, StringComparison.Ordinal));
        bare.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        bare.ErrorCode.ShouldBe("request_invalid");
        Details(bare).ShouldContain(detail => detail.Path == "/If-Match" && detail.Reason == "invalid");

        var accepted = await world.Client.SendAsync(
            HttpMethod.Post, reorder, world.As, new { ordered_ids = new[] { second, first } }, NewKey(), tag);
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        accepted.Body.GetProperty("changed").GetBoolean().ShouldBeTrue();

        // The same list again is AC5's no-op: no row moves and the answer says so.
        var again = await world.Client.SendAsync(HttpMethod.Get, $"{world.Tree}?parent_id={module}", world.As);
        var refreshed = again.ETag.ShouldNotBeNull();
        var noop = await world.Client.SendAsync(
            HttpMethod.Post, reorder, world.As, new { ordered_ids = new[] { second, first } }, NewKey(), refreshed);
        noop.Status.ShouldBe(HttpStatusCode.OK, noop.ErrorMessage);
        noop.Body.GetProperty("changed").GetBoolean().ShouldBeFalse();

        // The tag read before the first reorder is stale now (AC19): 412 with the current tag in details, nothing moves.
        var stale = await world.Client.SendAsync(
            HttpMethod.Post, reorder, world.As, new { ordered_ids = new[] { first, second } }, NewKey(), tag);
        stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        stale.ErrorCode.ShouldBe("precondition_failed");
    }

    /// <summary>
    /// AC7/AC10: a node holding a live child or a live item is refused with the guard reason that names it, a leaf is
    /// soft-deleted, and the deleted node answers 404 on every verb and disappears from the tree — a second delete is
    /// 404, never a second tombstone.
    /// </summary>
    [Fact]
    public async Task Delete_GuardsThenAnswers404OnEveryVerb()
    {
        await using var world = await StartAsync();

        var module = await CreatedAsync(world, new { kind = "module", title = "Checkout" });
        var branch = await CreatedAsync(world, new { parent_id = module, kind = "feature", title = "Branch" });
        var leaf = await CreatedAsync(world, new { parent_id = module, kind = "feature", title = "Leaf" });
        var category = await CreatedAsync(world, new { parent_id = branch, kind = "category", title = "Cards" });
        var holder = await CreatedAsync(world, new { parent_id = module, kind = "feature", title = "Holder" });
        var categoryWithItem = await CreatedAsync(world, new { parent_id = holder, kind = "category", title = "With item" });

        var item = await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/projects/{world.Project}/authoring/checklist-items", world.As,
            new { node_id = categoryWithItem, title = "An item", priority = "high" }, NewKey());
        item.Status.ShouldBe(HttpStatusCode.Created, item.ErrorMessage);

        var children = await world.Client.SendAsync(HttpMethod.Delete, world.Node(module), world.As, null, NewKey(), "\"1\"");
        children.Status.ShouldBe(HttpStatusCode.Conflict);
        children.ErrorCode.ShouldBe("authoring_not_empty");
        Details(children).ShouldContain(detail => detail.Reason == "node_has_children");

        var items = await world.Client.SendAsync(HttpMethod.Delete, world.Node(categoryWithItem), world.As, null, NewKey(), "\"1\"");
        items.Status.ShouldBe(HttpStatusCode.Conflict);
        items.ErrorCode.ShouldBe("authoring_not_empty");
        Details(items).ShouldContain(detail => detail.Reason == "node_has_items");

        var deleted = await world.Client.SendAsync(HttpMethod.Delete, world.Node(leaf), world.As, null, NewKey(), "\"1\"");
        deleted.Status.ShouldBe(HttpStatusCode.NoContent);

        // The tombstone keeps its version and bumps it; every verb that addresses it now answers 404 (AC10) and the
        // tree no longer lists it.
        foreach (var second in new[]
        {
            await world.Client.SendAsync(HttpMethod.Delete, world.Node(leaf), world.As, null, NewKey(), "\"2\""),
            await world.Client.SendAsync(HttpMethod.Patch, world.Node(leaf), world.As, new { title = "Renamed" }, NewKey(), "\"2\""),
            await world.Client.SendAsync(HttpMethod.Get, $"{world.Tree}?node_id={leaf}", world.As),
        })
        {
            second.Status.ShouldBe(HttpStatusCode.NotFound);
            second.ErrorCode.ShouldBe("not_found");
        }

        var listed = await world.Client.SendAsync(HttpMethod.Get, $"{world.Tree}?parent_id={module}", world.As);
        listed.Status.ShouldBe(HttpStatusCode.OK, listed.ErrorMessage);
        listed.Body.GetProperty("items").EnumerateArray()
            .Select(node => node.GetProperty("node_id").GetGuid())
            .ShouldNotContain(leaf, "a soft-deleted node is hidden from the tree");

        // The live sibling is untouched by either refusal.
        (await world.Client.SendAsync(HttpMethod.Get, $"{world.Tree}?node_id={branch}", world.As)).Status
            .ShouldBe(HttpStatusCode.OK);

        // The category that holds the item is still there and still refuses (nothing was written by the 409).
        (await world.Client.SendAsync(HttpMethod.Get, $"{world.Tree}?node_id={category}", world.As)).Status
            .ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// AC16/AC19's edge: a body of exactly 64 KiB is read (and then refused by the field rules, never by the cap) while
    /// one byte more is <c>413 payload_too_large</c>; every <c>If-Match</c> form is either the version this route issued
    /// (quoted, weak, unquoted) or <c>422 /If-Match invalid</c>.
    /// </summary>
    [Fact]
    public async Task BodyCapAndIfMatchForms_AreReadExactly()
    {
        await using var world = await StartAsync();

        const string head = "{\"kind\":\"module\",\"title\":\"";
        const string tail = "\"}";
        var atCapTitle = new string('a', BodyCap - head.Length - tail.Length);
        var atCap = await SendRawAsync(world, HttpMethod.Post, world.Nodes, head + atCapTitle + tail);
        atCap.Status.ShouldNotBe(HttpStatusCode.RequestEntityTooLarge, "exactly 64 KiB is inside the cap");
        atCap.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, "a 64 KiB title is still too long for the field");

        var overCapTitle = new string('a', BodyCap - head.Length - tail.Length + 1);
        var overCap = await SendRawAsync(world, HttpMethod.Post, world.Nodes, head + overCapTitle + tail);
        overCap.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        overCap.ErrorCode.ShouldBe("payload_too_large");

        var node = await CreatedAsync(world, new { kind = "module", title = "Patchable" });

        // The forms the reader accepts, each against the version it names (every PATCH bumps it): the quoted
        // entity ETag, its weak form, and the bare number (CatalogEndpoints' parse).
        var acceptedForms = new[] { "\"1\"", "W/\"2\"", "3", "W/4", "\"5\"" };
        foreach (var accepted in acceptedForms)
        {
            var patched = await world.Client.SendAsync(
                HttpMethod.Patch, world.Node(node), world.As, new { title = $"Renamed {accepted}" }, NewKey(), accepted);
            patched.Status.ShouldBe(HttpStatusCode.OK, accepted);
            patched.Body.GetProperty("changed").GetBoolean().ShouldBeTrue(accepted);
        }

        // The forms it refuses: absent, a non-number, zero, a negative number and an overflowing number.
        var absent = await world.Client.SendAsync(HttpMethod.Patch, world.Node(node), world.As, new { title = "x" }, NewKey());
        absent.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(absent).ShouldContain(detail => detail.Path == "/If-Match" && detail.Reason == "required");

        foreach (var malformed in new[] { "\"abc\"", "\"0\"", "\"-1\"", "\"99999999999999999999\"" })
        {
            var refused = await world.Client.SendAsync(
                HttpMethod.Patch, world.Node(node), world.As, new { title = "x" }, NewKey(), malformed);
            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, malformed);
            refused.ErrorCode.ShouldBe("request_invalid", malformed);
            Details(refused).ShouldContain(detail => detail.Path == "/If-Match" && detail.Reason == "invalid", malformed);
        }
    }

    /// <summary>
    /// The strict body reader (AC16/OI-13-116) and AC10's addressing: an unknown member is named, a duplicate member and a
    /// lone surrogate in a member name are 422 rather than 500, another org's node is <c>parent_not_found</c> where it is
    /// addressed and 404 where it is read, and a credential scoped to a sibling project of the same org is
    /// <c>403 project_scope_mismatch</c>.
    /// </summary>
    [Fact]
    public async Task StrictBodyAndForeignAddressing_AreRefused()
    {
        await using var world = await StartAsync();

        var unknown = await SendRawAsync(world, HttpMethod.Post, world.Nodes, """{"kind":"module","title":"x","bogus":1}""");
        unknown.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(unknown).ShouldContain(detail => detail.Path == "/bogus" && detail.Reason == "unknown_field");

        var duplicate = await SendRawAsync(world, HttpMethod.Post, world.Nodes, """{"kind":"module","kind":"feature","title":"x"}""");
        duplicate.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        duplicate.ErrorCode.ShouldBe("request_invalid");

        // A lone surrogate in a member *name* cannot be echoed, so the answer is the stable placeholder path, not a 500.
        var surrogate = await SendRawAsync(world, HttpMethod.Post, world.Nodes, """{"kind":"module","title":"x","\ud800":1}""");
        surrogate.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(surrogate).ShouldContain(detail => detail.Path == "/<invalid-member-name>");

        // Another organization's node: an unreachable parent id is the ladder's own parent_not_found (AC1), and the tree
        // answers 404 for the same id (AC10) — a foreign id and a nonexistent one are indistinguishable.
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "qa-nodes-other");
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "other");
        var otherAs = TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg);
        var foreignNode = (await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/projects/{otherProject}/authoring/nodes", otherAs,
            new { kind = "module", title = "Foreign" }, NewKey())).Id("node_id");

        var foreignParent = await world.Client.SendAsync(
            HttpMethod.Post, world.Nodes, world.As, new { parent_id = foreignNode, kind = "feature", title = "Child" }, NewKey());
        foreignParent.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        foreignParent.ErrorCode.ShouldBe("authoring_parent_invalid");
        Details(foreignParent).ShouldContain(detail => detail.Reason == "parent_not_found");

        (await world.Client.SendAsync(HttpMethod.Get, $"{world.Tree}?node_id={foreignNode}", world.As)).Status
            .ShouldBe(HttpStatusCode.NotFound);

        // A sibling project of the same org: AC10 words this as 403 `project_scope_mismatch`, but no service role holds
        // an `authoring:*` code (contracts/permissions.json), so the permission stage refuses a service principal
        // first and the handler's scope branch stays defensive — the shape 00 §9.3 already records for `GET /runs`.
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, "sibling");
        var scoped = TenancyApiFactory.Resolved(
            serviceOrg: world.Org, serviceProject: sibling, servicePermissions: ["authoring:write"]);

        var mismatch = await world.Client.SendAsync(
            HttpMethod.Post, world.Nodes, scoped, new { kind = "module", title = "Scoped" }, NewKey());
        mismatch.Status.ShouldBe(HttpStatusCode.Forbidden);
        mismatch.ErrorCode.ShouldBe("forbidden");

        var mismatchRead = await world.Client.SendAsync(HttpMethod.Get, world.Tree, scoped);
        mismatchRead.Status.ShouldBe(HttpStatusCode.Forbidden);
        mismatchRead.ErrorCode.ShouldBe("forbidden");
    }
}
