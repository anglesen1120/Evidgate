using Evidgate.Modules.Catalog.Contracts;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The catalogue facts an evaluation reads (<see cref="ICatalogContracts.GetEvaluationSuiteAsync"/> and
/// <see cref="ICatalogContracts.GetEvaluationCasesAsync"/>) over a real PostgreSQL and the real host, on a caller-owned
/// tenant transaction. Each test states the predicate whose removal makes it fail.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class CatalogEvaluationReadsTests
{
    private readonly PostgresTenancyFixture _fixture;

    public CatalogEvaluationReadsTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
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

    /// <summary>A second tenant through the real routes, for the isolation cases.</summary>
    private static async Task<(Guid Org, Guid Project)> CreateOtherTenantAsync(World world, string slug)
    {
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(world.Client, user, slug);
        var project = await CreateProjectAsync(world.Client, user, org, slug + "-project");

        return (org, project);
    }

    /// <summary>One committed run and its plan for <paramref name="org"/> (the seeder writes as the migration role).</summary>
    private Task<EvaluationPlanSeeder.Plan> SeedPlanAsync(World world, int executions, Guid? org = null) =>
        EvaluationPlanSeeder.SeedAsync(_fixture, world.Database, org ?? world.Org, executions);

    /// <summary>A committed second revision of the plan's suite that declares no member at all.</summary>
    private async Task<Guid> InsertEmptyRevisionAsync(World world, EvaluationPlanSeeder.Plan plan)
    {
        var revisionId = Guid.CreateVersion7();
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            """
            insert into public.suite_revision (id, org_id, suite_id, revision_no, revision_hash, shard_strategy, retry_policy)
            values (@id, @org_id, @suite_id, 2, @hash, '{}', '{}')
            """,
            ("id", revisionId),
            ("org_id", world.Org),
            ("suite_id", plan.SuiteId),
            ("hash", "sha256:" + new string('d', 64)))).ShouldBe(1);
        await owner.CommitAsync();

        return revisionId;
    }

    private Task<Guid> CaseIdOfAsync(World world, Guid caseRevisionId) =>
        _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select case_id from public.test_revision where org_id = '{world.Org}' and id = '{caseRevisionId}'");

    // ------------------------------------------------------------------ GetEvaluationSuiteAsync

    [Fact]
    public async Task GetEvaluationSuiteAsync_ReturnsTheSlugAndWhetherTheRevisionDeclaresNoMember()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);
        var emptyRevision = await InsertEmptyRevisionAsync(world, plan);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var contracts = caller.Resolve<ICatalogContracts>();

        var seeded = await contracts.GetEvaluationSuiteAsync(world.Org, plan.SuiteRevisionId, TestContext.Current.CancellationToken);
        seeded.ShouldNotBeNull();
        seeded.SuiteKey.ShouldBe(plan.SuiteSlug);
        seeded.DeclaredEmpty.ShouldBeFalse();

        // The mutation this pins: dropping the `not exists suite_member` probe answers false for the empty revision too.
        var empty = await contracts.GetEvaluationSuiteAsync(world.Org, emptyRevision, TestContext.Current.CancellationToken);
        empty.ShouldNotBeNull();
        empty.SuiteKey.ShouldBe(plan.SuiteSlug);
        empty.DeclaredEmpty.ShouldBeTrue();

        (await contracts.GetEvaluationSuiteAsync(world.Org, Guid.CreateVersion7(), TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task GetEvaluationSuiteAsync_ReturnsNothingForAnotherTenantsRevision()
    {
        await using var world = await StartAsync();
        var (otherOrg, _) = await CreateOtherTenantAsync(world, "rival");
        var mine = await SeedPlanAsync(world, 1);
        var theirs = await SeedPlanAsync(world, 1, otherOrg);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var contracts = caller.Resolve<ICatalogContracts>();

        // Positive control: the same call for the scope's own revision answers a row.
        (await contracts.GetEvaluationSuiteAsync(world.Org, mine.SuiteRevisionId, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();

        (await contracts.GetEvaluationSuiteAsync(world.Org, theirs.SuiteRevisionId, TestContext.Current.CancellationToken))
            .ShouldBeNull();
        (await contracts.GetEvaluationSuiteAsync(otherOrg, theirs.SuiteRevisionId, TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task GetEvaluationSuiteAsync_ReturnsNothingWhenTheRequestedOrgIsNotTheScopesOrg()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        var (otherOrg, _) = await CreateOtherTenantAsync(world, "rival");

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var contracts = caller.Resolve<ICatalogContracts>();

        // The mutation this pins: without `sr.org_id = @org_id` the statement returns the scope's own revision even
        // though the caller asked for another tenant, because row level security alone still admits that row.
        (await contracts.GetEvaluationSuiteAsync(otherOrg, plan.SuiteRevisionId, TestContext.Current.CancellationToken))
            .ShouldBeNull();
        (await contracts.GetEvaluationSuiteAsync(world.Org, plan.SuiteRevisionId, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
    }

    // ------------------------------------------------------------------ GetEvaluationCasesAsync

    [Fact]
    public async Task GetEvaluationCasesAsync_MapsEachNamedRevisionToItsCaseIdAndName()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 3);
        var first = plan.Executions[0].CaseRevisionId;
        var second = plan.Executions[1].CaseRevisionId;

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var contracts = caller.Resolve<ICatalogContracts>();

        var cases = await contracts.GetEvaluationCasesAsync(
            world.Org,
            [first, second, Guid.CreateVersion7()],
            TestContext.Current.CancellationToken);

        cases.Count.ShouldBe(2);
        cases[first].CaseId.ShouldBe(await CaseIdOfAsync(world, first));
        cases[second].CaseId.ShouldBe(await CaseIdOfAsync(world, second));
        cases[first].CaseKey.ShouldBe("case-0");
        cases[second].CaseKey.ShouldBe("case-1");
    }

    [Fact]
    public async Task GetEvaluationCasesAsync_ReturnsNothingForAnotherTenantsRevisions()
    {
        await using var world = await StartAsync();
        var (otherOrg, _) = await CreateOtherTenantAsync(world, "rival");
        var mine = await SeedPlanAsync(world, 1);
        var theirs = await SeedPlanAsync(world, 1, otherOrg);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var contracts = caller.Resolve<ICatalogContracts>();

        var own = await contracts.GetEvaluationCasesAsync(
            world.Org, [mine.Executions[0].CaseRevisionId], TestContext.Current.CancellationToken);
        own.Count.ShouldBe(1);

        var foreign = await contracts.GetEvaluationCasesAsync(
            world.Org, [theirs.Executions[0].CaseRevisionId], TestContext.Current.CancellationToken);
        foreign.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetEvaluationCasesAsync_ReturnsNothingWhenTheRequestedOrgIsNotTheScopesOrg()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        var (otherOrg, _) = await CreateOtherTenantAsync(world, "rival");

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var contracts = caller.Resolve<ICatalogContracts>();

        // The mutation this pins: dropping `r.org_id = @org_id` answers the scope's own revision for a foreign org.
        (await contracts.GetEvaluationCasesAsync(
            otherOrg, [plan.Executions[0].CaseRevisionId], TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await contracts.GetEvaluationCasesAsync(
            world.Org, [plan.Executions[0].CaseRevisionId], TestContext.Current.CancellationToken)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetEvaluationCasesAsync_EmptyBatchMakesNoRoundTrip()
    {
        await using var world = await StartAsync();
        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var contracts = caller.Resolve<ICatalogContracts>();

        world.Api.Statements.Clear();
        var cases = await contracts.GetEvaluationCasesAsync(world.Org, [], TestContext.Current.CancellationToken);

        cases.ShouldBeEmpty();
        world.Api.Statements.Snapshot().ShouldNotContain(
            statement => statement.Contains("from public.test_revision", StringComparison.Ordinal));
    }
}
