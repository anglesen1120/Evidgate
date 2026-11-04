// The batch read behind a run-list page: newest live verdict per run, in one statement, never a shadow row.
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <see cref="IPolicyContracts.GetLatestLiveDecisionsAsync"/> against a real PostgreSQL: the verdict of each named
/// run's newest <c>live</c> evaluation, read for a whole page of runs in one statement, absent for a run without one
/// and never a <c>shadow</c> row. The read is matched on the caller's org on top of RLS, so another tenant's run is
/// invisible even when its id is named.
/// </summary>
public sealed partial class PolicyContractsTests
{
    /// <summary>The statement's own table, which the statement-budget assertions count.</summary>
    private const string EvaluationTable = "public.evaluation";

    [Fact]
    public async Task LatestLiveDecisions_ReturnTheNewestLiveVerdictOfEachRun()
    {
        await using var world = await StartAsync();
        var (revision, hash) = await CreateRevisionAsync(world, """{"flaky_budget":3}""");
        var older = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, 1);
        var single = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, 1);
        var shadowOnly = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, 1);

        // The later revision wins, and the shadow row at an even higher revision is never the run's verdict.
        await InsertEvaluationAsync(world, older.RunId, revision, hash, 1, "live", "FAIL");
        var newest = await InsertEvaluationAsync(world, older.RunId, revision, hash, 2, "live", "PASS");
        await InsertEvaluationAsync(world, older.RunId, revision, hash, 3, "shadow", "INCONCLUSIVE");
        await InsertEvaluationAsync(world, single.RunId, revision, hash, 1, "live", "INCONCLUSIVE");
        await InsertEvaluationAsync(world, shadowOnly.RunId, revision, hash, 1, "shadow", "PASS");

        world.Api.Statements.Clear();
        var found = await ReadDecisionsAsync(world, [older.RunId, single.RunId, shadowOnly.RunId]);

        found.Count.ShouldBe(2);
        var byRun = found.ToDictionary(item => item.RunId);
        byRun[older.RunId].Decision.ShouldBe("PASS");
        byRun[older.RunId].EvidenceRevision.ShouldBe(2, "the highest live evidence revision is the newest verdict");
        byRun[older.RunId].EvaluationId.ShouldBe(newest);
        byRun[single.RunId].Decision.ShouldBe("INCONCLUSIVE");
        byRun.ShouldNotContainKey(shadowOnly.RunId, "a shadow row is a second opinion, never a run's decision");
        EvaluationStatements(world).ShouldBe(1, "one page of runs is one statement, never one per run");
    }

    /// <summary>The read is bound to the caller's tenant: another org's run is invisible, and the owning org still reads it.</summary>
    [Fact]
    public async Task LatestLiveDecisions_AreEmptyForAnotherTenantsRun()
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

        var mineReading = await ReadDecisionsAsync(world, [mine.RunId, theirs.RunId]);
        mineReading.ShouldHaveSingleItem().RunId.ShouldBe(mine.RunId);

        // The owning tenant reads its own verdict when asked for the same pair, so the absence is the tenant boundary.
        var theirsReading = await ReadDecisionsAsync(other, [mine.RunId, theirs.RunId]);
        theirsReading.ShouldHaveSingleItem().RunId.ShouldBe(theirs.RunId);
    }

    /// <summary>No id names no run and costs no statement; more than one page of ids is a caller bug, not a query.</summary>
    [Fact]
    public async Task LatestLiveDecisions_AreEmptyWithoutIds_AndRefuseMoreThanOnePage()
    {
        await using var world = await StartAsync();

        world.Api.Statements.Clear();
        (await ReadDecisionsAsync(world, [])).ShouldBeEmpty();
        EvaluationStatements(world).ShouldBe(0, "an empty request never reaches the database");

        var tooMany = new List<Guid>(201);
        for (var i = 0; i < 201; i++)
        {
            tooMany.Add(Guid.CreateVersion7());
        }

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await ReadDecisionsAsync(world, tooMany));
    }

    /// <summary>Reads the batch verdicts through the module's registered contracts surface, inside a tenant scope.</summary>
    private static async Task<IReadOnlyList<RunLiveDecision>> ReadDecisionsAsync(World world, IReadOnlyList<Guid> runIds)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var contracts = services.ServiceProvider.GetRequiredService<IPolicyContracts>();

        var found = await contracts.GetLatestLiveDecisionsAsync(runIds, TestContext.Current.CancellationToken);
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        return found;
    }

    /// <summary>The statements the read ran over the evaluation table since the last clear.</summary>
    private static int EvaluationStatements(World world) =>
        world.Api.Statements.Snapshot().Count(statement => statement.Contains(EvaluationTable, StringComparison.Ordinal));
}
