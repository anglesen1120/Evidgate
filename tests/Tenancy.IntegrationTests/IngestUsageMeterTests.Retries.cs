using Evidgate.Modules.Usage.Application;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The queue's recovery paths against the real ledger: a failed attempt leaves no row and is retried, a replayed task
/// bills once, and a payload that can never succeed stops at the dead-letter cap instead of retrying forever.
/// </summary>
public sealed partial class IngestUsageMeterTests
{
    // ------------------------------------------------------------------ AC4

    [Fact]
    public async Task MeterBatch_LedgerThrowsOnFirstAttempt_SingleMeterEvent()
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

    [Fact]
    public async Task MeterBatch_ExecutionRetriedThenPassed_MeteredOnce()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);

        // One execution ran twice: attempt 1 failed, attempt 2 passed. The batch accepts both events, so the batch's own
        // accepted count is what bills the executor's retry — once, not once per attempt.
        var executionKey = world.Plan.Executions[0].ExecutionKey;
        var body = IngestEventBatchTests.Batch(
            IngestEventBatchTests.Event(
                Guid.CreateVersion7(), executionKey, sequence: 1, attemptNo: 1, status: "failed"),
            IngestEventBatchTests.Event(
                Guid.CreateVersion7(), executionKey, sequence: 2, attemptNo: 2, status: "passed"));

        var answer = await IngestEventBatchTests.PostEventsAsync(world, body);
        answer.Status.ShouldBe(System.Net.HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(2);
        var batchId = answer.Body.GetProperty("batch_id").GetGuid();

        await DispatchAndRunAsync(world);

        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(1, "a retried execution is still one accepted batch");
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "quantity::text")).ShouldBe("2");
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "metric")).ShouldBe("results");

        // A resend of the same two envelopes accepts nothing: nothing is dispatched and nothing bills a second time.
        var resend = await IngestEventBatchTests.PostEventsAsync(world, body);
        resend.Status.ShouldBe(System.Net.HttpStatusCode.OK, resend.ErrorMessage);
        resend.Body.GetProperty("accepted").GetInt32().ShouldBe(0);
        (await Dispatcher(world).DispatchBatchAsync(Token)).Enqueued.ShouldBe(0);
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(1);
    }

    [Fact]
    public async Task MeterBatch_LedgerWritesThenThrows_RowRolledBackAndRetryMetersOnce()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var failures = new LedgerFailures(failuresLeft: 1);
        await using var world = await StartAsync(
            owned.Name,
            services =>
            {
                // The real ledger behind a wrapper that writes the row and then fails: the attempt's transaction is what
                // makes the row disappear, so the retry is the first surviving write.
                services.AddScoped<UsageContracts>();
                services.AddScoped<IUsageLedger>(provider => new WriteThenThrowLedger(
                    provider.GetRequiredService<UsageContracts>(), failures));
            });

        var batchId = await PostBatchAsync(world, events: 2);

        (await Dispatcher(world).DispatchBatchAsync(Token)).Enqueued.ShouldBe(1);

        var consumer = Consumer(world);
        (await consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);
        (await TaskStateAsync(world.Database)).ShouldBe("retry", "the failure after the write still fails the attempt");
        (await LedgerCountAsync(world.Database, world.Org))
            .ShouldBe(0, "the attempt's transaction rolled the insert back with it");

        await MakeRunnableAsync(world.Database);
        (await consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await TaskStateAsync(world.Database)).ShouldBe("done");
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(1);
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "quantity::text")).ShouldBe("2");
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
}
