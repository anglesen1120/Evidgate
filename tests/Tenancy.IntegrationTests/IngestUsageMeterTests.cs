// The ingest-side usage emitter end to end: the real ingest route writes the metering outbox row in its own commit, the
// real dispatcher turns it into a task row, and the real consumer runs the metering handler against a real PostgreSQL and
// the real usage ledger. Resend, replay, retry, dead-letter, a task claimed for another org and two distinct batches are
// each asserted on the ledger's own rows, because a double-billed batch is the failure this story exists to prevent.
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Usage.Application;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// One accepted ingest batch bills once per metric: the handler reads the batch's own accepted count (never the payload),
/// keys the ledger row <c>batch:&lt;event_batch_id&gt;:&lt;metric&gt;</c> and reports a replayed key as a duplicate. The
/// queue's own retry and dead-letter behaviour is exercised, not simulated, so a failing attempt never leaves a row
/// behind and the cap is the queue's.
/// </summary>
/// <remarks>
/// Every case runs in its own migrated database, because the consumer claims any registered <c>task_type</c> on the queue
/// it polls: a sibling case's row would otherwise be claimed here and the attempt count would stop meaning anything.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class IngestUsageMeterTests
{
    /// <summary>The usage task type the ingest commit enqueues, spelled once in production code.</summary>
    private const string MeteringTask = "usage.batch_accepted";

    private const string Queue = "default";

    private readonly PostgresTenancyFixture _fixture;

    public IngestUsageMeterTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A dedicated database with the queue mechanism composed on the ingest host: the dispatcher, the consumer and the
    /// one-second visibility/backoff a retry case needs to become claimable again.
    /// </summary>
    private async Task<IngestTestWorld> StartAsync(
        string database,
        Action<IServiceCollection>? services = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE:Tasks:VisibilityTimeoutSeconds"] = "1",
            ["EVIDGATE:Tasks:BackoffBaseSeconds"] = "1",
            ["EVIDGATE:Tasks:BackoffMaxSeconds"] = "1",
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return await IngestTestWorld.StartAsync(
            _fixture,
            IngestTestWorld.Writer(),
            database: database,
            configuration: settings,
            services: collection =>
            {
                collection.AddEvidgateTasks(configuration);
                services?.Invoke(collection);
            });
    }

    /// <summary>One accepted batch of <paramref name="events"/> events, as the route commits it.</summary>
    private static async Task<Guid> PostBatchAsync(IngestTestWorld world, int events, long sequenceFrom = 1)
    {
        var body = IngestEventBatchTests.Batch(
            [.. Enumerable.Range(0, events).Select(index => (object)IngestEventBatchTests.Event(
                Guid.CreateVersion7(),
                world.Plan.Executions[index].ExecutionKey,
                sequence: sequenceFrom + index))]);

        var answer = await IngestEventBatchTests.PostEventsAsync(world, body);

        answer.Status.ShouldBe(System.Net.HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(events);

        return answer.Body.GetProperty("batch_id").GetGuid();
    }

    /// <summary>Moves this database's metering outbox rows to the queue and runs them, the way the Worker does.</summary>
    private static async Task DispatchAndRunAsync(IngestTestWorld world)
    {
        var dispatched = await world.Host.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(Token);

        dispatched.Enqueued.ShouldBe(1, "the accepted batch's outbox row must reach the queue exactly once");

        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();

        (await consumer.PollOnceAsync(Queue, Token)).ShouldBe(1, "the metering task must be claimed and run");
    }

    private Task<long> LedgerCountAsync(string database, Guid org) =>
        _fixture.ScalarAsync<long>(
            database, $"select count(*) from public.usage_ledger_entry where org_id = '{org:D}'");

    /// <summary>One column of the single ledger row of <paramref name="batchId"/>, or null when there is none.</summary>
    private Task<string> LedgerColumnAsync(string database, Guid org, Guid batchId, string column) =>
        _fixture.ScalarAsync<string>(
            database,
            $"select {column} from public.usage_ledger_entry where org_id = '{org:D}' "
            + $"and billing_key = 'batch:{batchId:D}:event'");

    private async Task<string> BatchColumnAsync(string database, Guid batchId, string column) =>
        await _fixture.ScalarAsync<string>(
            database, $"select {column} from public.event_batch where id = '{batchId:D}'");

    private Task<string> TaskStateAsync(string database) =>
        _fixture.ScalarAsync<string>(database, $"select state from public.task where task_type = '{MeteringTask}'");

    /// <summary>Makes the queue's row runnable without sleeping: the backoff of a retry is cleared, as an operator's replay is.</summary>
    private Task MakeRunnableAsync(string database) =>
        _fixture.ExecuteAsync(
            database,
            $"update public.task set state = 'queued', attempts = 0, locked_until = null, available_at = now() "
            + $"where task_type = '{MeteringTask}'");

    private static TaskConsumer Consumer(IngestTestWorld world) =>
        world.Host.Services.GetRequiredService<TaskConsumer>();

    private static OutboxDispatcher Dispatcher(IngestTestWorld world) =>
        world.Host.Services.GetRequiredService<OutboxDispatcher>();

    // ------------------------------------------------------------------ AC1

    [Fact]
    public async Task MeterBatch_AcceptedBatch_OneLedgerRowPerMetric()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);
        var batchId = await PostBatchAsync(world, events: 2);

        // The commit wrote the metering row in its own transaction (the outbox row), before any dispatch.
        (await world.CountAsync("outbox", $"org_id = '{world.Org:D}' and task_type = '{MeteringTask}'")).ShouldBe(1);

        await DispatchAndRunAsync(world);

        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(1);

        var billingKey = $"batch:{batchId:D}:event";

        (await world.ScalarAsync<string>(
            $"select metric from public.usage_ledger_entry where org_id = '{world.Org:D}' and billing_key = '{billingKey}'"))
            .ShouldBe("results", "the wire metric 'event' is the ledger's accepted-batch metric");
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "quantity::text")).ShouldBe("2");
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "unit")).ShouldBe("batch");
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "project_id::text")).ShouldBe(world.Project.ToString("D"));

        // The period and the instant come from the batch's own commit time, never from the payload.
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "period"))
            .ShouldBe(await BatchColumnAsync(world.Database, batchId, "to_char(created_at at time zone 'UTC', 'YYYY-MM')"));
        (await LedgerColumnAsync(
                world.Database, world.Org, batchId, "to_char(occurred_at at time zone 'UTC', 'YYYY-MM-DD HH24:MI:SS.MS')"))
            .ShouldBe(await BatchColumnAsync(
                world.Database, batchId, "to_char(created_at at time zone 'UTC', 'YYYY-MM-DD HH24:MI:SS.MS')"));

        (await TaskStateAsync(world.Database)).ShouldBe("done");
    }

    // ------------------------------------------------------------------ AC2

    [Fact]
    public async Task MeterBatch_ResentIdenticalBatch_NoSecondRow()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);

        var body = IngestEventBatchTests.Batch(
            IngestEventBatchTests.Event(Guid.CreateVersion7(), world.Plan.Executions[0].ExecutionKey, sequence: 1));
        var first = await IngestEventBatchTests.PostEventsAsync(world, body);
        first.Status.ShouldBe(System.Net.HttpStatusCode.OK, first.ErrorMessage);

        await DispatchAndRunAsync(world);
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(1);

        var resend = await IngestEventBatchTests.PostEventsAsync(world, body);
        resend.Status.ShouldBe(System.Net.HttpStatusCode.OK, resend.ErrorMessage);
        resend.Body.GetProperty("accepted").GetInt32().ShouldBe(0);

        // A batch nothing new was accepted into queues no metering row and reaches no task.
        (await world.CountAsync("outbox", $"org_id = '{world.Org:D}' and task_type = '{MeteringTask}'")).ShouldBe(1);
        (await Dispatcher(world).DispatchBatchAsync(Token)).Enqueued.ShouldBe(0);
        (await Consumer(world).PollOnceAsync(Queue, Token)).ShouldBe(0);
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ AC4

    [Fact]
    public async Task MeterBatch_RetryFailThenPass_SingleMeterEvent()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var failures = new LedgerFailures(failuresLeft: 1);
        await using var world = await StartAsync(
            owned.Name,
            services =>
            {
                // The real ledger behind a switch: the first attempt fails before writing, exactly like a database blip.
                services.AddScoped<UsageContracts>();
                services.AddScoped<IUsageLedger>(provider => new SwitchableLedger(
                    provider.GetRequiredService<UsageContracts>(), failures));
            });

        await PostBatchAsync(world, events: 1);

        var dispatched = await Dispatcher(world).DispatchBatchAsync(Token);
        dispatched.Enqueued.ShouldBe(1);

        var consumer = Consumer(world);

        (await consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);
        (await TaskStateAsync(world.Database)).ShouldBe("retry", "the failed attempt is retried, not swallowed");
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(0, "a failed attempt wrote no ledger row");

        await MakeRunnableAsync(world.Database);
        (await consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await TaskStateAsync(world.Database)).ShouldBe("done");
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(1, "two attempts metered the accepted batch once");
    }

    // ------------------------------------------------------------------ AC5

    [Fact]
    public async Task MeterBatch_ReplayedTask_SingleLedgerRow()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);
        var batchId = await PostBatchAsync(world, events: 2);

        await DispatchAndRunAsync(world);
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(1);

        // The queue runs a row more than once (at-least-once); the second run finds its key on file and writes nothing.
        await MakeRunnableAsync(world.Database);
        (await Consumer(world).PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await TaskStateAsync(world.Database)).ShouldBe("done", "a duplicate billing key is successful delivery");
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(1);
    }

    [Fact]
    public async Task MeterBatch_PayloadQuantityDisagreesWithBatch_RefusedBeforeLedger()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);
        var batchId = await PostBatchAsync(world, events: 2);

        // A payload whose quantity disagrees with the batch's accepted count is refused before the ledger is reached:
        // the quantity is re-read from the batch, never trusted.
        (await Dispatcher(world).DispatchBatchAsync(Token)).Enqueued.ShouldBe(1);
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.task set payload = jsonb_set(payload, '{{quantity}}', '3') "
            + $"where task_type = '{MeteringTask}'");

        (await Consumer(world).PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await TaskStateAsync(world.Database)).ShouldBe("retry");
        (await _fixture.ScalarAsync<string>(
            world.Database, $"select last_error from public.task where task_type = '{MeteringTask}'"))
            .ShouldContain($"{batchId:D} metered 3 against 2 accepted events");
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ dead-letter cap

    [Fact]
    public async Task MeterBatch_HandlerThrows_DeadLetterAfterCap()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);
        var batchId = await PostBatchAsync(world, events: 1);

        await Dispatcher(world).DispatchBatchAsync(Token);
        var missing = Guid.CreateVersion7();

        // A row naming a batch that is not there (an operator replay of a deleted database, a crafted payload) can never
        // become valid by retrying: the queue's one-attempt cap is this database's policy, and the message names the id.
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.task set payload = jsonb_set(payload, '{{event_batch_id}}', '\"{missing:D}\"'), "
            + $"max_attempts = 1 where task_type = '{MeteringTask}'");

        (await Consumer(world).PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await TaskStateAsync(world.Database)).ShouldBe("dead_letter");
        (await _fixture.ScalarAsync<string>(
            world.Database, $"select last_error from public.task where task_type = '{MeteringTask}'"))
            .ShouldBe($"InvalidOperationException: the event batch {missing:D} is not visible to this tenant");

        // The real batch stays unbilled by this row, and the payload is the operator's replay input.
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(0);
        (await _fixture.ScalarAsync<string>(
            world.Database, $"select payload ->> 'event_batch_id' from public.task where task_type = '{MeteringTask}'"))
            .ShouldBe(missing.ToString("D"));
        (await world.CountAsync("event_batch", $"id = '{batchId:D}'")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ tenant isolation

    [Fact]
    public async Task MeterBatch_TaskClaimedForAnotherOrg_WritesNoLedgerRow()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);
        var batchId = await PostBatchAsync(world, events: 1);
        var (otherOrg, _) = await world.CreateOtherOrgAsync(Unique.Slug("other"));

        await Dispatcher(world).DispatchBatchAsync(Token);

        // The row was claimed for the other org: the batch is not a row for that tenant, so nothing is metered.
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.task set org_id = '{otherOrg:D}' where task_type = '{MeteringTask}'");

        (await Consumer(world).PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await TaskStateAsync(world.Database)).ShouldBe("retry");
        (await _fixture.ScalarAsync<string>(
            world.Database, $"select last_error from public.task where task_type = '{MeteringTask}'"))
            .ShouldContain($"{batchId:D} is not visible to this tenant");
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(0);
        (await LedgerCountAsync(world.Database, otherOrg)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ two batches, two keys

    [Fact]
    public async Task MeterBatch_TwoBatches_DistinctBillingKeys()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);

        var first = await PostBatchAsync(world, events: 1);
        var second = await PostBatchAsync(world, events: 2, sequenceFrom: 3);

        first.ShouldNotBe(second);

        (await Dispatcher(world).DispatchBatchAsync(Token)).Enqueued.ShouldBe(2);

        var consumer = Consumer(world);
        (await consumer.PollOnceAsync(Queue, Token)).ShouldBe(2);

        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(2);
        (await LedgerColumnAsync(world.Database, world.Org, first, "quantity::text")).ShouldBe("1");
        (await LedgerColumnAsync(world.Database, world.Org, second, "quantity::text")).ShouldBe("2");
    }

    /// <summary>One shared switch, so a failure counted in one attempt's scope is spent by the next attempt.</summary>
    private sealed class LedgerFailures(int failuresLeft)
    {
        private int _left = failuresLeft;

        internal bool TryConsume() => Interlocked.Decrement(ref _left) >= 0;
    }

    /// <summary>The real ledger, failing while the shared switch has failures left.</summary>
    private sealed class SwitchableLedger(IUsageLedger inner, LedgerFailures failures) : IUsageLedger
    {
        public Task<UsageBatchResult> RecordAsync(
            RecordUsageBatchRequest request,
            CancellationToken cancellationToken) =>
            failures.TryConsume()
                ? throw new InvalidOperationException("the usage ledger is unavailable")
                : inner.RecordAsync(request, cancellationToken);
    }
}
