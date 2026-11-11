using System.Net;
using System.Net.Http.Headers;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 02.4.1-T2/T3 and 02.4.2 through the real host: GitHub OIDC exchange against a local JWKS, binding challenge,
/// replay arbiter, minted <c>ci_producer</c> scope, and ingest-token create/exchange/rotate/revoke.
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed partial class CiAuthTests : IAsyncLifetime, IClassFixture<TenancyHostPool>
{
    private const long RepoId = 4242;
    private static readonly string[] EventOnly = ["event:write"];
    private static readonly string[] MainAndTags = ["refs/heads/main", "refs/tags/v*"];
    private static readonly string[] TagsOnly = ["refs/tags/v*"];
    private const string Workflow = "acme/web/.github/workflows/ci.yml@refs/heads/main";

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

    // ---------------------------------------------------------------- bindings (admin)

    [Fact]
    public async Task CreateBinding_ReturnsTheChallengeOnce_AndStoresOnlyAnHmac()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;

        var created = await CreateBindingAsync(client, user, org, project);
        created.Status.ShouldBe(HttpStatusCode.Created);
        var challenge = created.Body.GetProperty("binding_challenge").GetString()!;
        challenge.Length.ShouldBeGreaterThanOrEqualTo(43);
        created.Body.GetProperty("binding").GetProperty("status").GetString().ShouldBe("pending_verification");

        var list = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/ci-bindings", Admin(user, org));
        list.Status.ShouldBe(HttpStatusCode.OK);
        list.Body.GetRawText().ShouldNotContain(challenge);
        list.Body.GetProperty("items").GetArrayLength().ShouldBe(1);

        var stored = await _fixture.ScalarAsync<string>(_database!, "select encode(binding_challenge_hash, 'hex') from public.ci_binding");
        stored.Length.ShouldBe(64);
        (await _fixture.ScalarAsync<long>(_database!, $"select count(*) from public.ci_binding where position('{challenge}' in repository_full_name || array_to_string(allowed_ref_patterns, '')) > 0"))
            .ShouldBe(0);
        (await _fixture.ScalarAsync<int>(_database!, "select challenge_pepper_version from public.ci_binding")).ShouldBe(2);

        (await CreateBindingAsync(client, user, org, project)).Status.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateBinding_RefusesBadShapes_UnknownProjects_AndCallersWithoutThePermission()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;

        foreach (var body in new object[]
                 {
                     new { provider = "gitlab", repository_id = RepoId, repository_full_name = "acme/web", allowed_ref_patterns = new[] { "refs/heads/main" }, allowed_workflow_refs = new[] { Workflow } },
                     new { provider = "github_actions", repository_id = 0, repository_full_name = "acme/web", allowed_ref_patterns = new[] { "refs/heads/main" }, allowed_workflow_refs = new[] { Workflow } },
                     new { provider = "github_actions", repository_id = RepoId, repository_full_name = "not a repo", allowed_ref_patterns = new[] { "refs/heads/main" }, allowed_workflow_refs = new[] { Workflow } },
                     new { provider = "github_actions", repository_id = RepoId, repository_full_name = "acme/web", allowed_ref_patterns = new[] { "main" }, allowed_workflow_refs = new[] { Workflow } },
                     new { provider = "github_actions", repository_id = RepoId, repository_full_name = "acme/web", allowed_ref_patterns = Array.Empty<string>(), allowed_workflow_refs = new[] { Workflow } },
                     new { provider = "github_actions", repository_id = RepoId, repository_full_name = "acme/web", allowed_ref_patterns = new[] { "refs/heads/main" }, allowed_workflow_refs = new[] { "ci.yml" } },
                 })
        {
            (await client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/ci-bindings", Admin(user, org), body))
                .Status.ShouldBe(HttpStatusCode.UnprocessableEntity, body.ToString());
        }

        (await CreateBindingAsync(client, user, org, Guid.CreateVersion7())).Status.ShouldBe(HttpStatusCode.NotFound);

        var bare = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            _database!,
            $"""
            insert into public."user" (id, email) values ('{bare}', 'bare@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status) values (gen_random_uuid(), '{org}', '{bare}', 'bare@example.test', 'active')
            """);
        (await CreateBindingAsync(client, bare, org, project)).Status.ShouldBe(HttpStatusCode.Forbidden);

        (await _fixture.ScalarAsync<long>(_database!, "select count(*) from public.ci_binding")).ShouldBe(0);
    }

    [Fact]
    public async Task UpdateBinding_NeedsIfMatch_Disables_AndReEnablesOnlyAfterActivation()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var created = await CreateBindingAsync(client, user, org, project);
        var binding = created.Body.GetProperty("binding").GetProperty("id").GetGuid();
        var challenge = created.Body.GetProperty("binding_challenge").GetString()!;

        (await client.SendAsync(HttpMethod.Patch, $"/v1/ci-bindings/{binding}", Admin(user, org), new { status = "disabled" }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await client.SendAsync(HttpMethod.Patch, $"/v1/ci-bindings/{binding}", Admin(user, org), new { status = "disabled" }, NewKey(), "\"7\""))
            .Status.ShouldBe(HttpStatusCode.PreconditionFailed);

        // A binding that never proved control cannot be "re-enabled".
        (await client.SendAsync(HttpMethod.Patch, $"/v1/ci-bindings/{binding}", Admin(user, org), new { status = "active" }, NewKey(), "\"1\""))
            .Status.ShouldBe(HttpStatusCode.Conflict);

        (await ExchangeAsync(client, api.Jwks.Mint(RepoId), challenge)).Status.ShouldBe(HttpStatusCode.OK);

        var narrowed = await client.SendAsync(
            HttpMethod.Patch, $"/v1/ci-bindings/{binding}", Admin(user, org), new { allowed_ref_patterns = TagsOnly }, NewKey(), await VersionAsync(binding));
        narrowed.Status.ShouldBe(HttpStatusCode.OK);
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId))).ErrorCode.ShouldBe("binding_mismatch");

        (await client.SendAsync(HttpMethod.Delete, $"/v1/ci-bindings/{binding}", Admin(user, org), null, NewKey())).Status.ShouldBe(HttpStatusCode.NoContent);
        (await client.SendAsync(HttpMethod.Delete, $"/v1/ci-bindings/{binding}", Admin(user, org), null, NewKey())).Status.ShouldBe(HttpStatusCode.NoContent);
        (await ExchangeAsync(client, api.Jwks.Mint(RepoId))).ErrorCode.ShouldBe("binding_disabled");

        (await client.SendAsync(HttpMethod.Patch, $"/v1/ci-bindings/{binding}", Admin(user, org), new { status = "active" }, NewKey(), await VersionAsync(binding)))
            .Status.ShouldBe(HttpStatusCode.OK);

        (await client.SendAsync(HttpMethod.Patch, $"/v1/ci-bindings/{Guid.CreateVersion7()}", Admin(user, org), new { status = "disabled" }, NewKey(), "\"1\""))
            .Status.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- audit and tenancy of the CI tables

    [Fact]
    public async Task CiMutationsAndExchanges_WriteOneAuditRowEach_WithTheRightActor()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var created = await CreateBindingAsync(client, user, org, project);
        var challenge = created.Body.GetProperty("binding_challenge").GetString()!;
        var token = await CreateTokenAsync(client, user, org, project);
        var exchanged = await ExchangeAsync(client, api.Jwks.Mint(RepoId), challenge);
        await ExchangeIngestAsync(client, token.Text("secret")!);

        var rows = await _fixture.ColumnAsync<string>(
            _database!,
            $"select action || '|' || actor_type from public.audit_log where org_id = '{org}' and action in ('ci_binding.create', 'ingest_token.create', 'ci_token.exchange', 'ingest_token.exchange') order by seq");
        rows.ShouldBe(["ci_binding.create|user", "ingest_token.create|user", "ci_token.exchange|ci_producer", "ingest_token.exchange|ci_producer"]);

        // No secret reaches the trail.
        (await _fixture.ScalarAsync<long>(_database!, $"select count(*) from public.audit_log where metadata::text like '%{challenge}%' or metadata::text like '%{token.Text("secret")![^43..]}%'"))
            .ShouldBe(0);
        exchanged.Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OtherOrgsBindingsAndTokens_AreNotFound()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var _ = api;
        var userB = Guid.CreateVersion7();
        var orgB = await CreateOrgAsync(client, userB, "other-org");
        var binding = (await CreateBindingAsync(client, user, org, project)).Body.GetProperty("binding").GetProperty("id").GetGuid();
        var token = (await CreateTokenAsync(client, user, org, project)).Body.GetProperty("token").GetProperty("id").GetGuid();

        (await client.SendAsync(HttpMethod.Delete, $"/v1/ci-bindings/{binding}", Admin(userB, orgB), null, NewKey())).Status.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Delete, $"/v1/ingest-tokens/{token}", Admin(userB, orgB), null, NewKey())).Status.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Post, $"/v1/ingest-tokens/{token}/rotate", Admin(userB, orgB))).Status.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/ci-bindings", Admin(userB, orgB))).Body.GetProperty("items").GetArrayLength().ShouldBe(0);
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

    private static Task<ApiAnswer> CreateBindingAsync(HttpClient client, Guid user, Guid org, Guid project) =>
        client.SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{project}/ci-bindings",
            Admin(user, org),
            new
            {
                provider = "github_actions",
                repository_id = RepoId,
                repository_full_name = "acme/web",
                allowed_ref_patterns = MainAndTags,
                allowed_workflow_refs = new[] { Workflow },
            });

    private static Task<ApiAnswer> CreateTokenAsync(HttpClient client, Guid user, Guid org, Guid project) =>
        client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/ingest-tokens", Admin(user, org), new { name = "gitlab" });

    private static Task<ApiAnswer> ExchangeAsync(HttpClient client, string jwt, string? challenge = null) =>
        client.SendAsync(
            HttpMethod.Post,
            "/v1/ci/token",
            headers => headers.TryAddWithoutValidation("Authorization", "Bearer " + jwt),
            challenge is null ? null : new { binding_challenge = challenge });

    private static Task<ApiAnswer> ExchangeIngestAsync(HttpClient client, string token) =>
        client.SendAsync(HttpMethod.Post, "/v1/ci/token", _ => { }, new { ingest_token = token });

    private async Task<string> VersionAsync(Guid binding) =>
        "\"" + await _fixture.ScalarAsync<int>(_database!, $"select version from public.ci_binding where id = '{binding}'") + "\"";

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
