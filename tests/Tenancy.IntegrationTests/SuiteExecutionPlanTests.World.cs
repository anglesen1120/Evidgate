using System.Net;
using System.Net.Http.Headers;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The world of <see cref="SuiteExecutionPlanTests"/>: one dedicated migrated database, the host with the real resolver
/// and the CI token scheme, and the seeding, minting and reading helpers the facts share.
/// </summary>
public sealed partial class SuiteExecutionPlanTests : IAsyncLifetime
{
    private const string Commit1 = "aaaaaaa1111111";

    private string? _database;
    private DedicatedDatabase? _dedicated;

    public async ValueTask InitializeAsync()
    {
        _dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        _database = _dedicated.Name;
    }

    public async ValueTask DisposeAsync()
    {
        if (_dedicated is not null)
        {
            await _dedicated.DisposeAsync();
        }
    }

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        Guid Suite,
        Guid Revision,
        Guid[] CaseRevisions) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>An org with a project, <paramref name="members"/> cases and a suite with one revision of them.</summary>
    private async Task<World> StartAsync(string? projectSlug = null, int members = 6)
    {
        var api = TenancyApiFactory.Ci(_fixture.AppConnectionString(_database!));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug(projectSlug ?? "checkout"));
        var (suite, revision, caseRevisions) = await CreateSuiteRevisionAsync(
            api, client, user, org, project, projectSlug ?? "suite", members);

        return new World(api, client, _database!, user, org, project, suite, revision, caseRevisions);
    }

    private async Task<(Guid Suite, Guid Revision, Guid[] CaseRevisions)> CreateSuiteRevisionAsync(
        TenancyApiFactory api, HttpClient client, Guid user, Guid org, Guid project, string slug, int members)
    {
        var caseRevisions = members == 0 ? [] : await SeedCasesAsync(api, org, project, members);
        var suite = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{project}/suites",
            TenancyApiFactory.Resolved(user: user, orgHeader: org),
            new { name = slug, slug = Unique.Slug(slug) },
            NewKey());
        suite.Status.ShouldBe(HttpStatusCode.Created, suite.ErrorMessage);

        var revision = await CreateRevisionAsync(client, user, org, suite.Id(), caseRevisions);
        return (suite.Id(), revision, caseRevisions);
    }

    private static Task<Guid> CreateRevisionAsync(World world, Guid suite, IReadOnlyList<Guid> caseRevisions) =>
        CreateRevisionAsync(world.Client, world.User, world.Org, suite, caseRevisions);

    private static async Task<Guid> CreateRevisionAsync(
        HttpClient client, Guid user, Guid org, Guid suite, IReadOnlyList<Guid> caseRevisions)
    {
        var created = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/suites/{suite}/revisions",
            TenancyApiFactory.Resolved(user: user, orgHeader: org),
            new { members = caseRevisions.Select(id => (object)new { @ref = (string?)null, case_revision_id = id, @params = (object?)null, required = (bool?)null }).ToArray() },
            NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id("suite_revision_id");
    }

    /// <summary>Registers <paramref name="count"/> cases through the identity service and returns their revisions.</summary>
    private async Task<Guid[]> SeedCasesAsync(TenancyApiFactory api, Guid org, Guid project, int count)
    {
        var files = Enumerable.Range(0, count).Select(i => new SourceFile(
            $"t/{i}.spec.ts",
            $"// evidgate.id: 018f2f0e-8a52-7c4b-9d3e-{i:D12}\ntest('{i}', () => {{ expect(a).toBe({i}); }});",
            "playwright",
            "typescript")).ToArray();

        await using var services = api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(org, null, false, TestContext.Current.CancellationToken);
        var outcome = await services.ServiceProvider.GetRequiredService<ICaseIdentityService>()
            .RegisterDiscoveredAsync(project, Commit1, files, TestContext.Current.CancellationToken);
        outcome.Succeeded.ShouldBeTrue(outcome.Failure);
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        var ids = new Guid[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = await _fixture.ScalarAsync<Guid>(
                _database!,
                $"""
                select r.id from public.test_revision r
                  join public.test_case c on c.org_id = r.org_id and c.id = r.case_id
                 where c.project_id = '{project}' and c.file_path = 't/{i}.spec.ts'
                """);
        }

        return ids;
    }

    /// <summary>Publishes the world's revision through the module contract, as the publish route does.</summary>
    private static async Task PublishAsync(World world)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var contracts = services.ServiceProvider.GetRequiredService<ICatalogContracts>();
        (await contracts.PublishSuiteRevisionAsync(world.Revision, TestContext.Current.CancellationToken)).ShouldBeTrue();
        await scope.CommitAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Mints a <c>ci_producer</c> token through the ingest-token route and the CI exchange, as the CLI does.</summary>
    private static async Task<ApiAnswer> MintAsync(World world, Guid project, string[]? scopes = null)
    {
        var created = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{project}/ingest-tokens",
            world.Admin,
            scopes is null ? new { name = "plan" } : new { name = "plan", scopes },
            NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        var exchanged = await world.Client.SendAsync(
            HttpMethod.Post, "/v1/ci/token", _ => { }, new { ingest_token = created.Text("secret") });
        exchanged.Status.ShouldBe(HttpStatusCode.OK, exchanged.ErrorMessage);

        return exchanged;
    }

    private static Task<ApiAnswer> GetPlanAsync(
        World world, Guid revision, Action<HttpRequestHeaders> caller, string query = "") =>
        world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{revision}/execution-plan{query}", caller);

    private static Action<HttpRequestHeaders> Bearer(string token) => headers =>
        headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    /// <summary>The stored members in <c>(ordinal, id)</c> order, as <c>ordinal|case_id|variant_hash|case_revision_id</c>.</summary>
    private async Task<List<string>> ExpectedAsync(World world, Guid revision) =>
    [
        .. (await _fixture.ColumnAsync<string>(
            world.Database,
            $"""
            select m.ordinal || '|' || tr.case_id || '|' || v.variant_hash || '|' || m.case_revision_id
              from public.suite_member m
              join public.test_revision tr on tr.org_id = m.org_id and tr.id = m.case_revision_id
              join public.variant v on v.org_id = m.org_id and v.id = m.variant_id
             where m.org_id = '{world.Org}' and m.suite_revision_id = '{revision}'
             order by m.ordinal, m.id
            """)),
    ];

    private static string MemberKey(System.Text.Json.JsonElement member) =>
        string.Join(
            '|',
            member.GetProperty("case_id").GetString(),
            member.GetProperty("variant_hash").GetString(),
            member.GetProperty("case_revision_id").GetGuid().ToString("D"));

    /// <summary>The whole error envelope with <c>request_id</c> normalised, which differs per request.</summary>
    private static string ErrorShape(ApiAnswer answer)
    {
        var error = answer.Body.GetProperty("error");
        var requestId = error.GetProperty("request_id").GetString();

        return string.Join(
            '\n',
            "code=" + error.GetProperty("code").GetString(),
            "message=" + error.GetProperty("message").GetString(),
            "retryable=" + error.GetProperty("retryable").GetBoolean(),
            "details=" + error.GetProperty("details").GetRawText(),
            "request_id=" + (string.IsNullOrEmpty(requestId) ? "<absent>" : "<set>"));
    }
}
