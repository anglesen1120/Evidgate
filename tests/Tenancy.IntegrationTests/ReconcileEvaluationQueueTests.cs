using System.Diagnostics;
using System.Net;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Policy.Application.Evaluation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReconcileRunTests
{
    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    public async Task Frozen_Evaluation_ForcedDeadlineClose_QueuesOneEvaluation(string initialState)
    {
        await using var world = await StartWithQueueAsync();
        await SeedPinnedPolicyAsync(world, world.RunId, "trace");
        await SetRunStateAsync(world, world.RunId, initialState);
        await PassCollectionDeadlineAsync(world, world.RunId);
        var task = await world.Host.Services.GetRequiredService<ITaskQueueWriter>().EnqueueAsync(
            new TaskEnqueue
            {
                Queue = DefaultQueue,
                TaskType = RunReconcileContract.OutboxTaskType,
                PayloadJson = RunReconcileContract.DeadlinePayloadJson(world.RunId),
                IdempotencyKey = RunReconcileContract.DeadlineIdempotencyKey(world.RunId),
                OrgId = world.Org,
            }, Token);
        task.ShouldNotBeNull();
        (await world.Host.Services.GetRequiredService<TaskConsumer>().PollOnceAsync(DefaultQueue, Token)).ShouldBe(1);
        (await StateAsync(world, world.RunId)).ShouldBe("incomplete");
        (await RevisionCountAsync(world)).ShouldBe(1);
        (await FrozenEventRowsOfRunAsync(world, world.RunId)).ShouldBe(1);

        await DispatchFrozenEvaluationsAsync(world, expectedDone: 1);

        (await FrozenEvaluationTaskCountAsync(world)).ShouldBe(1);
        (await world.CountAsync("evaluation")).ShouldBe(1);
        (await world.ScalarAsync<string>("select decision from public.evaluation")).ShouldBe("INCONCLUSIVE");
    }

    [Fact]
    public async Task Frozen_Evaluation_LateSecondRevision_QueuesAndStoresSecondEvaluation()
    {
        await using var world = await StartWithQueueAsync(attemptsSkip: 1);
        await EvaluateFirstAndLateRevisionAsync(world);

        (await FrozenEvaluationTaskCountAsync(world)).ShouldBe(2);
        (await world.CountAsync("evaluation")).ShouldBe(2);
        (await world.ScalarAsync<long>(
            "select count(distinct evidence_revision) from public.evaluation where mode = 'live'"))
            .ShouldBe(2, "each frozen revision gets its own immutable live evaluation");
    }

    [Fact]
    public async Task Frozen_Evaluation_ReplayRecordedRevision_QueuesNoFurtherEvaluation()
    {
        await using var world = await StartWithQueueAsync(attemptsSkip: 1);
        await EvaluateFirstAndLateRevisionAsync(world);

        var (_, replay) = await ReconcileAndFinishOnHostAsync(world, world.RunId, lateTrigger: true);
        replay.ShouldNotBeNull().RevisionNo.ShouldBe(2);
        await DispatchFrozenEvaluationsAsync(world, expectedDone: 2);

        (await RevisionCountAsync(world)).ShouldBe(2);
        (await FrozenEventRowsOfRunAsync(world, world.RunId)).ShouldBe(2);
        (await FrozenEvaluationTaskCountAsync(world)).ShouldBe(2);
        (await world.CountAsync("evaluation")).ShouldBe(2);
    }

    [Fact]
    public async Task Frozen_Evaluation_FaultAfterInsert_QueueRetriesToSameDecidingColumns()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EVIDGATE:Tasks:BackoffBaseSeconds"] = "1",
            ["EVIDGATE:Tasks:BackoffMaxSeconds"] = "1",
        }).Build();
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(),
            services: services =>
            {
                services.AddEvidgateTasks(configuration);
                services.RemoveAll<ITaskHandlerInvoker>();
                services.AddSingleton<FaultingEvaluationTaskHandler>();
                services.AddSingleton<ITaskHandlerInvoker>(provider =>
                    new TaskHandlerAdapter<EvidenceRevisionFrozenPayload>(
                        provider.GetRequiredService<FaultingEvaluationTaskHandler>()));
            });
        await RecordPlanAttemptsAsync(world, skip: 1);
        await SeedPinnedPolicyAsync(world, world.RunId, "trace");
        await StartIncompleteRunAsync(world);
        await world.Host.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(Token);
        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        var probe = world.Host.Services.GetRequiredService<FaultingEvaluationTaskHandler>();
        var taskId = await world.ScalarAsync<Guid>(
            $"select id from public.task where task_type = '{EvidenceRevisionFrozenContract.OutboxTaskType}'");

        (await consumer.PollOnceAsync(DefaultQueue, Token)).ShouldBe(1);
        (await world.ScalarAsync<string>($"select state from public.task where id = '{taskId:D}'")).ShouldBe("retry");
        (await world.CountAsync("evaluation")).ShouldBe(0, "the failed consumer attempt must roll the INSERT back");
        probe.Snapshots.Count.ShouldBe(1, "the first attempt computed and inserted before the injected fault");

        await AwaitFrozenTaskRetryAsync(world, taskId);
        (await consumer.PollOnceAsync(DefaultQueue, Token)).ShouldBe(1);
        (await world.ScalarAsync<string>($"select state from public.task where id = '{taskId:D}'")).ShouldBe("done");
        (await world.ScalarAsync<int>($"select attempts from public.task where id = '{taskId:D}'")).ShouldBe(2);
        (await world.CountAsync("evaluation")).ShouldBe(1);
        probe.Snapshots.Count.ShouldBe(2);
        probe.Snapshots[1].ShouldBe(probe.Snapshots[0], "the re-claimed task must reproduce every deciding column");
    }

    private async Task EvaluateFirstAndLateRevisionAsync(IngestTestWorld world)
    {
        await SeedPinnedPolicyAsync(world, world.RunId, "trace");
        await StartIncompleteRunAsync(world);
        await OpenLateWindowAsync(world, world.RunId);
        await DispatchFrozenEvaluationsAsync(world, expectedDone: 1);
        (await world.CountAsync("evaluation")).ShouldBe(1, "the first revision was actually consumed");

        var late = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(),
                world.Plan.Executions[^1].ExecutionKey, sequence: 2)));
        late.Status.ShouldBe(HttpStatusCode.OK, late.ErrorMessage);
        await DispatchAndReconcileAsync(world, expectedReconciles: 1);
        await DispatchFrozenEvaluationsAsync(world, expectedDone: 2);
    }

    private static async Task DispatchFrozenEvaluationsAsync(IngestTestWorld world, int expectedDone)
    {
        await world.Host.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(Token);
        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        for (var poll = 0; poll < 12; poll++)
        {
            var done = await world.ScalarAsync<long>(
                $"select count(*) from public.task where task_type = '{EvidenceRevisionFrozenContract.OutboxTaskType}' and state = 'done'");
            if (done == expectedDone)
            {
                return;
            }

            await consumer.PollOnceAsync(DefaultQueue, Token);
        }

        throw new ShouldAssertException($"expected {expectedDone} completed frozen-revision evaluation tasks");
    }

    private static Task<long> FrozenEvaluationTaskCountAsync(IngestTestWorld world) =>
        world.ScalarAsync<long>(
            $"select count(*) from public.task where task_type = '{EvidenceRevisionFrozenContract.OutboxTaskType}'");

    private static async Task AwaitFrozenTaskRetryAsync(IngestTestWorld world, Guid taskId)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30))
        {
            if (await world.ScalarAsync<bool>(
                $"select state = 'retry' and available_at <= now() from public.task where id = '{taskId:D}'"))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), Token);
        }

        throw new ShouldAssertException("the failed evaluation task never became runnable again");
    }

    private sealed class FaultingEvaluationTaskHandler(
        EvidenceRevisionFrozenTaskHandler inner,
        IServiceScopeFactory services) : ITaskHandler<EvidenceRevisionFrozenPayload>
    {
        public string TaskType => EvidenceRevisionFrozenContract.OutboxTaskType;

        public List<string> Snapshots { get; } = [];

        public async ValueTask HandleAsync(
            EvidenceRevisionFrozenPayload payload, ITenantScope? scope, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(scope);
            await inner.HandleAsync(payload, scope, cancellationToken);
            await using var child = services.CreateAsyncScope();
            var snapshot = await child.ServiceProvider.GetRequiredService<ITenantDb>()
                .QuerySingleOrDefaultAsync<string>(
                    """
                    select jsonb_build_object('decision', decision, 'reasons', reasons, 'missing', missing,
                        'completeness', completeness, 'outcome', outcome, 'policy_hash', policy_hash,
                        'manifest_hash', manifest_hash, 'exceptions_hash', exceptions_hash)::text
                      from public.evaluation
                     where org_id = @org_id and run_id = @run_id and evidence_revision = @revision
                    """,
                    new { org_id = scope.OrgId, run_id = payload.RunId, revision = payload.EvidenceRevisionNo },
                    cancellationToken);
            Snapshots.Add(snapshot.ShouldNotBeNull());
            if (Snapshots.Count == 1)
            {
                throw new InvalidOperationException("injected failure after evaluation INSERT and before commit");
            }
        }
    }
}
