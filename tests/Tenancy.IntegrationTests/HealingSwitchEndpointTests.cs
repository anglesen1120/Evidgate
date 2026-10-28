using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 08.6.3 AC1/AC2/AC7 over the real host and a real PostgreSQL: the kill switch's write path (tenant and project
/// scopes, the idempotent retry, the refusals of AC1) and its read path (the three scope rows, the resolved mode and
/// the null dependency observations). The routes, status codes, wire names and error codes are the 08.6.3 contract, so
/// these tests drive HTTP only and never a module type.
/// </summary>
/// <remarks>
/// The host runs with no <c>Healing:Apply</c> setting — the V1 default — which is why <c>enabled</c> answers 422
/// <c>mode_not_available</c> here; <c>V1ModeClampTests</c> covers the flag-on half of AC7 as the gate's unit.
/// </remarks>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class HealingSwitchEndpointTests
{
    private readonly PostgresTenancyFixture _fixture;

    public HealingSwitchEndpointTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project)
        : IAsyncDisposable
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
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    private static Task<ApiAnswer> PutAsync(World world, string json, string? key = null, Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(HttpMethod.Put, "/v1/healing/switch", caller ?? world.Admin, RawBody(json), key ?? NewKey());

    private static Task<ApiAnswer> GetAsync(World world, Guid? project = null, Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(
            HttpMethod.Get,
            project is null ? "/v1/healing/switch" : $"/v1/healing/switch?project_id={project:D}",
            caller ?? world.Admin);

    /// <summary>A body the raw-body helper needs to send verbatim (the JSON helper would re-serialize it).</summary>
    private static StringContent RawBody(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// AC1: a tenant row is persisted and read back, then the project override is persisted beside it and the
    /// resolution is the more restrictive of the two (<c>disabled</c>).
    /// </summary>
    [Fact]
    public async Task Tenant_and_project_rows_persist_and_resolve_to_the_most_restrictive()
    {
        await using var world = await StartAsync();

        var tenant = await PutAsync(world, """{"scope":"tenant","mode":"shadow","reason":"V1 posture"}""");
        tenant.Status.ShouldBe(HttpStatusCode.OK, tenant.ErrorMessage);
        tenant.Text("scope").ShouldBe("tenant");
        tenant.Text("mode").ShouldBe("shadow");
        tenant.Text("project_id").ShouldBeNull();
        tenant.Body.GetProperty("set_by").GetGuid().ShouldBe(world.User);
        tenant.Body.GetProperty("version").GetInt32().ShouldBe(1);

        var project = await PutAsync(world, $$"""{"scope":"project","project_id":"{{world.Project:D}}","mode":"disabled"}""");
        project.Status.ShouldBe(HttpStatusCode.OK, project.ErrorMessage);
        project.Text("scope").ShouldBe("project");
        project.Body.GetProperty("project_id").GetGuid().ShouldBe(world.Project);
        project.Body.GetProperty("version").GetInt32().ShouldBe(1);

        var read = await GetAsync(world, world.Project);
        read.Status.ShouldBe(HttpStatusCode.OK, read.ErrorMessage);
        read.Body.GetProperty("resolved").GetString().ShouldBe("disabled");
        read.Body.GetProperty("system").ValueKind.ShouldBe(JsonValueKind.Null);
        read.Body.GetProperty("tenant").GetProperty("mode").GetString().ShouldBe("shadow");
        read.Body.GetProperty("project").GetProperty("mode").GetString().ShouldBe("disabled");
        read.Body.GetProperty("dependency_health").GetProperty("llm").ValueKind.ShouldBe(JsonValueKind.Null);
        read.Body.GetProperty("dependency_health").GetProperty("knowledge_store").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>A database with no switch row at all resolves to <c>shadow</c>, the V1 posture (AC7).</summary>
    [Fact]
    public async Task A_tenant_with_no_row_anywhere_resolves_to_shadow()
    {
        await using var world = await StartAsync();

        var read = await GetAsync(world);

        read.Status.ShouldBe(HttpStatusCode.OK, read.ErrorMessage);
        read.Body.GetProperty("resolved").GetString().ShouldBe("shadow");
        read.Body.GetProperty("tenant").ValueKind.ShouldBe(JsonValueKind.Null);
        read.Body.GetProperty("project").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// AC1: <c>scope: system</c> is 403 <c>forbidden_scope</c> — before any project lookup, so a random project id in
    /// the same body changes nothing.
    /// </summary>
    [Fact]
    public async Task The_system_scope_is_refused_on_the_tenant_route()
    {
        await using var world = await StartAsync();

        foreach (var body in new[]
        {
            """{"scope":"system","mode":"shadow"}""",
            $$"""{"scope":"system","project_id":"{{Guid.CreateVersion7():D}}","mode":"enabled"}""",
        })
        {
            var refused = await PutAsync(world, body);

            refused.Status.ShouldBe(HttpStatusCode.Forbidden);
            refused.ErrorCode.ShouldBe("forbidden_scope");
        }
    }

    /// <summary>AC2: <c>unavailable</c> is not a storable mode — 422 <c>invalid_switch_mode</c>, never a row.</summary>
    [Fact]
    public async Task Unavailable_is_refused_with_its_own_code()
    {
        await using var world = await StartAsync();

        var refused = await PutAsync(world, """{"scope":"tenant","mode":"unavailable"}""");

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("invalid_switch_mode");

        var read = await GetAsync(world);
        read.Body.GetProperty("tenant").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>AC7 through the route: <c>enabled</c> is 422 <c>mode_not_available</c> while the clamp is off.</summary>
    [Fact]
    public async Task Enabled_is_refused_while_healing_apply_is_off()
    {
        await using var world = await StartAsync();

        var refused = await PutAsync(world, """{"scope":"tenant","mode":"enabled"}""");

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("mode_not_available");
    }

    /// <summary>The scope/project shape rules of the contract, each with the field detail a client can act on.</summary>
    [Fact]
    public async Task The_scope_and_project_shape_rules_are_enforced()
    {
        await using var world = await StartAsync();

        var tenantWithProject = await PutAsync(world, $$"""{"scope":"tenant","project_id":"{{world.Project:D}}","mode":"shadow"}""");
        tenantWithProject.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        tenantWithProject.ErrorCode.ShouldBe("request_invalid");

        var projectWithoutProject = await PutAsync(world, """{"scope":"project","mode":"shadow"}""");
        projectWithoutProject.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        projectWithoutProject.ErrorCode.ShouldBe("request_invalid");

        var unknownProject = await PutAsync(world, $$"""{"scope":"project","project_id":"{{Guid.CreateVersion7():D}}","mode":"shadow"}""");
        unknownProject.Status.ShouldBe(HttpStatusCode.NotFound);
        unknownProject.ErrorCode.ShouldBe("not_found");

        var unknownMember = await PutAsync(world, """{"scope":"tenant","mode":"shadow","surprise":1}""");
        unknownMember.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        unknownMember.ErrorCode.ShouldBe("request_invalid");
    }

    /// <summary>00 §9.1: the write requires an <c>Idempotency-Key</c>; without one it is a 400, not a stored row.</summary>
    [Fact]
    public async Task The_write_requires_an_idempotency_key()
    {
        await using var world = await StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Put, "/v1/healing/switch")
        {
            Content = RawBody("""{"scope":"tenant","mode":"shadow"}"""),
        };
        world.Admin(request.Headers);

        using var response = await world.Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// AC1: an idempotent retry replays the stored response verbatim — same version, one row — and a different body
    /// under the same key is 409 <c>idempotency_conflict</c> (00 §9.1).
    /// </summary>
    [Fact]
    public async Task The_same_key_replays_and_a_different_body_conflicts()
    {
        await using var world = await StartAsync();
        var key = NewKey();
        const string body = """{"scope":"tenant","mode":"shadow","reason":"first"}""";

        var first = await PutAsync(world, body, key);
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);

        var replay = await PutAsync(world, body, key);
        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.Body.GetProperty("version").GetInt32().ShouldBe(first.Body.GetProperty("version").GetInt32());

        var conflict = await PutAsync(world, """{"scope":"tenant","mode":"disabled","reason":"second"}""", key);
        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
    }

    /// <summary>A second write of the same scope bumps the row's version instead of adding a row.</summary>
    [Fact]
    public async Task A_second_write_of_the_same_scope_bumps_the_version()
    {
        await using var world = await StartAsync();

        var first = await PutAsync(world, """{"scope":"tenant","mode":"shadow"}""");
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);

        var second = await PutAsync(world, """{"scope":"tenant","mode":"disabled","reason":"incident"}""");
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        second.Body.GetProperty("version").GetInt32().ShouldBeGreaterThan(first.Body.GetProperty("version").GetInt32());
        second.Text("mode").ShouldBe("disabled");

        var read = await GetAsync(world);
        read.Body.GetProperty("tenant").GetProperty("version").GetInt32()
            .ShouldBe(second.Body.GetProperty("version").GetInt32());
    }
}
