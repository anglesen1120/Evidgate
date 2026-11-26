using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Deploy targets through the real host and a real PostgreSQL: create, the case-insensitive name, ordering and the
/// same-project policy pin. The versioned writes, soft delete and access rules are in <c>DeployTargetTests.Writes.cs</c>.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class DeployTargetTests
{
    private readonly PostgresTenancyFixture _fixture;

    public DeployTargetTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Create_ReturnsAnActiveTargetWithItsETag_AndWritesOneAuditRow()
    {
        await using var world = await StartAsync();

        var created = await CreateAsync(world, new { name = "staging", order = 10 });

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        created.Text("name").ShouldBe("staging");
        created.Text("state").ShouldBe("active");
        created.Body.GetProperty("order").GetInt32().ShouldBe(10);
        created.Body.GetProperty("policy_revision_id").ValueKind.ShouldBe(JsonValueKind.Null);
        created.Body.GetProperty("version").GetInt32().ShouldBe(1);
        created.ETag.ShouldBe("\"1\"");
        (await AuditCountAsync(world, "deploy_target.create")).ShouldBe(1);
    }

    [Fact]
    public async Task Name_IsUniqueIgnoringCase_AndTheSecondCreateIs409TargetExists()
    {
        await using var world = await StartAsync();
        (await CreateAsync(world, new { name = "Prod" })).Status.ShouldBe(HttpStatusCode.Created);

        var duplicate = await CreateAsync(world, new { name = "prod" });

        duplicate.Status.ShouldBe(HttpStatusCode.Conflict);
        duplicate.ErrorCode.ShouldBe("target_exists");
        (await CountAsync(world)).ShouldBe(1);
        (await AuditCountAsync(world, "deploy_target.create")).ShouldBe(1);
    }

    [Fact]
    public async Task TheSameName_IsFreeInAnotherProject()
    {
        await using var world = await StartAsync();
        var other = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("billing"));
        (await CreateAsync(world, new { name = "prod" })).Status.ShouldBe(HttpStatusCode.Created);

        var elsewhere = await CreateAsync(world, new { name = "PROD" }, project: other);

        elsewhere.Status.ShouldBe(HttpStatusCode.Created, elsewhere.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("-leading")]
    [InlineData("slash/name")]
    public async Task ABadName_Is422_AndWritesNothing(string name)
    {
        await using var world = await StartAsync();

        var refused = await CreateAsync(world, new { name });

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("request_invalid");
        (await CountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task AnOutOfRangeOrder_Is422()
    {
        await using var world = await StartAsync();

        (await CreateAsync(world, new { name = "a", order = -1 })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CreateAsync(world, new { name = "a", order = 10001 })).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task List_IsOrderedByOrderThenName_AndKeepsDisabledTargets()
    {
        await using var world = await StartAsync();
        await CreateAsync(world, new { name = "prod", order = 20 });
        await CreateAsync(world, new { name = "b-edge", order = 10 });
        var first = await CreateAsync(world, new { name = "a-edge", order = 10 });
        await DisableAsync(world, first.Id(), "\"1\"");

        var list = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/deploy-targets", world.Admin);

        list.Status.ShouldBe(HttpStatusCode.OK, list.ErrorMessage);
        list.Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString())
            .ToArray().ShouldBe(["a-edge", "b-edge", "prod"]);
        list.Body.GetProperty("items")[0].GetProperty("state").GetString().ShouldBe("disabled");
        list.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Pin_ToARevisionOfTheSameProject_IsStored()
    {
        await using var world = await StartAsync();
        var revision = await InsertRevisionAsync(world, world.Project);

        var created = await CreateAsync(world, new { name = "prod", policy_revision_id = revision });

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        created.Id("policy_revision_id").ShouldBe(revision);
    }

    [Fact]
    public async Task Pin_ToARevisionOfAnotherProject_IsRefused_OnCreateAndOnPatch()
    {
        await using var world = await StartAsync();
        var other = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("billing"));
        var foreign = await InsertRevisionAsync(world, other);

        var create = await CreateAsync(world, new { name = "prod", policy_revision_id = foreign });
        create.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Detail(create).ShouldBe(("policy_revision_id", "unknown"));
        (await CountAsync(world)).ShouldBe(0);

        var target = await CreateAsync(world, new { name = "staging" });
        var patch = await PatchAsync(world, target.Id(), "\"1\"", new { policy_revision_id = foreign });
        patch.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.deploy_target where policy_revision_id is not null and org_id = '{world.Org:D}'")).ShouldBe(0);
    }

    [Fact]
    public async Task TheDatabase_RefusesACrossProjectPin_EvenWithoutTheApiCheck()
    {
        await using var world = await StartAsync();
        var other = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("billing"));
        var foreign = await InsertRevisionAsync(world, other);

        var error = await Should.ThrowAsync<Npgsql.PostgresException>(() => _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.deploy_target (id, org_id, project_id, name, policy_revision_id)
            values ('{Guid.CreateVersion7()}', '{world.Org}', '{world.Project}', 'x', '{foreign}')
            """));

        error.ConstraintName.ShouldBe("deploy_target_policy_revision_fk");
    }

    // ------------------------------------------------------------------ world and helpers

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
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

        return new World(api, client, database, user, org, project);
    }

    private static Task<ApiAnswer> CreateAsync(World world, object body, Guid? project = null, Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{project ?? world.Project}/deploy-targets", caller ?? world.Admin, body, NewKey());

    private static Task<ApiAnswer> PatchAsync(World world, Guid target, string? ifMatch, object body, Guid? project = null, Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(HttpMethod.Patch, $"/v1/projects/{project ?? world.Project}/deploy-targets/{target}", caller ?? world.Admin, body, NewKey(), ifMatch);

    private static Task<ApiAnswer> DisableAsync(World world, Guid target, string? ifMatch, Guid? project = null, Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(HttpMethod.Delete, $"/v1/projects/{project ?? world.Project}/deploy-targets/{target}", caller ?? world.Admin, null, NewKey(), ifMatch);

    private Task<long> CountAsync(World world) =>
        _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.deploy_target where org_id = '{world.Org:D}'");

    private Task<long> AuditCountAsync(World world, string action) =>
        _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.audit_log where action = '{action}' and org_id = '{world.Org:D}'");

    private async Task<Guid> InsertRevisionAsync(World world, Guid project)
    {
        var id = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, state, created_by)
            values ('{id}', '{world.Org}', '{project}', 1, 'sha256:{new string('a', 64)}', jsonb_build_object(), 'draft', '{world.User}')
            """);

        return id;
    }

    private static (string Path, string Reason) Detail(ApiAnswer answer, int index = 0)
    {
        var detail = answer.Body.GetProperty("error").GetProperty("details")[index];
        return (detail.GetProperty("path").GetString()!, detail.GetProperty("reason").GetString()!);
    }
}
