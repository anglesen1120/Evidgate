// Request bodies are written inline so each test reads as the request it sends; the arrays are tiny and built once per test.
#pragma warning disable CA1861
using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.1-T1.3: the two additive catalogue reads run creation needs — the case id behind each planned case revision
/// (<see cref="ICatalogContracts.GetCaseIdsAsync"/>) and the variant hash of each planned execution's parameters
/// (<see cref="ICatalogContracts.NormalizeVariantHashes"/>) — over a real PostgreSQL and the real host.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class CatalogRunPlanningReadsTests
{
    private const string Commit1 = "aaaaaaa1111111";

    private readonly PostgresTenancyFixture _fixture;

    public CatalogRunPlanningReadsTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project, Guid Suite, Guid[] Revisions)
        : IAsyncDisposable
    {
        public Action<System.Net.Http.Headers.HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>An org with a project, <paramref name="cases"/> registered cases (one revision each) and an empty suite.</summary>
    private async Task<World> StartAsync(int cases = 4)
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var revisions = await SeedCasesAsync(api, org, project, database, cases, 0);

        var admin = TenancyApiFactory.Resolved(user: user, orgHeader: org);
        var suite = await client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/suites", admin, new { name = "Smoke", slug = "smoke" }, NewKey());
        suite.Status.ShouldBe(HttpStatusCode.Created, suite.ErrorMessage);

        return new World(api, client, database, user, org, project, suite.Id(), revisions);
    }

    private async Task<Guid[]> SeedCasesAsync(TenancyApiFactory api, Guid org, Guid project, string database, int count, int offset)
    {
        var files = Enumerable.Range(offset, count).Select(i => new SourceFile(
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

        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            ids.Add(await _fixture.ScalarAsync<Guid>(
                database,
                $"select r.id from public.test_revision r join public.test_case c on c.org_id = r.org_id and c.id = r.case_id where c.project_id = '{project}' and c.file_path = 't/{offset + i}.spec.ts'"));
        }

        return [.. ids];
    }

    private static Task<ApiAnswer> CreateRevisionAsync(World world, object body) =>
        world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{world.Suite}/revisions", world.Admin, body, NewKey());

    private static object Member(Guid revision, object? parameters = null, string? reference = null) =>
        new { @ref = reference, case_revision_id = revision, @params = parameters };

    private Task<Guid> CaseIdOfAsync(World world, Guid revisionId) =>
        _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select c.id from public.test_revision r join public.test_case c on c.org_id = r.org_id and c.id = r.case_id where r.id = '{revisionId}'");

    // ------------------------------------------------------------------ NormalizeVariantHashes

    [Fact]
    public async Task NormalizeVariantHashes_MatchesTheSuiteMemberHash_RegardlessOfKeyOrder()
    {
        await using var world = await StartAsync();
        var created = await CreateRevisionAsync(world, new
        {
            members = new[] { Member(world.Revisions[0], new { browser = "chromium", retries = "1" }, "login") },
        });
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        // What the suite-member path stored for that variant (03.2.1-T2.1).
        var stored = await _fixture.ScalarAsync<string>(world.Database, "select variant_hash from public.variant");

        await using var services = world.Api.Services.CreateAsyncScope();
        var contracts = services.ServiceProvider.GetRequiredService<ICatalogContracts>();

        using var document = JsonDocument.Parse("""[{"retries":"1","browser":"chromium"},{"browser":"chromium","retries":"1"}]""");
        var hashes = contracts.NormalizeVariantHashes([.. document.RootElement.EnumerateArray()]);

        hashes.Count.ShouldBe(2);
        hashes[0].ShouldBe(stored);
        hashes[1].ShouldBe(stored);
    }

    [Fact]
    public async Task NormalizeVariantHashes_RefusesSecretShapedAndNestedParams_AndNonObjects()
    {
        await using var world = await StartAsync();
        await using var services = world.Api.Services.CreateAsyncScope();
        var contracts = services.ServiceProvider.GetRequiredService<ICatalogContracts>();

        using var document = JsonDocument.Parse(
            """[{"api_token":"abc"},{"nested":{"a":"b"}},{"browser":"chromium"},"not-an-object",null]""");
        var hashes = contracts.NormalizeVariantHashes([.. document.RootElement.EnumerateArray()]);

        hashes.Count.ShouldBe(5);
        hashes[0].ShouldBeNull();   // secret-shaped key
        hashes[1].ShouldBeNull();   // nested object
        hashes[2].ShouldNotBeNull(); // a plain variant still hashes
        hashes[3].ShouldBeNull();   // a string element is not a variant object
        hashes[4].ShouldBeNull();   // a null element is not a variant object
    }

    // ------------------------------------------------------------------ GetCaseIdsAsync

    [Fact]
    public async Task GetCaseIdsAsync_ReturnsOnlyThisProjectsRevisions_AndMapsToTheCase()
    {
        await using var world = await StartAsync();

        // A second project in the same org, and a second org: neither revision may appear.
        var siblingProject = await CreateProjectAsync(world.Client, world.User, world.Org, "billing");
        var siblingRevisions = await SeedCasesAsync(world.Api, world.Org, siblingProject, world.Database, 1, 0);
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");
        var otherRevisions = await SeedCasesAsync(world.Api, otherOrg, otherProject, world.Database, 1, 0);

        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var contracts = services.ServiceProvider.GetRequiredService<ICatalogContracts>();

        var mine = await contracts.GetCaseIdsAsync(
            world.Project,
            [world.Revisions[0], world.Revisions[1], siblingRevisions[0], otherRevisions[0]],
            TestContext.Current.CancellationToken);

        mine.Count.ShouldBe(2);
        mine.ContainsKey(world.Revisions[0]).ShouldBeTrue();
        mine.ContainsKey(world.Revisions[1]).ShouldBeTrue();
        mine.ContainsKey(siblingRevisions[0]).ShouldBeFalse();
        mine.ContainsKey(otherRevisions[0]).ShouldBeFalse();
        mine[world.Revisions[0]].ShouldBe(await CaseIdOfAsync(world, world.Revisions[0]));
        mine[world.Revisions[1]].ShouldBe(await CaseIdOfAsync(world, world.Revisions[1]));

        // Empty input: an empty answer, no round trip.
        world.Api.Statements.Clear();
        (await contracts.GetCaseIdsAsync(world.Project, [], TestContext.Current.CancellationToken)).Count.ShouldBe(0);
        world.Api.Statements.Snapshot().ShouldNotContain(
            statement => statement.Contains("from public.test_revision", StringComparison.Ordinal));

        await scope.CommitAsync(TestContext.Current.CancellationToken);
    }
}
