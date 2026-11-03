using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Evidgate.Api.OperatorIngress;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
/// The default host runs with no <c>Healing:Apply</c> setting — the V1 default — which is why <c>enabled</c> answers
/// 422 <c>mode_not_available</c> there; <see cref="Enabled_is_accepted_when_healing_apply_is_on"/> flips the flag
/// through the host configuration (<c>EVIDGATE:Healing:Apply</c>) and proves the other half of AC7 end to end, and
/// <c>V1ModeClampTests</c> covers the same matrix as the gate's unit.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class HealingSwitchEndpointTests
{
    /// <summary>The platform operator a permitting gate stamps on the request (the 02.8.1 stand-in).</summary>
    private static readonly Guid OperatorId = Guid.CreateVersion7();

    private readonly PostgresTenancyFixture _fixture;

    public HealingSwitchEndpointTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        WebApplicationFactory<Program> Host,
        HttpClient Client,
        DedicatedDatabase Database,
        Guid User,
        Guid Org,
        Guid Project,
        Guid Triage)
        : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        /// <summary>
        /// The org's <c>qa_engineer</c> (the built-in role holding <c>healing:triage</c>, 0010): the read side of the
        /// switch needs it, and the founder holds only <c>tenant_admin</c>/<c>release_approver</c> (T98), which carry
        /// <c>healing:configure</c> but not <c>healing:triage</c>.
        /// </summary>
        public Action<HttpRequestHeaders> TriageHeaders => TenancyApiFactory.Resolved(user: Triage, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            if (!ReferenceEquals(Host, Api))
            {
                await Host.DisposeAsync();
            }

            await Api.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    /// <summary>A gate that permits and stamps an authenticated operator principal, like the dead-letter suite's.</summary>
    private sealed class PermitGate(Guid operatorId) : IOperatorGate
    {
        public ValueTask<bool> IsOperatorAsync(HttpContext httpContext)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, operatorId.ToString("D"))],
                authenticationType: "operator-test"));

            return ValueTask.FromResult(true);
        }
    }

    /// <summary>
    /// One host over a fresh database with an org, a project, and an active <c>qa_engineer</c> member (the
    /// <c>healing:triage</c> holder) granted through the real role route. <paramref name="applyEnabled"/> turns the V1
    /// clamp on through the deployment configuration (<c>EVIDGATE:Healing:Apply</c>), and
    /// <paramref name="permittingOperator"/> replaces the fail-closed operator gate so the system row can be written
    /// through the real operator route — both are host-composition changes, never product substitutions.
    /// </summary>
    private async Task<World> StartAsync(bool applyEnabled = false, bool permittingOperator = false)
    {
        var database = await _fixture.CreateDedicatedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));

        WebApplicationFactory<Program> host = api;
        if (applyEnabled || permittingOperator)
        {
            host = api.WithWebHostBuilder(builder =>
            {
                if (applyEnabled)
                {
                    builder.ConfigureAppConfiguration((_, configuration) =>
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
                        {
                            ["EVIDGATE:Healing:Apply"] = "true",
                        }));
                }

                if (permittingOperator)
                {
                    builder.ConfigureTestServices(services =>
                        services.AddSingleton<IOperatorGate>(new PermitGate(OperatorId)));
                }
            });
        }

        var client = host.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        // The read side's caller: an active member holding `qa_engineer` (the only built-in role with
        // `healing:triage`). 0044's `membership_user_fk` needs the account row first, and `tenant_admin` may grant a
        // role to another member (the self-escalation rule binds self-grants only).
        var triage = Guid.CreateVersion7();
        var membership = Guid.CreateVersion7();
        var email = $"qa-{triage:N}@example.test";
        await _fixture.ExecuteAsync(
            database, $"insert into public.\"user\" (id, email) values ('{triage:D}', '{email}')");
        await _fixture.ExecuteAsync(
            database,
            $"insert into public.membership (id, org_id, user_id, invited_email, status) values ('{membership:D}', '{org:D}', '{triage:D}', '{email}', 'active')");

        var world = new World(api, host, client, database, user, org, project, triage);

        var role = await _fixture.ScalarAsync<Guid>(
            database, "select id from public.role where code = 'qa_engineer' and org_id is null");
        var granted = await SendAsync(
            world,
            HttpMethod.Post,
            $"/v1/orgs/{org:D}/memberships/{membership:D}/roles",
            $$"""{"role_id":"{{role:D}}"}""",
            NewKey());
        granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);

        return world;
    }

    /// <summary>
    /// Sends one request with the raw body the case wants. The registry's generic helper wraps its body in
    /// <see cref="JsonContent"/>, which would re-serialize a prepared content object instead of sending it: the
    /// switch's 422-vs-403-vs-404 vocabulary depends on the exact bytes, so these tests build the request themselves
    /// (the shape every other raw-body suite uses).
    /// </summary>
    private static async Task<ApiAnswer> SendAsync(
        World world,
        HttpMethod method,
        string url,
        string? json = null,
        string? key = null,
        Action<HttpRequestHeaders>? caller = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (json is not null)
        {
            request.Content = RawBody(json);
        }

        (caller ?? world.Admin)(request.Headers);
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        using var response = await world.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    private static Task<ApiAnswer> PutAsync(World world, string json, string? key = null, Action<HttpRequestHeaders>? caller = null) =>
        SendAsync(world, HttpMethod.Put, "/v1/healing/switch", json, key ?? NewKey(), caller);

    private static Task<ApiAnswer> GetAsync(World world, Guid? project = null, Action<HttpRequestHeaders>? caller = null) =>
        SendAsync(
            world,
            HttpMethod.Get,
            project is null ? "/v1/healing/switch" : $"/v1/healing/switch?project_id={project:D}",
            caller: caller ?? world.TriageHeaders);

    /// <summary>The operator's system write: no tenant headers and no <c>Idempotency-Key</c> (D-S124-Q4).</summary>
    private static Task<ApiAnswer> PostSystemAsync(World world, string json) =>
        SendAsync(world, HttpMethod.Post, "/internal/operator/healing/switch", json, caller: _ => { });

    /// <summary>A body the raw-body helper needs to send verbatim (the JSON helper would re-serialize it).</summary>
    private static StringContent RawBody(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>The <c>error.details[].path</c> rows of a refusal.</summary>
    private static List<string> DetailPaths(ApiAnswer answer) =>
        answer.Body.TryGetProperty("error", out var error) && error.TryGetProperty("details", out var details)
            && details.ValueKind == JsonValueKind.Array
                ? [.. details.EnumerateArray().Select(detail => detail.GetProperty("path").GetString() ?? string.Empty)]
                : [];

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
        tenant.Text("reason").ShouldBe("V1 posture");
        tenant.Body.GetProperty("project_id").ValueKind.ShouldBe(JsonValueKind.Null);
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

    /// <summary>
    /// AC7, the other half: with <c>Healing:Apply</c> on, <c>enabled</c> is storable on the tenant route and the read
    /// resolves it — the flag the gate reads is the deployment key (<c>EVIDGATE:Healing:Apply</c>), so a host that
    /// only looked like it set the flag would fail here.
    /// </summary>
    [Fact]
    public async Task Enabled_is_accepted_when_healing_apply_is_on()
    {
        await using var world = await StartAsync(applyEnabled: true);

        var stored = await PutAsync(world, """{"scope":"tenant","mode":"enabled"}""");
        stored.Status.ShouldBe(HttpStatusCode.OK, stored.ErrorMessage);
        stored.Text("mode").ShouldBe("enabled");

        var read = await GetAsync(world);
        read.Status.ShouldBe(HttpStatusCode.OK, read.ErrorMessage);
        read.Body.GetProperty("resolved").GetString().ShouldBe("enabled");
        read.Body.GetProperty("tenant").GetProperty("mode").GetString().ShouldBe("enabled");
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

    /// <summary>
    /// A soft-deleted project is not live: the write and the read answer the same 404 <c>not_found</c> an unknown id
    /// gets, and the tombstoned project contributes no row to the resolution (the registry's <c>status</c> is the
    /// boundary the waiver and quarantine routes also apply, SR-F1).
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_project_is_not_found_on_the_switch_routes()
    {
        await using var world = await StartAsync();

        var stored = await PutAsync(world, $$"""{"scope":"project","project_id":"{{world.Project:D}}","mode":"disabled"}""");
        stored.Status.ShouldBe(HttpStatusCode.OK, stored.ErrorMessage);

        await _fixture.ExecuteAsync(
            world.Database, $"update public.project set status = 'deleted' where id = '{world.Project:D}'");

        var write = await PutAsync(world, $$"""{"scope":"project","project_id":"{{world.Project:D}}","mode":"shadow"}""");
        write.Status.ShouldBe(HttpStatusCode.NotFound);
        write.ErrorCode.ShouldBe("not_found");

        var read = await GetAsync(world, world.Project);
        read.Status.ShouldBe(HttpStatusCode.NotFound);
        read.ErrorCode.ShouldBe("not_found");

        var orgRead = await GetAsync(world);
        orgRead.Status.ShouldBe(HttpStatusCode.OK, orgRead.ErrorMessage);
        orgRead.Body.GetProperty("project").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// AC1/SR-F2: the <c>system</c> row is written through the real operator route (a permitting gate stands in for
    /// 02.8.1) and a tenant's read sees its mode — but neither the operator's <c>set_by</c> nor the incident
    /// <c>reason</c>, which are the platform operator's own record.
    /// </summary>
    [Fact]
    public async Task The_tenant_read_redacts_the_system_row_operator_and_reason()
    {
        await using var world = await StartAsync(permittingOperator: true);

        var set = await PostSystemAsync(world, """{"mode":"disabled","reason":"platform incident"}""");
        set.Status.ShouldBe(HttpStatusCode.OK, set.ErrorMessage);
        set.Body.GetProperty("set_by").GetGuid().ShouldBe(OperatorId);
        set.Body.GetProperty("reason").GetString().ShouldBe("platform incident");

        var read = await GetAsync(world);
        read.Status.ShouldBe(HttpStatusCode.OK, read.ErrorMessage);
        read.Body.GetProperty("resolved").GetString().ShouldBe("disabled");

        var system = read.Body.GetProperty("system");
        system.GetProperty("scope").GetString().ShouldBe("system");
        system.GetProperty("mode").GetString().ShouldBe("disabled");
        system.GetProperty("set_by").ValueKind.ShouldBe(JsonValueKind.Null);
        system.GetProperty("reason").ValueKind.ShouldBe(JsonValueKind.Null);
        system.GetProperty("at").GetDateTimeOffset().ShouldBe(set.Body.GetProperty("at").GetDateTimeOffset());
        system.GetProperty("version").GetInt32().ShouldBe(set.Body.GetProperty("version").GetInt32());
    }

    /// <summary>
    /// The reason bound counts Unicode code points, the unit the column's own <c>length(reason)</c> check uses: 1001
    /// astral-plane characters (2002 UTF-16 units) are inside the 2000-character budget and stored, while 2001
    /// characters are refused on the <c>reason</c> field (QA-D7).
    /// </summary>
    [Fact]
    public async Task The_reason_is_bounded_in_code_points()
    {
        await using var world = await StartAsync();

        var emoji = string.Concat(Enumerable.Repeat("😀", 1001));
        var accepted = await PutAsync(world, $$"""{"scope":"tenant","mode":"shadow","reason":"{{emoji}}"}""");
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        accepted.Body.GetProperty("reason").GetString().ShouldBe(emoji);

        var tooLong = await PutAsync(world, $$"""{"scope":"tenant","mode":"shadow","reason":"{{new string('a', 2001)}}"}""");
        tooLong.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        DetailPaths(tooLong).ShouldContain("/reason");
    }

    /// <summary>00 §9.1: the write requires an <c>Idempotency-Key</c>; without one it is a 400, not a stored row.</summary>
    [Fact]
    public async Task The_write_requires_an_idempotency_key()
    {
        await using var world = await StartAsync();

        var refused = await SendAsync(world, HttpMethod.Put, "/v1/healing/switch", """{"scope":"tenant","mode":"shadow"}""");

        refused.Status.ShouldBe(HttpStatusCode.BadRequest);
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
