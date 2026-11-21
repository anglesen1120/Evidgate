using Evidgate.Modules.Usage.Application;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The no-double-count suite of the usage emitter: whichever way a batch's metering is repeated — the producer resends
/// the batch, the queue redelivers the metering row, an attempt fails and is retried — the ledger ends with exactly one
/// row per metric per original batch, and a stored row that bills something else is never overwritten or tallied as a
/// replay. Every case counts the rows the ledger itself holds, never a return value.
/// </summary>
public sealed partial class IngestUsageMeterTests
{
    private Task<long> DuplicateAttemptsAsync(string database, Guid org, Guid batchId) =>
        _fixture.ScalarAsync<long>(
            database,
            $"select coalesce(sum(attempts), 0) from public.usage_duplicate_attempt where org_id = '{org:D}' "
            + $"and billing_key = 'batch:{batchId:D}:event'");

    /// <summary>
    /// A second outbox row for the same batch under another idempotency key: what an emitter that re-enqueued after a
    /// lost acknowledgement would leave, which the queue cannot recognise as the same work.
    /// </summary>
    private Task RedeliverMeteringRowAsync(string database, Guid org, string suffix) =>
        _fixture.ExecuteAsync(
            database,
            "insert into public.outbox (id, org_id, aggregate_type, aggregate_id, task_type, payload, idempotency_key) "
            + "select gen_random_uuid(), org_id, aggregate_type, aggregate_id, task_type, payload, "
            + $"idempotency_key || ':{suffix}' from public.outbox "
            + $"where org_id = '{org:D}' and task_type = '{MeteringTask}' and idempotency_key not like '%:redelivery%'");

    private async Task RunRedeliveryAsync(IngestTestWorld world, string suffix)
    {
        await RedeliverMeteringRowAsync(world.Database, world.Org, suffix);

        (await Dispatcher(world).DispatchBatchAsync(Token)).Enqueued.ShouldBe(1, "the redelivered row reaches the queue");
        (await Consumer(world).PollOnceAsync(Queue, Token)).ShouldBe(1, "and the handler runs it again");
    }

    // ------------------------------------------------------------------ resend

    [Fact]
    public async Task NoDoubleCount_ResendOfMeteredBatch_OneOutboxRowOneTaskOneLedgerRow()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);

        var body = IngestEventBatchTests.Batch(
            IngestEventBatchTests.Event(Guid.CreateVersion7(), world.Plan.Executions[0].ExecutionKey, sequence: 1),
            IngestEventBatchTests.Event(Guid.CreateVersion7(), world.Plan.Executions[1].ExecutionKey, sequence: 2));
        var first = await IngestEventBatchTests.PostEventsAsync(world, body);
        first.Status.ShouldBe(System.Net.HttpStatusCode.OK, first.ErrorMessage);
        var batchId = first.Body.GetProperty("batch_id").GetGuid();

        await DispatchAndRunAsync(world);

        for (var resend = 0; resend < 2; resend++)
        {
            var answer = await IngestEventBatchTests.PostEventsAsync(world, body);
            answer.Status.ShouldBe(System.Net.HttpStatusCode.OK, answer.ErrorMessage);
            answer.Body.GetProperty("accepted").GetInt32().ShouldBe(0);
        }

        (await Dispatcher(world).DispatchBatchAsync(Token)).Enqueued.ShouldBe(0);
        (await Consumer(world).PollOnceAsync(Queue, Token)).ShouldBe(0);

        (await world.CountAsync("outbox", $"org_id = '{world.Org:D}' and task_type = '{MeteringTask}'")).ShouldBe(1);
        (await world.CountAsync("task", $"org_id = '{world.Org:D}' and task_type = '{MeteringTask}'")).ShouldBe(1);
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(1);
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "quantity::text")).ShouldBe("2");
        (await DuplicateAttemptsAsync(world.Database, world.Org, batchId)).ShouldBe(0, "a resend never reaches the ledger");
    }

    // ------------------------------------------------------------------ reprocess / redelivery

    [Fact]
    public async Task NoDoubleCount_RedeliveredMeteringRow_BillsOnceAndTalliesEachDuplicate()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);
        var batchId = await PostBatchAsync(world, events: 2);

        await DispatchAndRunAsync(world);
        var storedHash = await _fixture.ScalarAsync<string>(
            world.Database,
            $"select payload_hash from public.usage_ledger_entry where org_id = '{world.Org:D}' "
            + $"and billing_key = 'batch:{batchId:D}:event'");

        await RunRedeliveryAsync(world, ":redelivery-1");
        await RunRedeliveryAsync(world, ":redelivery-2");

        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(1, "two redeliveries add no row");
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "quantity::text")).ShouldBe("2");
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "payload_hash")).ShouldBe(storedHash);
        (await DuplicateAttemptsAsync(world.Database, world.Org, batchId))
            .ShouldBe(2, "each redelivery is tallied as a duplicate attempt, not billed");
        (await PendingTaskRowsAsync(world.Database)).ShouldBe(0, "every redelivered task completed");
    }

    [Fact]
    public async Task NoDoubleCount_RedeliveryAgainstStoredRowOfOtherQuantity_KeepsStoredBillAndTalliesNothing()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);
        var batchId = await PostBatchAsync(world, events: 2);

        await SeedLedgerAsync(world, await SeededEntryAsync(world, batchId, quantity: 7m));
        await DispatchAndRunAsync(world);
        await RunRedeliveryAsync(world, ":redelivery-1");

        // The payload-hash compare is what separates a replay from a different bill: a replay is tallied, a conflict is
        // rolled back whole and leaves no tally, so a dropped compare would show here as two tallied "duplicates".
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(1);
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "quantity::text")).ShouldBe("7");
        (await DuplicateAttemptsAsync(world.Database, world.Org, batchId)).ShouldBe(0);
        (await PendingTaskRowsAsync(world.Database)).ShouldBe(0, "a conflict completes the task instead of retrying it");
    }

    // ------------------------------------------------------------------ retry fail then pass

    [Fact]
    public async Task NoDoubleCount_RetryFailThenPassAcrossTwoBatches_OneRowPerBatchWithItsOwnQuantity()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var failures = new LedgerFailures(failuresLeft: 1);
        await using var world = await StartAsync(
            owned.Name,
            services =>
            {
                services.AddScoped<UsageContracts>();
                services.AddScoped<IUsageLedger>(provider => new WriteThenThrowLedger(
                    provider.GetRequiredService<UsageContracts>(), failures));
            });

        var first = await PostBatchAsync(world, events: 1);
        var second = await PostBatchAsync(world, events: 2, sequenceFrom: 3);

        (await Dispatcher(world).DispatchBatchAsync(Token)).Enqueued.ShouldBe(2);
        (await Consumer(world).PollOnceAsync(Queue, Token)).ShouldBe(2);

        // One attempt wrote its row and then died: its transaction took the row with it, the other batch's row stays.
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(1);
        (await PendingTaskRowsAsync(world.Database)).ShouldBe(1, "the failed attempt is queued again");

        // Every task runs once more, the settled one included: the retry bills the failed batch, the rerun bills nothing.
        await MakeRunnableAsync(world.Database);
        (await Consumer(world).PollOnceAsync(Queue, Token)).ShouldBe(2);

        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(2, "one row per batch, not one per attempt");
        (await LedgerColumnAsync(world.Database, world.Org, first, "quantity::text")).ShouldBe("1");
        (await LedgerColumnAsync(world.Database, world.Org, second, "quantity::text")).ShouldBe("2");
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select sum(quantity)::text from public.usage_ledger_entry where org_id = '{world.Org:D}'"))
            .ShouldBe("3", "the accepted events are billed once in total");
        (await PendingTaskRowsAsync(world.Database)).ShouldBe(0);
    }
}
