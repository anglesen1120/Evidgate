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
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await owner.ExecuteAsync(
                """
                insert into public.case_attempt
                  (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status, created_at)
                select gen_random_uuid(), pe.org_id, pe.run_id, pe.execution_key, tr.case_id, n, 'passed', 'passed', @stamp
                  from public.planned_execution pe
                  join public.test_revision tr on tr.org_id = pe.org_id and tr.id = pe.case_revision_id
                  cross join generate_series(1, 5) n
                 where pe.org_id = @org_id and pe.run_id = @run_id
                """,
                ("org_id", world.Org), ("run_id", wide.RunId), ("stamp", freeze))).ShouldBe(60);
            await owner.CommitAsync();
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
        narrowInput.AttemptHistory.Count.ShouldBe(1);
        wideInput.ShouldNotBeNull();
        wideInput.Attempts.Count.ShouldBe(12);
        wideInput.AttemptHistory.Count.ShouldBe(60);

        // The mutation this pins: reading the header and then the attempts in separate round trips, or joining the
        // attempts in the application, makes the count follow the attempt count instead of the revision.
        narrowStatements.ShouldBe(1, "the frozen input is one statement whatever the attempt count");
        wideStatements.ShouldBe(narrowStatements);
    }
}
