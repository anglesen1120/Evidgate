// The aggregate behind the dashboard counts: the newest live verdict of each run, counted per decision in one statement.
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <see cref="IPolicyContracts.CountLatestLiveDecisionsAsync"/> against a real PostgreSQL: each run counts once under
/// its newest <c>live</c> evaluation, a <c>shadow</c> row and a run without a verdict count nowhere, the read is one
/// statement per chunk, and another tenant's run is not counted even when its id is named.
/// </summary>
public sealed partial class PolicyContractsTests
{
    [Fact]
    public async Task LatestLiveDecisionCounts_CountEachRunOnceUnderItsNewestLiveVerdict()
    {
        await using var world = await StartAsync();
        var (revision, hash) = await CreateRevisionAsync(world, """{"flaky_budget":3}""");
        var superseded = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, 1);
        var failing = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, 1);
        var inconclusive = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, 1);
        var shadowOnly = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, 1);
        var unevaluated = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, 1);

        // FAIL at revision 1 is superseded by PASS at revision 2; a shadow row at revision 3 never wins.
        await InsertEvaluationAsync(world, superseded.RunId, revision, hash, 1, "live", "FAIL");
        await InsertEvaluationAsync(world, superseded.RunId, revision, hash, 2, "live", "PASS");
        await InsertEvaluationAsync(world, superseded.RunId, revision, hash, 3, "shadow", "INCONCLUSIVE");
        await InsertEvaluationAsync(world, failing.RunId, revision, hash, 1, "live", "FAIL");
        await InsertEvaluationAsync(world, inconclusive.RunId, revision, hash, 1, "live", "INCONCLUSIVE");
        await InsertEvaluationAsync(world, shadowOnly.RunId, revision, hash, 1, "shadow", "PASS");

        world.Api.Statements.Clear();
        var counts = await CountDecisionsAsync(
            world,
            [superseded.RunId, failing.RunId, inconclusive.RunId, shadowOnly.RunId, unevaluated.RunId]);

        counts.ShouldBe(new LiveDecisionCounts(Pass: 1, Fail: 1, Inconclusive: 1));
        counts.Evaluated.ShouldBe(3);
        EvaluationStatements(world).ShouldBe(1, "one chunk of runs is one statement, never one per run");
    }

    [Fact]
    public async Task LatestLiveDecisionCounts_DoNotCountAnotherTenantsRun()
    {
        await using var world = await StartAsync();
        var (revision, hash) = await CreateRevisionAsync(world, """{"flaky_budget":3}""");
        var mine = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, 1);
        await InsertEvaluationAsync(world, mine.RunId, revision, hash, 1, "live", "PASS");
        var otherOrg = await CreateOrgAsync(world.Client, world.User, Unique.Slug("rival"));
        var otherProject = await CreateProjectAsync(world.Client, world.User, otherOrg, Unique.Slug("checkout"));
        var other = world with { Org = otherOrg, Project = otherProject };
        var (theirRevision, theirHash) = await CreateRevisionAsync(other, """{"flaky_budget":3}""");
        var theirs = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, otherOrg, 1);
        await InsertEvaluationAsync(other, theirs.RunId, theirRevision, theirHash, 1, "live", "FAIL");

        (await CountDecisionsAsync(world, [mine.RunId, theirs.RunId])).ShouldBe(new LiveDecisionCounts(1, 0, 0));
        (await CountDecisionsAsync(other, [mine.RunId, theirs.RunId])).ShouldBe(new LiveDecisionCounts(0, 1, 0));
    }

    [Fact]
    public async Task LatestLiveDecisionCounts_AreZeroWithoutIds_AndRefuseMoreThanOneChunk()
    {
        await using var world = await StartAsync();

        world.Api.Statements.Clear();
        (await CountDecisionsAsync(world, [])).ShouldBe(new LiveDecisionCounts(0, 0, 0));
        EvaluationStatements(world).ShouldBe(0, "an empty request never reaches the database");

        var tooMany = Enumerable.Range(0, 201).Select(_ => Guid.CreateVersion7()).ToList();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await CountDecisionsAsync(world, tooMany));
    }

    private static async Task<LiveDecisionCounts> CountDecisionsAsync(World world, IReadOnlyList<Guid> runIds)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var contracts = services.ServiceProvider.GetRequiredService<IPolicyContracts>();

        var counts = await contracts.CountLatestLiveDecisionsAsync(runIds, TestContext.Current.CancellationToken);
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        return counts;
    }
}
