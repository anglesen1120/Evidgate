using Evidgate.Modules.Evidence.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

// The freeze's edge: an attempt ordering that contradicts the clock, a retry recorded under another run of the
// same tenant, and a revision whose executions were all attempted after it froze.
public sealed partial class FrozenEvidenceTests
{
    [Fact]
    public async Task GetFrozenInput_PrefersTheHigherAttemptNumberOverALaterCreatedAt()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        var freeze = Base.AddMinutes(10);
        await SeedRevisionAsync(world, plan.RunId, 1, freeze);
        await SeedAttemptAsync(world, plan.RunId, plan.Executions[0], 1, "failed", Base.AddMinutes(5));
        await SeedAttemptAsync(world, plan.RunId, plan.Executions[0], 2, "passed", Base);

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var evidence = caller.Resolve<IFrozenEvidence>();

        // The mutation this pins: ordering the admitted attempts by `created_at desc` instead of `attempt_no desc`
        // answers `failed`, because the first attempt happened to be written later than its retry.
        var input = await evidence.GetFrozenInputAsync(
            world.Org, plan.RunId, 1, TestContext.Current.CancellationToken);
        input.ShouldNotBeNull();

        input.Attempts.Select(row => row.Status).ShouldBe(["passed"]);
    }

    [Fact]
    public async Task GetFrozenInput_ReturnsTheRequestedRunsStatusWhenASiblingRunRetriedTheSameExecutionKey()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        var execution = plan.Executions[0];
        var freeze = Base.AddMinutes(10);
        await SeedRevisionAsync(world, plan.RunId, 1, freeze);
        await SeedAttemptAsync(world, plan.RunId, execution, 1, "failed", Base);

        // A second run of the same tenant plans an execution under that same key, so the sibling's retry passes the
        // attempt table's own foreign key and is admissible by the clock alone.
        var sibling = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);
        await RunRowSeeder.PlanExecutionAsync(_fixture, world.Database, world.Org, sibling, execution.ExecutionKey);
        await SeedAttemptAsync(world, sibling, execution, 2, "passed", Base.AddMinutes(5));

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var evidence = caller.Resolve<IFrozenEvidence>();

        // The mutation this pins: dropping `a.run_id = er.run_id` lets the sibling run's later retry win and the
        // requested run reads `passed` instead of its own `failed`.
        var input = await evidence.GetFrozenInputAsync(
            world.Org, plan.RunId, 1, TestContext.Current.CancellationToken);
        input.ShouldNotBeNull();

        input.Attempts.Select(row => row.Status).ShouldBe(["failed"]);
    }

    [Fact]
    public async Task GetFrozenInput_KeepsTheRevisionHeaderWhenEveryAttemptPostdatesTheFreeze()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);
        var freeze = Base.AddMinutes(10);
        await SeedRevisionAsync(world, plan.RunId, 1, freeze, "complete", [], manifestHash: "sha256:" + new string('c', 64));
        await SeedAttemptAsync(world, plan.RunId, plan.Executions[0], 1, "passed", freeze.AddMinutes(10));
        await SeedAttemptAsync(world, plan.RunId, plan.Executions[1], 1, "failed", freeze.AddMinutes(20));

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var evidence = caller.Resolve<IFrozenEvidence>();

        // The mutation this pins: an inner join to the admitted attempts answers null, and a dropped
        // `a.created_at <= er.created_at` admits both post-freeze attempts.
        var input = await evidence.GetFrozenInputAsync(
            world.Org, plan.RunId, 1, TestContext.Current.CancellationToken);
        input.ShouldNotBeNull();
        input.Revision.Completeness.ShouldBe("complete");
        input.Revision.Missing.ShouldBeEmpty();
        input.Revision.ManifestHash.ShouldBe("sha256:" + new string('c', 64));
        input.Attempts.ShouldBeEmpty();
    }
}
