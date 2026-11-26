using System.Diagnostics.Metrics;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

// The worker half of incident recording: a real freeze (reconcile + revision insert + outbox row) reaches the Healing
// consumer through the dispatcher's subscription, which enqueues one record_incidents task, whose handler records the
// incidents. Each task type has its own queue here so a case drives one consumer at a time.
public sealed partial class ReconcileRunTests
{
    private const string PolicyDelivery = EvidenceRevisionFrozenContract.OutboxTaskType;
    private const string HealingDelivery = IncidentTasks.EvidenceRevisionFrozen;
    private const string RecordIncidents = IncidentTasks.RecordIncidents;

    [Fact]
    public async Task Record_Incidents_Task_Creates_Incidents_From_Freeze()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartIncidentWorldAsync(owned.Name);
        await FreezeWithFailedAttemptAsync(world);
        await DispatchOutboxAsync(world);
        await PollAsync(world, HealingDelivery);
        await PollAsync(world, RecordIncidents);

        (await TasksAsync(world, RecordIncidents, "state = 'done' and attempts = 1")).ShouldBe(1);
        (await IncidentsAsync(world)).ShouldBe(1);
        (await world.CountAsync(
            "audit_log",
            $"org_id = '{world.Org:D}' and action = 'incident.created' and actor_type = 'system' "
            + "and object_type = 'incident'")).ShouldBe(1);
    }

    [Fact]
    public async Task Freeze_Commit_Writes_No_Incident_Synchronously()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartIncidentWorldAsync(owned.Name);
        await FreezeWithFailedAttemptAsync(world);

        (await FrozenEventRowsOfRunAsync(world, world.RunId)).ShouldBe(1);
        (await IncidentsAsync(world)).ShouldBe(0);
        (await TasksAsync(world, RecordIncidents)).ShouldBe(0);

        await DispatchOutboxAsync(world);
        await PollAsync(world, HealingDelivery);
        (await TasksAsync(world, RecordIncidents, "state = 'queued'")).ShouldBe(1);
        (await IncidentsAsync(world)).ShouldBe(0, "the freeze consumer only enqueues");

        await PollAsync(world, RecordIncidents);
        (await IncidentsAsync(world)).ShouldBe(1);
    }

    // Goes red when the subscriber's task key stops carrying its delivery type (the second delivery then collides with
    // the Policy task) or when the freeze consumer enqueues once per delivery instead of once per revision.
    [Fact]
    public async Task Freeze_Event_Redelivery_Enqueues_No_Second_Task()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartIncidentWorldAsync(owned.Name);
        await FreezeWithFailedAttemptAsync(world);
        await DispatchOutboxAsync(world);

        (await TasksAsync(world, PolicyDelivery)).ShouldBe(1, "one Policy task per freeze");
        (await TasksAsync(world, HealingDelivery)).ShouldBe(1, "one Healing task per freeze");
        await PollAsync(world, HealingDelivery);
        await PollAsync(world, RecordIncidents);

        await RedispatchFreezeAsync(world);
        (await TasksAsync(world, PolicyDelivery)).ShouldBe(1);
        (await TasksAsync(world, HealingDelivery)).ShouldBe(1);

        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.task set state = 'queued', attempts = 0 where task_type = '{HealingDelivery}'");
        await PollAsync(world, HealingDelivery);

        (await TasksAsync(world, RecordIncidents)).ShouldBe(1, "a replayed delivery enqueues no second record task");
        (await TasksAsync(
            world,
            RecordIncidents,
            $"idempotency_key = '{IncidentTasks.NaturalKey(world.Org, world.RunId, 1)}'")).ShouldBe(1);
        (await IncidentsAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Freeze_Delivery_Naming_Another_Revision_For_The_Same_Key_Is_Refused()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartIncidentWorldAsync(owned.Name);
        await FreezeWithFailedAttemptAsync(world);
        await DispatchOutboxAsync(world);
        await PollAsync(world, HealingDelivery);

        await _fixture.ExecuteAsync(
            world.Database,
            "update public.task set state = 'queued', attempts = 0, max_attempts = 1, "
            + "payload = jsonb_set(payload, '{evidence_revision_id}', to_jsonb(uuidv7())) "
            + $"where task_type = '{HealingDelivery}'");
        await PollAsync(world, HealingDelivery);

        (await TasksAsync(world, HealingDelivery, "state = 'dead_letter' and attempts = 1")).ShouldBe(1);
        (await TasksAsync(world, RecordIncidents)).ShouldBe(1, "the changed payload enqueues nothing");
        (await IncidentsAsync(world)).ShouldBe(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Frozen_Event_Consumers_Fail_Independently(bool policyFails)
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartIncidentWorldAsync(owned.Name);
        await SeedPinnedPolicyAsync(world, world.RunId, "trace");
        await FreezeWithFailedAttemptAsync(world);
        await DispatchOutboxAsync(world);

        var failing = policyFails ? PolicyDelivery : HealingDelivery;
        var healthy = policyFails ? HealingDelivery : PolicyDelivery;
        await _fixture.ExecuteAsync(
            world.Database,
            "update public.task set max_attempts = 1, payload = jsonb_set(payload, '{org_id}', to_jsonb(uuidv7())) "
            + $"where task_type = '{failing}'");

        await PollAsync(world, failing);
        (await TasksAsync(world, failing, "state = 'dead_letter' and attempts = 1")).ShouldBe(1);
        (await TasksAsync(world, healthy, "state = 'queued' and attempts = 0")).ShouldBe(1);

        await PollAsync(world, healthy);
        (await TasksAsync(world, healthy, "state = 'done'")).ShouldBe(1);
        if (policyFails)
        {
            await PollAsync(world, RecordIncidents);
        }

        (await IncidentsAsync(world)).ShouldBe(policyFails ? 1 : 0);
        (await EvaluationRowsAsync(world)).ShouldBe(policyFails ? 0 : 1);
    }

    [Fact]
    public async Task Record_Incidents_Task_Naming_Another_Tenant_Retries_Then_Dead_Letters()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartIncidentWorldAsync(owned.Name);
        await FreezeWithFailedAttemptAsync(world);
        await DispatchOutboxAsync(world);
        await PollAsync(world, HealingDelivery);
        await _fixture.ExecuteAsync(
            world.Database,
            "update public.task set max_attempts = 2, payload = jsonb_set(payload, '{org_id}', to_jsonb(uuidv7())) "
            + $"where task_type = '{RecordIncidents}'");

        await PollAsync(world, RecordIncidents);
        (await TasksAsync(world, RecordIncidents, "state = 'retry' and attempts = 1")).ShouldBe(1);
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.task set available_at = now() where task_type = '{RecordIncidents}'");
        await PollAsync(world, RecordIncidents);

        (await TasksAsync(world, RecordIncidents, "state = 'dead_letter' and attempts = 2")).ShouldBe(1);
        (await IncidentsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Incident_Created_Counter_Counts_Each_New_Incident_Once_And_Carries_No_Tags()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartIncidentWorldAsync(owned.Name);
        long created = 0;
        long tagged = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == EvidgateTelemetry.MeterName
                && instrument.Name == EvidgateTelemetry.IncidentCreatedMetricName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            Interlocked.Add(ref created, value);
            if (tags.Length > 0)
            {
                Interlocked.Increment(ref tagged);
            }
        });
        listener.Start();

        await FreezeWithFailedAttemptAsync(world);
        await DispatchOutboxAsync(world);
        await PollAsync(world, HealingDelivery);
        await PollAsync(world, RecordIncidents);
        Interlocked.Read(ref created).ShouldBe(1);

        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.task set state = 'queued', attempts = 0 where task_type = '{RecordIncidents}'");
        await PollAsync(world, RecordIncidents);

        (await IncidentsAsync(world)).ShouldBe(1);
        Interlocked.Read(ref created).ShouldBe(1, "a repeat creates nothing and counts nothing");
        Interlocked.Read(ref tagged).ShouldBe(0);
    }

    private Task<IngestTestWorld> StartIncidentWorldAsync(string database) =>
        StartWithQueueAsync(
            attemptsSkip: 1,
            database: database,
            configure: services => services.PostConfigure<TasksOptions>(options =>
            {
                foreach (var type in new[] { PolicyDelivery, HealingDelivery, RecordIncidents })
                {
                    options.QueueRoutes.Add(new QueueRouteOptions { TaskType = type, Queue = type });
                }
            }));

    /// <summary>
    /// Records a failing latest attempt for the first planned execution, then freezes the run the way production does:
    /// the reconcile inserts the revision and writes the frozen-evidence outbox row in one transaction.
    /// </summary>
    private async Task FreezeWithFailedAttemptAsync(IngestTestWorld world)
    {
        var execution = world.Plan.Executions[0];
        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.case_attempt "
            + "(id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status) values "
            + $"('{Guid.CreateVersion7():D}', '{world.Org:D}', '{world.RunId:D}', '{execution.ExecutionKey}', "
            + $"'{execution.CaseId:D}', 2, 'failed', 'failed')");
        await StartIncompleteRunAsync(world);
    }

    private static async Task DispatchOutboxAsync(IngestTestWorld world) =>
        await world.Host.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(Token);

    private async Task RedispatchFreezeAsync(IngestTestWorld world)
    {
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.outbox set dispatched_at = null where task_type = '{PolicyDelivery}'");
        await DispatchOutboxAsync(world);
    }

    private static async Task PollAsync(IngestTestWorld world, string queue) =>
        (await world.Host.Services.GetRequiredService<TaskConsumer>().PollOnceAsync(queue, Token)).ShouldBe(1);

    private static Task<long> TasksAsync(IngestTestWorld world, string taskType, string where = "true") =>
        world.CountAsync("task", $"org_id = '{world.Org:D}' and task_type = '{taskType}' and {where}");

    private static Task<long> IncidentsAsync(IngestTestWorld world) =>
        world.CountAsync("incident", $"org_id = '{world.Org:D}'");
}
