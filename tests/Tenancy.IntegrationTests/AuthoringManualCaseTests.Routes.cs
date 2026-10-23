using System.Globalization;
using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T2.3: routes 12–17 and 19 of 13.1.1 §5 through the real host and a real PostgreSQL — the list and detail
/// reads, the create that allocates a code (AC4), the PATCH that appends one version, the <c>If-Match</c> precondition
/// (AC19) and the steps schema of AC13.
/// </summary>
/// <remarks>
/// The fixture is the shared <see cref="PostgresTenancyFixture"/>: every test gets its own database, seeds an org and a
/// project as the owner and then calls the routes as a granted caller of that org. The domain rules that need no host
/// (the steps corpus) live in the existing partial file.
/// </remarks>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed partial class AuthoringManualCaseTests
{
    private const string Route = "/authoring/manual-cases";

    private readonly PostgresTenancyFixture _fixture;

    /// <summary>One fixture per test class instance (xUnit's collection fixture).</summary>
    /// <param name="fixture">The PostgreSQL fixture of the collection.</param>
    public AuthoringManualCaseTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>AC4: eight concurrent creates get eight distinct codes, and no route can change one afterwards.</summary>
    [Fact]
    public async Task CaseCode_IsAllocatedUniqueAndImmutable()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var (org, project, item, _) = await SeedTreeAsync(database);
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());

        var created = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => client.SendAsync(
            HttpMethod.Post,
            CasePath(project),
            caller,
            CaseBody(item, "Concurrent case " + index.ToString(CultureInfo.InvariantCulture)),
            NewKey())));

        foreach (var answer in created)
        {
            answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        }

        var codes = created.Select(answer => answer.Text("code")!).ToArray();
        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(8);
        codes.ShouldAllBe(code => System.Text.RegularExpressions.Regex.IsMatch(code, @"\A[A-Z][A-Z0-9]{1,7}-[0-9]{3}\z"));
        // Three-digit padding including the leading zeros of the first allocation (…-001), which is what makes the code
        // sortable as text as well.
        codes.ShouldContain(code => code.EndsWith("-001", StringComparison.Ordinal));
        created.ShouldAllBe(answer => answer.Body.GetProperty("version").GetInt32() == 1);

        // The code is immutable: a PATCH that carries it is refused with the member's own path, and the row keeps it.
        var first = created[0];
        var caseId = first.Body.GetProperty("case_id").GetGuid();
        var refused = await client.SendAsync(
            HttpMethod.Patch,
            CasePath(project) + "/" + caseId.ToString("D"),
            caller,
            Element($$"""{"code":"{{first.Text("code")}}-9"}"""),
            NewKey(),
            first.ETag);

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("request_invalid");
        refused.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .ShouldContain(detail => detail.GetProperty("path").GetString() == "/code");

        // The item is immutable here too: moving is route 19.
        var move = await client.SendAsync(
            HttpMethod.Patch,
            CasePath(project) + "/" + caseId.ToString("D"),
            caller,
            Element($$"""{"checklist_item_id":"{{Guid.CreateVersion7()}}"}"""),
            NewKey(),
            first.ETag);

        move.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        move.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .ShouldContain(detail => detail.GetProperty("path").GetString() == "/checklist_item_id");

        (await _fixture.ScalarAsync<string>(database, $"select code from public.manual_test_case where id = '{caseId}'"))
            .ShouldBe(first.Text("code"));
    }

    /// <summary>Route 12: the page is ordered <c>(code, id)</c>, the keyset cursor advances it, and the filters narrow it.</summary>
    [Fact]
    public async Task CaseList_IsOrderedByCodePagesByKeyset_AndFilters()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var (org, project, item, other) = await SeedTreeAsync(database);
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());

        foreach (var title in (string[])["Pay with a card", "Pay with a transfer", "Refund 100% of the order"])
        {
            (await client.SendAsync(HttpMethod.Post, CasePath(project), caller, CaseBody(item, title), NewKey()))
                .Status.ShouldBe(HttpStatusCode.Created);
        }

        (await client.SendAsync(HttpMethod.Post, CasePath(project), caller, CaseBody(other, "Elsewhere"), NewKey()))
            .Status.ShouldBe(HttpStatusCode.Created);

        var firstPage = await client.SendAsync(HttpMethod.Get, CasePath(project) + "?limit=2", caller);
        firstPage.Status.ShouldBe(HttpStatusCode.OK);
        var firstItems = firstPage.Body.GetProperty("items").EnumerateArray().ToArray();
        firstItems.Length.ShouldBe(2);
        string.Compare(firstItems[0].GetProperty("code").GetString(), firstItems[1].GetProperty("code").GetString(), StringComparison.Ordinal)
            .ShouldBeLessThanOrEqualTo(0, "the page is ordered by code");
        firstItems[0].GetProperty("node_id").GetGuid().ShouldNotBe(Guid.Empty, "the list carries the item's node");
        // No coverage field of AC12 here: that read model is 13.1.1-T4's.
        firstItems[0].TryGetProperty("linked_live_count", out _).ShouldBeFalse();

        var cursor = firstPage.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldNotBeNullOrEmpty();

        var secondPage = await client.SendAsync(
            HttpMethod.Get,
            CasePath(project) + "?limit=2&cursor=" + Uri.EscapeDataString(cursor!),
            caller);
        secondPage.Status.ShouldBe(HttpStatusCode.OK);
        var secondCodes = secondPage.Body.GetProperty("items").EnumerateArray()
            .Select(row => row.GetProperty("code").GetString()!)
            .ToArray();
        secondCodes.Length.ShouldBe(2);
        secondCodes.ShouldNotIntersectWith(firstItems.Select(row => row.GetProperty("code").GetString()!));

        // `q` is a literal substring, not a LIKE pattern: `%` matches only a title that contains one.
        var percent = await client.SendAsync(HttpMethod.Get, CasePath(project) + "?q=%25", caller);
        percent.Status.ShouldBe(HttpStatusCode.OK);
        percent.Body.GetProperty("items").GetArrayLength().ShouldBe(0);
        percent.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);

        var refund = await client.SendAsync(HttpMethod.Get, CasePath(project) + "?q=REFUND", caller);
        refund.Body.GetProperty("items").GetArrayLength().ShouldBe(1, "q is case-insensitive");

        var itemFilter = await client.SendAsync(HttpMethod.Get, CasePath(project) + "?item_id=" + other.ToString("D"), caller);
        itemFilter.Body.GetProperty("items").GetArrayLength().ShouldBe(1);

        var badState = await client.SendAsync(HttpMethod.Get, CasePath(project) + "?state=pending", caller);
        badState.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        badState.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .ShouldContain(detail => detail.GetProperty("path").GetString() == "/state");

        // AC12's filter is not silently ignored.
        var covered = await client.SendAsync(HttpMethod.Get, CasePath(project) + "?covered=false", caller);
        covered.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        covered.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .ShouldContain(detail => detail.GetProperty("path").GetString() == "/covered");
    }

    /// <summary>Route 14: the detail carries the steps and an <c>ETag</c> equal to the version.</summary>
    [Fact]
    public async Task CaseDetail_CarriesStepsAndVersionETag()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var (org, project, item, _) = await SeedTreeAsync(database);
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());

        var created = await client.SendAsync(HttpMethod.Post, CasePath(project), caller, CaseBody(item, "Pay with a card"), NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created);
        var caseId = created.Body.GetProperty("case_id").GetGuid();
        created.ETag.ShouldBe("\"1\"");

        var detail = await client.SendAsync(HttpMethod.Get, CasePath(project) + "/" + caseId.ToString("D"), caller);
        detail.Status.ShouldBe(HttpStatusCode.OK);
        detail.ETag.ShouldBe("\"1\"");
        detail.Text("title").ShouldBe("Pay with a card");
        detail.Body.GetProperty("node_id").GetGuid().ShouldNotBe(Guid.Empty);
        detail.Body.GetProperty("state").GetString().ShouldBe("draft");
        detail.Body.GetProperty("priority").GetString().ShouldBe("high");
        var steps = detail.Body.GetProperty("steps").EnumerateArray().ToArray();
        steps.Length.ShouldBe(1);
        steps[0].GetProperty("action").GetString().ShouldBe("Open the cart");
        steps[0].TryGetProperty("data", out _).ShouldBeFalse("an absent data member is not a null one");

        var missing = await client.SendAsync(HttpMethod.Get, CasePath(project) + "/" + Guid.CreateVersion7().ToString("D"), caller);
        missing.Status.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>Route 15: one PATCH appends exactly one version, and a repeat with the same body writes nothing (AC5).</summary>
    [Fact]
    public async Task CasePatch_WritesOneVersion_AndANoOpWritesNone()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var (org, project, item, _) = await SeedTreeAsync(database);
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());

        var created = await client.SendAsync(HttpMethod.Post, CasePath(project), caller, CaseBody(item, "Pay with a card"), NewKey());
        var caseId = created.Body.GetProperty("case_id").GetGuid();
        var path = CasePath(project) + "/" + caseId.ToString("D");

        var patched = await client.SendAsync(
            HttpMethod.Patch,
            path,
            caller,
            Element("""{"title":"Pay with a stored card","description":"Card on file"}"""),
            NewKey(),
            "\"1\"");

        patched.Status.ShouldBe(HttpStatusCode.OK);
        patched.Body.GetProperty("version").GetInt32().ShouldBe(2);
        patched.Body.GetProperty("changed").GetBoolean().ShouldBeTrue();
        patched.ETag.ShouldBe("\"2\"");

        var again = await client.SendAsync(
            HttpMethod.Patch,
            path,
            caller,
            Element("""{"title":"Pay with a stored card","description":"Card on file"}"""),
            NewKey(),
            "\"2\"");

        again.Status.ShouldBe(HttpStatusCode.OK);
        again.Body.GetProperty("version").GetInt32().ShouldBe(2);
        again.Body.GetProperty("changed").GetBoolean().ShouldBeFalse();

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.authoring_version where entity_id = '{caseId}'"))
            .ShouldBe(2, "the create and the one real change");

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.authoring_version where entity_id = '{caseId}' and change_kind = 'update'"))
            .ShouldBe(1);
    }

    /// <summary>
    /// AC13/AC4: the steps schema refuses 0 steps, 101 steps, an over-long field, an unknown member and a non-string
    /// <c>data</c> — and none of those refusals burns a code, so the first real case of the project is <c>-001</c>.
    /// </summary>
    [Fact]
    public async Task StepsSchema_RejectsInvalidDocuments_AndBurnsNoCode()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var (org, project, item, _) = await SeedTreeAsync(database);
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());
        var id = item.ToString("D");

        var invalid = new (string Because, string Steps)[]
        {
            ("an empty list", "[]"),
            ("one step past the bound", Steps(101)),
            ("a 2001-character action", Steps(1, action: new string('a', 2001))),
            ("an unknown member", """[{"no":1,"action":"a","expected":"e","surprise":"x"}]"""),
            ("a non-string data", """[{"no":1,"action":"a","expected":"e","data":{"nested":true}}]"""),
        };

        foreach (var (because, steps) in invalid)
        {
            var refused = await client.SendAsync(
                HttpMethod.Post,
                CasePath(project),
                caller,
                Element("""{"checklist_item_id":"__ITEM__","title":"Case","steps":__STEPS__,"priority":"high"}"""
                    .Replace("__ITEM__", id, StringComparison.Ordinal)
                    .Replace("__STEPS__", steps, StringComparison.Ordinal)),
                NewKey());

            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, because);
            refused.ErrorCode.ShouldBe("request_invalid", because);
            refused.Body.GetProperty("error").GetProperty("details").EnumerateArray()
                .ShouldContain(detail => detail.GetProperty("path").GetString()!.StartsWith("/steps", StringComparison.Ordinal), because);
        }

        // An unknown member at the body's root is refused too, and it is not a step problem.
        var extra = await client.SendAsync(
            HttpMethod.Post,
            CasePath(project),
            caller,
            Element($$"""{"checklist_item_id":"{{id}}","title":"Case","steps":[],"priority":"high","code":"SHOP-777"}"""),
            NewKey());
        extra.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        extra.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .ShouldContain(detail => detail.GetProperty("path").GetString() == "/code");

        // The 100-step bound itself is accepted, and it is the project's first allocation: no refusal above burned one.
        var accepted = await client.SendAsync(
            HttpMethod.Post,
            CasePath(project),
            caller,
            Element("""{"checklist_item_id":"__ITEM__","title":"At the bound","steps":__STEPS__,"priority":"high"}"""
                .Replace("__ITEM__", id, StringComparison.Ordinal)
                .Replace("__STEPS__", Steps(100), StringComparison.Ordinal)),
            NewKey());

        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
        accepted.Text("code")!.EndsWith("-001", StringComparison.Ordinal).ShouldBeTrue();
    }

    /// <summary>AC19: <c>If-Match</c> is required on PATCH, and a stale one is 412 with the current version and no write.</summary>
    [Fact]
    public async Task CasePatch_RequiresIfMatch_AndAnswersAStaleOneWith412()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var (org, project, item, _) = await SeedTreeAsync(database);
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());

        var created = await client.SendAsync(HttpMethod.Post, CasePath(project), caller, CaseBody(item, "Pay with a card"), NewKey());
        var caseId = created.Body.GetProperty("case_id").GetGuid();
        var path = CasePath(project) + "/" + caseId.ToString("D");

        var absent = await client.SendAsync(
            HttpMethod.Patch, path, caller, Element("""{"title":"Renamed"}"""), NewKey());
        absent.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        absent.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .ShouldContain(detail => detail.GetProperty("path").GetString() == "/If-Match"
                && detail.GetProperty("reason").GetString() == "required");

        (await client.SendAsync(
            HttpMethod.Patch, path, caller, Element("""{"title":"Renamed"}"""), NewKey(), "\"1\""))
            .Status.ShouldBe(HttpStatusCode.OK);

        var stale = await client.SendAsync(
            HttpMethod.Patch, path, caller, Element("""{"title":"Renamed again"}"""), NewKey(), "\"1\"");

        stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        stale.ErrorCode.ShouldBe("precondition_failed");
        stale.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .ShouldContain(detail => detail.GetProperty("path").GetString() == "/version"
                && detail.GetProperty("reason").GetString() == "2");

        (await _fixture.ScalarAsync<string>(database, $"select title from public.manual_test_case where id = '{caseId}'"))
            .ShouldBe("Renamed");
    }

    /// <summary>AC10/§5: a member without the permission is 403 and another tenant's caller is 404.</summary>
    [Fact]
    public async Task CaseRoutes_RefuseAViewerAndHideAnotherTenantsCase()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var connectionString = _fixture.AppConnectionString(database);
        var (org, project, item, _) = await SeedTreeAsync(database);
        var caseId = await SeedCaseAsync(database, org, project, item, "SHOP-001", "Pay with a card");
        var detailPath = CasePath(project) + "/" + caseId.ToString("D");

        // A member with no role holds no permission at all, which is the viewer's case without inventing a role grant.
        var viewer = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{Guid.CreateVersion7()}', '{org}', '{viewer}', 'viewer-{viewer:N}@example.test', 'active')
            """);

        await using (var real = TenancyApiFactory.RealResolver(connectionString))
        {
            using var client = real.CreateClient();
            var refused = await client.SendAsync(
                HttpMethod.Get, detailPath, TenancyApiFactory.Resolved(user: viewer, orgHeader: org));

            refused.Status.ShouldBe(HttpStatusCode.Forbidden);
            refused.ErrorCode.ShouldBe("forbidden");
        }

        // Another tenant's granted caller sees the same 404 a missing id produces: RLS decides, the URL does not.
        await using var granted = TenancyApiFactory.GrantAll(connectionString);
        using var grantedClient = granted.CreateClient();
        var rival = await SeedOrgAsync(database, "rival");
        var foreign = await grantedClient.SendAsync(
            HttpMethod.Get, detailPath, TenancyApiFactory.Caller(org: rival, user: Guid.CreateVersion7()));

        foreign.Status.ShouldBe(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The manual case routes of one project (the group is <c>/v1/projects/{p}/authoring</c>).</summary>
    private static string CasePath(Guid project) =>
        string.Create(CultureInfo.InvariantCulture, $"/v1/projects/{project:D}") + Route;

    /// <summary>A valid create body with one step.</summary>
    private static JsonElement CaseBody(Guid item, string title) => Element(
        """{"checklist_item_id":"__ITEM__","title":"__TITLE__","description":null,"precondition":"A cart with one item","steps":[{"no":1,"action":"Open the cart","expected":"The cart is shown"}],"priority":"high"}"""
            .Replace("__ITEM__", item.ToString("D"), StringComparison.Ordinal)
            .Replace("__TITLE__", title, StringComparison.Ordinal));

    /// <summary>A <c>steps</c> array of <paramref name="count"/> valid steps, as raw JSON.</summary>
    private static string Steps(int count, string? action = null)
    {
        var steps = Enumerable.Range(1, count).Select(no =>
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"no":{{no}},"action":"{{action ?? "Do the thing"}}","expected":"It happens"}"""));

        return "[" + string.Join(',', steps) + "]";
    }

    /// <summary>A raw JSON body, so an unknown member or an out-of-bound tree reaches the route exactly as written.</summary>
    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>An org row as the owner.</summary>
    private async Task<Guid> SeedOrgAsync(string database, string slug)
    {
        var id = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"insert into public.organization (id, name, slug) values ('{id}', '{slug} org', '{slug}')");

        return id;
    }

    /// <summary>One node and two active items under it, seeded as the owner (the composite FK chain).</summary>
    private async Task<(Guid Org, Guid Project, Guid Item, Guid OtherItem)> SeedTreeAsync(string database)
    {
        var org = await SeedOrgAsync(database, "acme");
        var project = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"insert into public.project (id, org_id, name, slug) values ('{project}', '{org}', 'shop project', 'shop')");

        var (item, other) = await SeedItemsAsync(database, org, project);

        return (org, project, item, other);
    }

    /// <summary>Two items under one node of a project, seeded as the owner.</summary>
    private async Task<(Guid Item, Guid OtherItem)> SeedItemsAsync(string database, Guid org, Guid project)
    {
        var node = Guid.CreateVersion7();
        var item = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.feature_node (id, org_id, project_id, kind, title, position)
            values ('{node}', '{org}', '{project}', 'module', 'Shop', 0);
            insert into public.checklist_item (id, org_id, project_id, feature_node_id, title, priority, state, source, position)
            values ('{item}', '{org}', '{project}', '{node}', 'Checkout', 'high', 'active', 'manual', 0),
                   ('{other}', '{org}', '{project}', '{node}', 'Refunds', 'low', 'active', 'manual', 1);
            """);

        return (item, other);
    }

    /// <summary>One case row under an item, seeded as the owner (for a test that needs it before any route runs).</summary>
    private async Task<Guid> SeedCaseAsync(
        string database,
        Guid org,
        Guid project,
        Guid item,
        string code,
        string title)
    {
        var caseId = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.manual_test_case (id, org_id, project_id, checklist_item_id, code, title, steps, priority, state, source)
            values ('{caseId}', '{org}', '{project}', '{item}', '{code}', '{title}',
                    jsonb_build_object('steps', jsonb_build_array(jsonb_build_object('no', 1, 'action', 'Open', 'expected', 'Shown'))),
                    'high', 'draft', 'manual')
            """);

        return caseId;
    }

}
