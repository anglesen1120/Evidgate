using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

// The read's cost and its refusal: one statement whatever the attempt count, and none at all without a tenant scope.
public sealed partial class FrozenEvidenceTests
{
    [Fact]
    public async Task GetFrozenInput_WithoutATenantScopeRefusesBeforeRunningSql()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        await SeedRevisionAsync(world, plan.RunId, 1, Base.AddMinutes(10));

        // A container scope that published no tenant scope: the read must fail closed instead of running unscoped SQL,
        // where zero rows would read as "this tenant has no such revision".
        await using var services = world.Api.Services.CreateAsyncScope();
        var evidence = services.ServiceProvider.GetRequiredService<IFrozenEvidence>();
        world.Api.Statements.Clear();

        var missing = await Should.ThrowAsync<TenantScopeMissingException>(async () =>
            await evidence.GetFrozenInputAsync(world.Org, plan.RunId, 1, TestContext.Current.CancellationToken));
        missing.Message.ShouldContain("tenant scope");
        world.Api.Statements.Snapshot().ShouldBeEmpty();

        // Positive control: the same call inside a real tenant scope answers the revision.
        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        (await caller.Resolve<IFrozenEvidence>().GetFrozenInputAsync(
            world.Org, plan.RunId, 1, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task GetFrozenInput_AddsOneStatementWhateverTheAttemptCount()
    {
        await using var world = await StartAsync();
        var narrow = await SeedPlanAsync(world, 1);
        var wide = await SeedPlanAsync(world, 12);
        var freeze = Base.AddMinutes(10);
        await SeedRevisionAsync(world, narrow.RunId, 1, freeze);
        await SeedRevisionAsync(world, wide.RunId, 1, freeze);
        await SeedAttemptAsync(world, narrow.RunId, narrow.Executions[0], 1, "passed", Base);
        foreach (var execution in wide.Executions)
        {
            for (var attemptNo = 1; attemptNo <= 3; attemptNo++)
            {
                await SeedAttemptAsync(world, wide.RunId, execution, attemptNo, "passed", Base.AddMinutes(attemptNo));
            }
        }

        await using var caller = await ContractScope.BeginAsync(world.Api, world.Org);
        var evidence = caller.Resolve<IFrozenEvidence>();

        world.Api.Statements.Clear();
        var narrowInput = await evidence.GetFrozenInputAsync(
            world.Org, narrow.RunId, 1, TestContext.Current.CancellationToken);
        var narrowStatements = world.Api.Statements.Snapshot().Count;

        world.Api.Statements.Clear();
        var wideInput = await evidence.GetFrozenInputAsync(
            world.Org, wide.RunId, 1, TestContext.Current.CancellationToken);
        var wideStatements = world.Api.Statements.Snapshot().Count;

        narrowInput.ShouldNotBeNull();
        narrowInput.Attempts.Count.ShouldBe(1);
        wideInput.ShouldNotBeNull();
        wideInput.Attempts.Count.ShouldBe(12);

        // The mutation this pins: reading the header and then the attempts in separate round trips, or joining the
        // attempts in the application, makes the count follow the attempt count instead of the revision.
        narrowStatements.ShouldBe(1, "the frozen input is one statement whatever the attempt count");
        wideStatements.ShouldBe(narrowStatements);
    }
}
