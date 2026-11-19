// The decision poll's statement set: one bounded run-header read and one evaluation read, whatever the run planned.
using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// GET /v1/runs/{r}/decision's statement set: the run header, then the newest evaluation, and nothing that grows with
/// the plan the run froze.
/// </summary>
public sealed partial class RunDecisionApiTests
{
    [Fact]
    public async Task PlannedExecutionCount_DoesNotChangeTheStatementSet()
    {
        await using var world = await StartAsync();
        var emptyRun = await SeedRunAsync(world);
        var manyRun = (await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, 25)).RunId;
        await DropPlannedExecutionsAsync(world, emptyRun);
        var policy = await SeedPolicyRevisionAsync(world);
        var emptyEvaluation = await InsertEvaluationAsync(world, emptyRun, policy, new Verdict(1, "live", "PASS"));
        var manyEvaluation = await InsertEvaluationAsync(world, manyRun, policy, new Verdict(1, "live", "PASS"));

        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(world.Database));
        using var client = api.CreateClient();
        var caller = TenancyApiFactory.Caller(org: world.Org, project: world.Project, user: world.User);

        api.Statements.Clear();
        var first = await GetAsync(client, emptyRun, caller);
        var emptyStatements = WorkStatements(api.Statements.Snapshot());

        api.Statements.Clear();
        var second = await GetAsync(client, manyRun, caller);
        var manyStatements = WorkStatements(api.Statements.Snapshot());

        first.Status.ShouldBe(HttpStatusCode.OK);
        first.Id("evaluation_id").ShouldBe(emptyEvaluation);
        second.Status.ShouldBe(HttpStatusCode.OK);
        second.Id("evaluation_id").ShouldBe(manyEvaluation);

        // The mutation this pins: reading the plan (its manifest and planned executions) on the poll, which adds a
        // table the header lookup never touches and makes the statement set depend on the plan's size.
        var leaks = emptyStatements
            .Where(row => row.Contains("planned_execution", StringComparison.Ordinal)
                || row.Contains("public.manifest", StringComparison.Ordinal))
            .ToList();
        emptyStatements.Count.ShouldBe(2, Join(emptyStatements));
        emptyStatements[0].ShouldContain("from public.run r");
        emptyStatements[1].ShouldContain("from public.evaluation e");
        leaks.ShouldBeEmpty();
        manyStatements.ShouldBe(emptyStatements, "the size of the plan never changes the statement set");
    }

    /// <summary>
    /// The pending branch costs the same two statements for an empty and a large plan: a fallback to the full plan read
    /// only when no evaluation exists would add the plan tables here.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoEvaluation_UsesTheHeaderReadOnlyWhateverThePlanSize(bool many)
    {
        await using var world = await StartAsync();
        var runId = many
            ? (await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, 25)).RunId
            : await SeedRunAsync(world);
        if (!many)
        {
            await DropPlannedExecutionsAsync(world, runId);
        }

        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(world.Database));
        using var client = api.CreateClient();
        var caller = TenancyApiFactory.Caller(org: world.Org, project: world.Project, user: world.User);

        api.Statements.Clear();
        var answer = await GetAsync(client, runId, caller);
        var statements = WorkStatements(api.Statements.Snapshot());

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Text("status").ShouldBe("pending");
        statements.Count.ShouldBe(2, Join(statements));
        statements[0].ShouldContain("from public.run r");
        statements[1].ShouldContain("from public.evaluation e");
        statements.ShouldAllBe(row => !row.Contains("planned_execution") && !row.Contains("public.manifest"));
    }

    /// <summary>
    /// Empties one seeded run's plan as the owner: the run keeps its manifest, and the poll must not read either the
    /// manifest or the executions the delete removed.
    /// </summary>
    private async Task DropPlannedExecutionsAsync(World world, Guid runId)
    {
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await owner.ExecuteAsync(
            "delete from public.planned_execution where org_id = @org_id and run_id = @run_id",
            ("org_id", world.Org),
            ("run_id", runId))).ShouldBe(1);
        await owner.CommitAsync();
    }
}
