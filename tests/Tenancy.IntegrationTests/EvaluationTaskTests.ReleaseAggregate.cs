// The release-aggregate trigger of the evaluation insert: a live verdict writes its outbox row in the same
// transaction, a shadow verdict and a rolled-back attempt write none, and the task handler turns the row into the
// aggregate of every release the run is bound to.
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Application.Evaluation;
using Evidgate.Modules.Policy.Application.Releases;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvaluationTaskTests
{
    private Task<long> AggregateOutboxCountAsync(RunCreationTests.World world) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where org_id = '{world.Org}' and task_type = 'policy.release_aggregate'");

    [Fact]
    public async Task OnEvaluationCreated_PersistsRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var seed = await SeedAsync(world);
        var cancellationToken = TestContext.Current.CancellationToken;
        // The seeded run carries no digests: record one and bind the run to a release declaring the same.
        await _fixture.ExecuteAsync(
            world.Database,
            "set session_replication_role = replica; "
            + $"update public.run set components = '{{\"web\":\"sha256:{new string('a', 64)}\"}}'::jsonb "
            + $"where org_id = '{world.Org}' and id = '{seed.RunId}'");
        var release = await ReleaseIntegrationTests.ReleaseWithAsync(world, "trigger", "active", ("web", 'a'));
        (await ReleaseIntegrationTests.BindAsync(world, release, seed.RunId)).Status.ShouldBe(System.Net.HttpStatusCode.Created);

        var evaluated = await EvaluateAsync(world, seed, EvaluationMode.Live, cancellationToken);

        (await AggregateOutboxCountAsync(world)).ShouldBe(1, "the live verdict enqueued the aggregation with its own insert");
        await using (var scope = await OpenScopeAsync(world, seed, cancellationToken))
        {
            var handler = new ReleaseAggregateOnEvaluationTaskHandler(world.Api.Services.GetRequiredService<IServiceScopeFactory>());
            await handler.HandleAsync(new EvaluationCreatedPayload(seed.Org, seed.RunId, evaluated.Id), scope, cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }

        (await _fixture.ColumnAsync<string>(
            world.Database,
            $"select decision from public.release_decision where org_id = '{world.Org}' and release_id = '{release}'"))
            .ShouldBe(["PASS"]);
    }

    [Fact]
    public async Task ReplayRun_NeverBoundNorCounted_ShadowVerdictEnqueuesNothing()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var seed = await SeedAsync(world);

        await EvaluateAsync(world, seed, EvaluationMode.Shadow, TestContext.Current.CancellationToken);

        (await AggregateOutboxCountAsync(world)).ShouldBe(0, "only a live verdict can complete a release");
    }

    [Fact]
    public async Task LiveEvaluation_RolledBack_LeavesNoAggregateOutboxRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var seed = await SeedAsync(world);

        await EvaluateThenFailAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);

        (await AggregateOutboxCountAsync(world)).ShouldBe(0, "the enqueue shares the evaluation's transaction");
        (await CountAsync(world, seed)).ShouldBe(0);
    }
}
