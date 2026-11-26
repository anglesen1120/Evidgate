using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>Project ingest-token administration, exchange and tenant isolation through the real host.</summary>
[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed partial class CiAuthTests : IAsyncLifetime, IClassFixture<TenancyHostPool>
{
    private static readonly string[] EventOnly = ["event:write"];

    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;
    private DedicatedDatabase? _dedicated;

    public CiAuthTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

    // This class asserts on unfiltered CI tables, so each test owns a migrated database.
    public async ValueTask InitializeAsync() => _dedicated = await _fixture.CreateDedicatedDatabaseAsync();

    public async ValueTask DisposeAsync()
    {
        if (_dedicated is not null)
        {
            await _dedicated.DisposeAsync();
        }
    }

    [Fact]
    public async Task RetiredBindingRoutes_AreUnserved_WhileProjectTokenAdministrationIsAvailable()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var admin = Admin(user, org);
        (await CreateTokenAsync(client, user, org, project)).Status.ShouldBe(HttpStatusCode.Created);
        var templates = api.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty).ToArray();
        templates.ShouldContain(path => path.EndsWith("/ingest-tokens", StringComparison.Ordinal));
        templates.ShouldNotContain(path => path.Contains("/ci-bindings", StringComparison.Ordinal));
        var list = $"/v1/projects/{project}/ci-bindings";
        var item = $"/v1/ci-bindings/{Guid.CreateVersion7()}";
        foreach (var (method, path) in new[] { (HttpMethod.Get, list), (HttpMethod.Post, list), (HttpMethod.Patch, item), (HttpMethod.Delete, item) })
        {
            (await client.SendAsync(method, path, admin)).Status.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task IngestToken_CreateAndExchange_AuditTheRightActorWithoutTheSecret()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var token = await CreateTokenAsync(client, user, org, project);
        (await ExchangeIngestAsync(client, token.Text("secret")!)).Status.ShouldBe(HttpStatusCode.OK);

        var rows = await _fixture.ColumnAsync<string>(
            _database!,
            $"select action || '|' || actor_type from public.audit_log where org_id = '{org}' and action in ('ingest_token.create', 'ingest_token.exchange') order by seq");
        rows.ShouldBe(["ingest_token.create|user", "ingest_token.exchange|ci_producer"]);

        // No secret reaches the trail.
        (await _fixture.ScalarAsync<long>(_database!, $"select count(*) from public.audit_log where metadata::text like '%{token.Text("secret")![^43..]}%'"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task OtherOrgsTokens_AreNotFound()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var userB = Guid.CreateVersion7();
        var orgB = await CreateOrgAsync(client, userB, "other-org");
        var token = (await CreateTokenAsync(client, user, org, project)).Body.GetProperty("token").GetProperty("id").GetGuid();

        (await client.SendAsync(HttpMethod.Delete, $"/v1/ingest-tokens/{token}", Admin(userB, orgB), null, NewKey())).Status.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Post, $"/v1/ingest-tokens/{token}/rotate", Admin(userB, orgB))).Status.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Delete, $"/v1/ingest-tokens/{token}", Admin(user, org), null, NewKey())).Status.ShouldBe(HttpStatusCode.NoContent);
    }

    // ---------------------------------------------------------------- helpers

    private string? _database;

    private async Task<(TenancyApiFactory Api, HttpClient Client, Guid User, Guid Org, Guid Project)> StartAsync()
    {
        _database = _dedicated!.Name;
        // A Ci-composed host over this test's dedicated database: the pool has no Ci variant, so it stays private.
        var api = TenancyApiFactory.Ci(_fixture.AppConnectionString(_database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return (api, client, user, org, project);
    }

    private static Action<HttpRequestHeaders> Admin(Guid user, Guid org) => TenancyApiFactory.Resolved(user: user, orgHeader: org);

    private static Task<ApiAnswer> CreateTokenAsync(HttpClient client, Guid user, Guid org, Guid project) =>
        client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/ingest-tokens", Admin(user, org), new { name = "gitlab" });

    private static Task<ApiAnswer> ExchangeIngestAsync(HttpClient client, string token) =>
        client.SendAsync(HttpMethod.Post, "/v1/ci/token", _ => { }, new { ingest_token = token });

    private static async Task<Guid> CreateOrgAsync(HttpClient client, Guid user, string slug)
    {
        var created = await client.SendAsync(
            HttpMethod.Post, "/v1/orgs", TenancyApiFactory.Resolved(user: user, email: $"{slug}@example.test"), new { name = slug, slug }, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created);

        return created.Id();
    }

    private static async Task<Guid> CreateProjectAsync(HttpClient client, Guid user, Guid org, string slug)
    {
        var created = await client.SendAsync(
            HttpMethod.Post, $"/v1/orgs/{org}/projects", TenancyApiFactory.Resolved(user: user), new { name = slug, slug }, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created);

        return created.Id();
    }
}
