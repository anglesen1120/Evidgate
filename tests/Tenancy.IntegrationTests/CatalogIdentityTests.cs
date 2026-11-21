using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 03.1.1 AC1–AC7 through the real host and a real PostgreSQL: the registration service (rename, conflicts, warnings,
/// missing), the database-level immutability, RLS, and the catalog routes with their 00 §9 envelope.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class CatalogIdentityTests(PostgresTenancyFixture fixture, CatalogIdentityDatabase database)
    : IClassFixture<CatalogIdentityDatabase>
{
    private const string IdA = "018f2f0e-8a52-7c4b-9d3e-5a1b2c3d4e5f";
    private const string IdB = "018f2f0e-8a52-7c4b-9d3e-5a1b2c3d4e60";
    private const string IdC = "018f2f0e-8a52-7c4b-9d3e-5a1b2c3d4e61";
    private const string Commit1 = "aaaaaaa1111111";
    private const string Commit2 = "bbbbbbb2222222";
    private const string Commit3 = "ccccccc3333333";

    private static string[] AppendOnlyViolations(World world) =>
    [
        $"delete from public.test_case where org_id = '{world.Org}'",
        $"update public.case_identity_source set raw_key = 'x' where org_id = '{world.Org}'",
        $"delete from public.case_identity_source where org_id = '{world.Org}'",
        $"update public.case_rename_history set to_path = 'x' where org_id = '{world.Org}'",
        $"delete from public.case_rename_history where org_id = '{world.Org}'",
    ];

    private static readonly string[] DuplicateLabels = ["smoke", "smoke", " p1 "];
    private static readonly int[] NotAnObject = [1, 2];

    private readonly PostgresTenancyFixture _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project) : IAsyncDisposable
    {
        public Action<System.Net.Http.Headers.HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // The class shares one database and host, so a test isolates itself with a fresh org and project.
    private async Task<World> StartAsync()
    {
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(database.Client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(database.Client, user, org, "checkout");

        return new World(database.Api, database.Client, database.Name, user, org, project);
    }

    private static SourceFile Annotated(string path, string id) => new(path, $"// evidgate.id: {id}\ntest('x', () => {{}});", "playwright", "typescript");

    private static async Task<CaseSyncReport> RegisterAsync(World world, string commit, params SourceFile[] files) =>
        await RegisterAsync(world.Api, world.Org, world.Project, commit, files);

    private static async Task<CaseSyncReport> RegisterAsync(TenancyApiFactory api, Guid org, Guid project, string commit, params SourceFile[] files)
    {
        await using var services = api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(org, null, false, TestContext.Current.CancellationToken);
        var outcome = await services.ServiceProvider.GetRequiredService<ICaseIdentityService>()
            .RegisterDiscoveredAsync(project, commit, files, TestContext.Current.CancellationToken);
        outcome.Succeeded.ShouldBeTrue(outcome.Failure);
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        return outcome.Report!;
    }

    private Task<long> CountAsync(World world, string table) =>
        _fixture.ScalarAsync<long>(world.Database, Scoped(world, $"select count(*) from public.{table}"));

    // This class's database is shared by its tests, so every raw statement names the org it may touch.
    private static string Scoped(World world, string sql, string? and = null) => Scoped(world.Org, sql, and);

    private static string Scoped(Guid org, string sql, string? and = null) =>
        $"{sql} where org_id = '{org}'" + (and is null ? string.Empty : $" and {and}");
}
