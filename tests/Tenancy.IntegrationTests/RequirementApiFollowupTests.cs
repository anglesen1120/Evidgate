using System.Net;
using System.Text;
using System.Text.Json;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class RequirementApiFollowupTests(PostgresTenancyFixture fixture)
{
    [Fact]
    public async Task Body_NonBmpBoundary_IsAcceptedOnCreateAndPatch_AndExcessDoesNotMutate()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var body = string.Concat(Enumerable.Repeat("😀", 20_000));
        var root = $"/v1/projects/{world.Project:D}/authoring/requirements";
        using var created = await SendRawAsync(world, HttpMethod.Post, root, "{\"kind\":\"user_story\",\"title\":\"Boundary\",\"body\":\"" + body + "\"}");
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var id = document.RootElement.GetProperty("requirement_id").GetGuid();
        var path = root + $"/{id:D}";
        using var patched = await SendRawAsync(world, HttpMethod.Patch, path, "{\"body\":\"" + body + "\"}", created.Headers.ETag!.ToString());
        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = await VersionCountAsync(world, id);
        using var refusedCreate = await SendRawAsync(world, HttpMethod.Post, root, "{\"kind\":\"user_story\",\"title\":\"Excess\",\"body\":\"" + body + "😀\"}");
        using var refusedPatch = await SendRawAsync(world, HttpMethod.Patch, path, "{\"body\":\"" + body + "😀\"}", patched.Headers.ETag!.ToString());
        foreach (var refused in new[] { refusedCreate, refusedPatch })
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            using var error = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            error.RootElement.GetProperty("error").GetProperty("details").EnumerateArray().ShouldContain(d => d.GetProperty("path").GetString() == "/body");
        }
        (await VersionCountAsync(world, id)).ShouldBe(before);
        (await fixture.ScalarAsync<int>(world.Database, $"select count(*)::int from public.requirement where project_id = '{world.Project}'")).ShouldBe(1);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("LINK-001\n")]
    [InlineData("LINK-001\0")]
    public async Task Coverage_InvalidCaseCursor_Is422AtAfterCode(string cursor)
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var head = await world.CreatedAsync(world.Create());
        var caller = TenancyApiFactory.Resolved(user: world.User, orgHeader: world.Org);
        var path = $"/v1/projects/{world.Project:D}/authoring/requirements/{head.RequirementId:D}/coverage";
        var refused = await world.Client.SendAsync(HttpMethod.Get, path + "?after_code=" + Uri.EscapeDataString(cursor), caller);
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.Body.GetProperty("error").GetProperty("details").EnumerateArray().ShouldContain(d => d.GetProperty("path").GetString() == "/after_code");
        (await world.Client.SendAsync(HttpMethod.Get, path + "?after_code=LINK-001", caller)).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Link_MissingExpectedPin_Is422AtExpectedVersionId()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var head = await world.CreatedAsync(world.Create());
        var target = await RequirementLinkTestData.SeedAsync(world);
        var caller = TenancyApiFactory.Resolved(user: world.User, orgHeader: world.Org);
        var path = $"/v1/projects/{world.Project:D}/authoring/requirements/{head.RequirementId:D}/links";
        var refused = await world.Client.SendAsync(HttpMethod.Post, path, caller, new { checklist_item_id = target.Item }, NewKey());
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.Body.GetProperty("error").GetProperty("details").EnumerateArray().ShouldContain(d => d.GetProperty("path").GetString() == "/expected_version_id");
        (await world.Client.SendAsync(HttpMethod.Post, path, caller, new { checklist_item_id = target.Item, expected_version_id = head.VersionId }, NewKey())).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Audit_OfRequirementWrites_CarriesProjectAndVersionPins_AndNoCustomerText()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var caller = TenancyApiFactory.Resolved(user: world.User, orgHeader: world.Org);
        var target = await RequirementLinkTestData.SeedAsync(world);
        var root = $"/v1/projects/{world.Project:D}/authoring/requirements";
        var created = await world.Client.SendAsync(
            HttpMethod.Post, root, caller, new { kind = "user_story", title = "Secret title", body = "Secret body" }, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var path = root + $"/{created.Id("requirement_id"):D}";
        var patched = await world.Client.SendAsync(HttpMethod.Patch, path, caller, new { body = "Secret second", reason = "Secret reason" }, NewKey(), created.ETag);
        patched.Status.ShouldBe(HttpStatusCode.OK, patched.ErrorMessage);
        var restored = await world.Client.SendAsync(
            HttpMethod.Post, path + "/restore", caller, new { version_id = created.Id("version_id"), reason = "Secret reason" }, NewKey(), patched.ETag);
        restored.Status.ShouldBe(HttpStatusCode.OK, restored.ErrorMessage);
        var pin = await RequirementLinkTestData.HeadVersionIdAsync(world, created.Id("requirement_id"));
        var link = await world.Client.SendAsync(
            HttpMethod.Post, path + "/links", caller, new { checklist_item_id = target.Item, expected_version_id = pin }, NewKey());
        link.Status.ShouldBe(HttpStatusCode.Created, link.ErrorMessage);
        (await world.Client.SendAsync(HttpMethod.Delete, path + $"/links/{link.Id("link_id"):D}", caller, null, NewKey())).Status.ShouldBe(HttpStatusCode.NoContent);
        (await world.Client.SendAsync(HttpMethod.Delete, path, caller, null, NewKey(), restored.ETag)).Status.ShouldBe(HttpStatusCode.NoContent);

        var json = await fixture.ScalarAsync<string>(
            world.Database,
            $"""
            select json_agg(json_build_object(
                     'action', action, 'project', project_id, 'metadata', metadata,
                     'before', encode(before_hash, 'hex'), 'after', encode(after_hash, 'hex')) order by seq)::text
              from public.audit_log
             where org_id = '{world.Org:D}' and action in (
               'authoring.requirement.create', 'authoring.requirement.update', 'authoring.requirement.restore',
               'authoring.requirement.delete', 'authoring.requirement_link.create', 'authoring.requirement_link.remove')
            """);
        json.ShouldNotContain("Secret");
        var rows = JsonDocument.Parse(json).RootElement.EnumerateArray().ToDictionary(row => row.GetProperty("action").GetString()!);
        rows.Count.ShouldBe(6);
        foreach (var row in rows.Values)
        {
            row.GetProperty("project").GetGuid().ShouldBe(world.Project);
            row.GetProperty("metadata").TryGetProperty("version_no", out _).ShouldBeTrue();
            if (row.GetProperty("action").GetString() != "authoring.requirement.create")
            {
                row.GetProperty("metadata").TryGetProperty("previous_id", out _).ShouldBeTrue();
            }
        }

        var creation = rows["authoring.requirement.create"];
        creation.GetProperty("before").ValueKind.ShouldBe(JsonValueKind.Null);
        creation.GetProperty("metadata").GetProperty("version_no").GetString().ShouldBe("1");
        creation.GetProperty("metadata").TryGetProperty("previous_id", out _).ShouldBeFalse();
        creation.GetProperty("after").GetString().ShouldNotBeNullOrEmpty();
        creation.GetProperty("after").GetString().ShouldBe(rows["authoring.requirement.update"].GetProperty("before").GetString());

        foreach (var moved in new[] { "authoring.requirement.update", "authoring.requirement.restore" })
        {
            rows[moved].GetProperty("before").GetString().ShouldNotBeNullOrEmpty();
            rows[moved].GetProperty("after").GetString().ShouldNotBeNullOrEmpty();
        }

        rows["authoring.requirement.delete"].GetProperty("before").GetString().ShouldNotBeNullOrEmpty();
        var removed = rows["authoring.requirement_link.remove"];
        removed.GetProperty("before").GetString().ShouldNotBeNullOrEmpty();
        removed.GetProperty("metadata").GetProperty("fields").GetString().ShouldBe("checklist_item_id:" + target.Item.ToString("D"));
        rows["authoring.requirement_link.create"].GetProperty("after").GetString().ShouldBe(removed.GetProperty("before").GetString());
    }

    [Fact]
    public async Task VersionHistoryQuery_DoesNotCarryBodiesThroughTheSizeWindow()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var head = await world.CreatedAsync(world.Create());

        await using var scope = await TenantScope.BeginAsync(fixture.AppConnectionString(world.Database), world.Org);
        var plan = await scope.ScalarAsync<string>(
            "explain (verbose, format json) " + RequirementSql.SelectVersions,
            ("org_id", world.Org),
            ("project_id", world.Project),
            ("requirement_id", head.RequirementId),
            ("before_version_no", 5),
            ("limit", 50),
            ("max_bytes", RequirementWriter.MaximumVersionPageBytes));

        var windows = new List<JsonElement>();
        CollectNodes(JsonDocument.Parse(plan).RootElement[0].GetProperty("Plan"), "WindowAgg", windows);
        windows.ShouldNotBeEmpty();
        foreach (var output in windows.SelectMany(node => node.GetProperty("Output").EnumerateArray()))
        {
            CarriesBodyColumn(output.GetString()!).ShouldBeFalse(output.GetString());
        }
    }

    [Theory]
    [InlineData("body", true)]
    [InlineData("v_1.body", true)]
    [InlineData("\"v_1\".\"body\"", true)]
    [InlineData("(v_1.body)", true)]
    [InlineData("(octet_length(v_1.body))", false)]
    [InlineData("sum((octet_length(v_1.body))) OVER (?)", false)]
    [InlineData("page.body_bytes", false)]
    public void VersionHistoryWindowOutput_IdentifiesBodyColumnsRatherThanSizeExpressions(string output, bool carriesBody) =>
        CarriesBodyColumn(output).ShouldBe(carriesBody);

    private static bool CarriesBodyColumn(string output) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            output.Trim().Trim('(', ')'),
            """\A(?:(?:"[^"]+"|[A-Za-z_][A-Za-z_0-9]*)\.)*"?body"?\z""",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

    private static void CollectNodes(JsonElement node, string type, List<JsonElement> found)
    {
        if (node.GetProperty("Node Type").GetString() == type)
        {
            found.Add(node);
        }

        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                CollectNodes(child, type, found);
            }
        }
    }

    private Task<int> VersionCountAsync(RequirementTestWorld world, Guid id) => fixture.ScalarAsync<int>(
        world.Database, $"select count(*)::int from public.requirement_version where requirement_id = '{id}'");

    private static async Task<HttpResponseMessage> SendRawAsync(RequirementTestWorld world, HttpMethod method, string path, string json, string? etag = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        TenancyApiFactory.Resolved(user: world.User, orgHeader: world.Org)(request.Headers);
        request.Headers.Add("Idempotency-Key", NewKey());
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        return await world.Client.SendAsync(request);
    }
}
