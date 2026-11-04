using System.Net;
using System.Net.Http.Headers;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class CatalogTaxonomyQaTests(PostgresTenancyFixture fixture)
{
    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("taxonomy"));
        var project = await CreateProjectAsync(client, user, org, "checkout");
        return new World(api, client, database, user, org, project);
    }

    private static async Task<CaseSyncReport> RegisterAsync(World world, string requirement)
    {
        var id = Guid.CreateVersion7();
        SourceFile[] files =
        [
            new("tests/checkout.spec.ts", "test('x', () => {});", "playwright", "typescript"),
            new("tests/evidgate.cases.yaml", $"version: 1\ncases:\n  - file: checkout.spec.ts\n    evidgate.id: {id}\n    requirement: {requirement}\n"),
        ];
        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var result = await services.ServiceProvider.GetRequiredService<ICaseIdentityService>()
            .RegisterDiscoveredAsync(world.Project, "aaaaaaa1111111", files, TestContext.Current.CancellationToken);
        result.Succeeded.ShouldBeTrue(result.Failure);
        await scope.CommitAsync(TestContext.Current.CancellationToken);
        return result.Report.ShouldNotBeNull();
    }

    private static Task<ApiAnswer> PatchAsync(World world, Guid caseId, string? value) =>
        world.Client.SendAsync(HttpMethod.Patch, $"/v1/projects/{world.Project}/cases/{caseId}", world.Admin,
            new { requirement_ref = value }, NewKey());

    private static Task<ApiAnswer> CreateSuiteAsync(World world, object body) =>
        world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/suites", world.Admin, body, NewKey());

    private static Task<ApiAnswer> RevisionAsync(World world, Guid suite, object body) =>
        world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{suite}/revisions", world.Admin, body, NewKey());

    private async Task<int> AsAppAsync(World world, string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", world.Org.ToString());
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var rows = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return rows;
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("\"REQ?1\"")]
    [InlineData("\"REQ\\t1\"")]
    public async Task Sidecar_UnusableRequirement_PreservesCaseAndWarns(string yaml)
    {
        await using var world = await StartAsync();
        var report = await RegisterAsync(world, yaml);
        report.Registered.ShouldBe(1);
        report.Conflicts.ShouldBe(0);
        report.Warnings.ShouldBe(1);
        report.Entries.Single().Path.ShouldBe("tests/checkout.spec.ts");
        (await fixture.ScalarAsync<string?>(world.Database,
            $"select requirement_ref from public.test_case where project_id = '{world.Project}'")).ShouldBeNull();
        var caseId = await fixture.ScalarAsync<Guid>(world.Database,
            $"select id from public.test_case where project_id = '{world.Project}'");
        (await PatchAsync(world, caseId, "REQ-1")).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("[REQ-1]")]
    [InlineData("{id: REQ-1}")]
    [InlineData("REQ-1\n    requirement: REQ-2")]
    public async Task Sidecar_MalformedRequirement_RefusesManifestWithoutRegisteringCase(string yaml)
    {
        await using var world = await StartAsync();
        var report = await RegisterAsync(world, yaml);
        report.Registered.ShouldBe(0);
        report.Warnings.ShouldBeGreaterThan(0);
        (await fixture.ScalarAsync<int>(world.Database,
            $"select count(*)::int from public.test_case where project_id = '{world.Project}'")).ShouldBe(0);
        (await RegisterAsync(world, "REQ-1")).Registered.ShouldBe(1);
    }

    [Theory]
    [InlineData(129)]
    [InlineData(200)]
    [InlineData(201)]
    public async Task RequirementRef_CharacterBoundary_Accepts128AndRejectsLonger(int length)
    {
        await using var world = await StartAsync();
        (await RegisterAsync(world, "REQ-1")).Registered.ShouldBe(1);
        var caseId = await fixture.ScalarAsync<Guid>(world.Database,
            $"select id from public.test_case where project_id = '{world.Project}'");
        var maximum = new string('r', 128);
        var accepted = await PatchAsync(world, caseId, maximum);
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        accepted.Text("requirement_ref").ShouldBe(maximum);
        var refused = await PatchAsync(world, caseId, new string('r', length));
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("schema_invalid");
        (await fixture.ScalarAsync<string>(world.Database,
            $"select requirement_ref from public.test_case where id = '{caseId}'")).ShouldBe(maximum);
    }

    [Theory]
    [InlineData("\tREQ-1")]
    [InlineData("REQ-1\n")]
    [InlineData("REQ-\U0001F680")]
    [InlineData("https://jira.example.test/REQ-1?focused=1")]
    public async Task RequirementRef_ForbiddenCharacters_RejectWithoutChangingMetadata(string value)
    {
        await using var world = await StartAsync();
        (await RegisterAsync(world, "REQ-1")).Registered.ShouldBe(1);
        var caseId = await fixture.ScalarAsync<Guid>(world.Database,
            $"select id from public.test_case where project_id = '{world.Project}'");
        (await PatchAsync(world, caseId, " REQ-2 ")).Status.ShouldBe(HttpStatusCode.OK);
        var refused = await PatchAsync(world, caseId, value);
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("schema_invalid");
        (await fixture.ScalarAsync<string>(world.Database,
            $"select requirement_ref from public.test_case where id = '{caseId}'")).ShouldBe("REQ-2");
    }

    [Theory]
    [InlineData("platform", "desktop", "web")]
    [InlineData("level", "weekly", "e2e")]
    [InlineData("purpose", "gate", "regression")]
    public async Task Taxonomy_UnknownEnum_RejectsCreateRevisionAndFilter(string field, string invalid, string valid)
    {
        await using var world = await StartAsync();
        var body = new Dictionary<string, object> { ["name"] = "Taxonomy", ["slug"] = "taxonomy", [field] = invalid };
        var rejected = await CreateSuiteAsync(world, body);
        rejected.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        rejected.ErrorCode.ShouldBe("schema_invalid");
        rejected.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetProperty("path").GetString()).ShouldContain(path => path != null && path.TrimStart('/') == field);
        body[field] = valid;
        var created = await CreateSuiteAsync(world, body);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var revision = await RevisionAsync(world, created.Id(), new Dictionary<string, object> { [field] = invalid });
        revision.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        revision.ErrorCode.ShouldBe("schema_invalid");
        (await RevisionAsync(world, created.Id(), new Dictionary<string, object> { [field] = valid }))
            .Status.ShouldBe(HttpStatusCode.Created);
        var filter = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/suites?{field}={invalid}", world.Admin);
        filter.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        filter.ErrorCode.ShouldBe("schema_invalid");
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/suites?{field}={valid}", world.Admin))
            .Body.GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Taxonomy_CombinedFilters_IntersectAndKeepCursorScope()
    {
        await using var world = await StartAsync();
        for (var i = 0; i < 3; i++)
        {
            (await CreateSuiteAsync(world, new { name = "Api", slug = $"api-{i}", platform = "api", level = "integration", purpose = "release_gate" }))
                .Status.ShouldBe(HttpStatusCode.Created);
        }

        (await CreateSuiteAsync(world, new { name = "Web", slug = "web", purpose = "release_gate" })).Status.ShouldBe(HttpStatusCode.Created);
        var url = $"/v1/projects/{world.Project}/suites?platform=api&level=integration&purpose=release_gate&limit=2";
        var first = await world.Client.SendAsync(HttpMethod.Get, url, world.Admin);
        first.Status.ShouldBe(HttpStatusCode.OK);
        first.Body.GetProperty("items").GetArrayLength().ShouldBe(2);
        var cursor = first.Text("next_cursor").ShouldNotBeNull();
        var second = await world.Client.SendAsync(HttpMethod.Get, url + "&cursor=" + Uri.EscapeDataString(cursor), world.Admin);
        second.Status.ShouldBe(HttpStatusCode.OK);
        second.Body.GetProperty("items").GetArrayLength().ShouldBe(1);
        first.Body.GetProperty("items").EnumerateArray().Concat(second.Body.GetProperty("items").EnumerateArray())
            .Select(item => item.GetProperty("id").GetGuid()).Distinct().Count().ShouldBe(3);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/suites?platform=web&level=integration", world.Admin))
            .Body.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Taxonomy_ProjectScopedCredential_CannotReadOrWriteSiblingProject()
    {
        await using var world = await StartAsync();
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, "sibling");
        var suite = await CreateSuiteAsync(world, new { name = "Suite", slug = "suite", platform = "api" });
        suite.Status.ShouldBe(HttpStatusCode.Created);
        var scoped = TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: sibling, servicePermissions: ["suite:read", "suite:write"]);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/suites?platform=api", scoped)).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/suites/{suite.Id()}", scoped)).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{suite.Id()}/revisions", scoped, new { purpose = "smoke" }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
        var own = TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["suite:read", "suite:write"]);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/suites/{suite.Id()}", own)).Status.ShouldBe(HttpStatusCode.OK);
        (await world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{suite.Id()}/revisions", own, new { purpose = "smoke" }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("platform", "api")]
    [InlineData("level", "smoke")]
    [InlineData("purpose", "exploratory")]
    public async Task SuiteRevision_PublishWithTaxonomyMutation_IsRefusedByAppRole(string field, string value)
    {
        await using var world = await StartAsync();
        var suite = await CreateSuiteAsync(world, new { name = "Suite", slug = "suite" });
        suite.Status.ShouldBe(HttpStatusCode.Created);
        var revision = await RevisionAsync(world, suite.Id(), new { purpose = "regression" });
        revision.Status.ShouldBe(HttpStatusCode.Created, revision.ErrorMessage);
        var id = revision.Id("suite_revision_id");
        var error = await Should.ThrowAsync<PostgresException>(() => AsAppAsync(world,
            $"update public.suite_revision set status = 'published', {field} = '{value}' where id = '{id}'"));
        error.SqlState.ShouldBe("23514");
        error.MessageText.ShouldContain("revision_immutable");
        (await AsAppAsync(world, $"update public.suite_revision set status = 'published' where id = '{id}'")).ShouldBe(1);
        var read = await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{id}", world.Admin);
        read.Text(field).ShouldBe(revision.Text(field));
        read.Text("revision_hash").ShouldBe(revision.Text("revision_hash"));
    }

    [Fact]
    public async Task SuiteHead_MismatchedTaxonomyOrClearedHead_IsRefusedByAppRole()
    {
        await using var world = await StartAsync();
        var suite = await CreateSuiteAsync(world, new { name = "Suite", slug = "suite" });
        suite.Status.ShouldBe(HttpStatusCode.Created);
        var first = await RevisionAsync(world, suite.Id(), new { purpose = "regression" });
        var second = await RevisionAsync(world, suite.Id(), new { purpose = "smoke" });
        first.Status.ShouldBe(HttpStatusCode.Created);
        second.Status.ShouldBe(HttpStatusCode.Created);
        var mismatch = await Should.ThrowAsync<PostgresException>(() => AsAppAsync(world,
            $"update public.suite set head_revision_id = '{first.Id("suite_revision_id")}' where id = '{suite.Id()}'"));
        mismatch.SqlState.ShouldBe("23514");
        var cleared = await Should.ThrowAsync<PostgresException>(() => AsAppAsync(world,
            $"update public.suite set head_revision_id = null, purpose = 'exploratory' where id = '{suite.Id()}'"));
        cleared.SqlState.ShouldBe("23514");
        (await AsAppAsync(world,
            $"update public.suite set head_revision_id = '{first.Id("suite_revision_id")}', purpose = 'regression' where id = '{suite.Id()}'"))
            .ShouldBe(1);
    }
}
