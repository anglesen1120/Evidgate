// The reserve-once half of the events route's quota admission: a resend of a committed batch spends nothing, and
// two identical batches racing each other reserve the one event they commit between them instead of the batch
// each of them asked for.
#pragma warning disable CA1861
using System.Net;
using System.Text;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The replay cases of <see cref="IngestQuotaTests"/>: the gate is asked only for the rows a batch's insert
/// really adds, so a batch that adds nothing — every event already on file — neither moves the counter nor
/// consumes a row lock.
/// </summary>
public sealed partial class IngestQuotaTests
{
    [Fact]
    public async Task IngestEvents_ReplayedBatch_DoesNotChargeQuota()
    {
        var spy = new QuotaGateSpy();
        await using var world = await StartIngestAsync(spy);
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(counters, world, used: 0m, limit: 10m);

        var json = IngestQaRequests.Json(StepBatch(world, (Guid.CreateVersion7(), 1L), (Guid.CreateVersion7(), 2L)));

        var first = await IngestQaRequests.PostAsync(world.Client, world.RunId, new StringContent(json, Encoding.UTF8, "application/json"));
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        first.Body.GetProperty("accepted").GetInt32().ShouldBe(2);
        spy.Calls.ShouldBe(1);
        spy.Requests.ShouldHaveSingleItem().Quantity.ShouldBe(2m);

        var replay = await IngestQaRequests.PostAsync(world.Client, world.RunId, new StringContent(json, Encoding.UTF8, "application/json"));

        // The resend is answered with duplicate acks and metered nothing, so it must not spend quota either: the
        // batch's insert adds no row, the counter does not move and the gate is not asked a second time.
        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.Body.GetProperty("accepted").GetInt32().ShouldBe(0);
        replay.Body.GetProperty("duplicates").GetInt32().ShouldBe(2);
        spy.Calls.ShouldBe(1);

        (await ReadAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(2m);
        (await EventRowsAsync(world)).ShouldBe(2);
        (await world.CountAsync("outbox", $"org_id = '{world.Org:D}' and task_type = 'usage.batch_accepted'")).ShouldBe(1);
    }

    [Fact]
    public async Task IngestEvents_InBatchDuplicate_ReservesTheOneStoredEvent()
    {
        var spy = new QuotaGateSpy();
        await using var world = await StartIngestAsync(spy);
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(counters, world, used: 0m, limit: 10m);

        // One event_id twice inside one batch, identical envelopes: one row is committed and one ack is a
        // duplicate, so the ledger's unit — and therefore the reservation — is the single stored event, not the
        // request's two entries.
        var id = Guid.CreateVersion7();
        var answer = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, StepBatch(world, (id, 1L), (id, 1L)));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(1);
        answer.Body.GetProperty("duplicates").GetInt32().ShouldBe(1);
        spy.Requests.ShouldHaveSingleItem().Quantity.ShouldBe(1m);
        (await EventRowsAsync(world)).ShouldBe(1);
        (await ReadAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(1m);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    public async Task IngestEvents_ConcurrentIdenticalBatches_ChargeOnlyTheNewlyCommittedEvent(int initialUsed)
    {
        var spy = new QuotaGateSpy();
        var barrier = new RequestBarrier();
        await using var world = await StartIngestAsync(
            spy,
            services: services => ReplaceWith<IRunEventScope>(services, inner => new SynchronizedScope(inner, barrier)));
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(counters, world, used: initialUsed, limit: 10m);
        var json = IngestQaRequests.Json(StepBatch(world, (Guid.CreateVersion7(), 1L)));

        // Both requests stand in front of their insert before either commits, so the second insert is the one that
        // waits on the first's uncommitted event row and then adds nothing.
        var answers = await Task.WhenAll(
            IngestQaRequests.PostAsync(world.Client, world.RunId, new StringContent(json, Encoding.UTF8, "application/json")),
            IngestQaRequests.PostAsync(world.Client, world.RunId, new StringContent(json, Encoding.UTF8, "application/json")));

        // One event between them: the winner's batch is accepted, the loser's is a duplicate. At 9 the loser would
        // also be the spurious 429 of a reservation made for an event it never commits.
        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.OK);
        answers.Sum(answer => answer.Body.GetProperty("accepted").GetInt32()).ShouldBe(1);
        answers.Sum(answer => answer.Body.GetProperty("duplicates").GetInt32()).ShouldBe(1);

        // The reservation matches the ledger: one question, for the one row the insert added.
        spy.Requests.ShouldHaveSingleItem().Quantity.ShouldBe(1m);
        (await EventRowsAsync(world)).ShouldBe(1);
        (await ReadAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(initialUsed + 1m);
        (await WarningRowsAsync(world)).ShouldBe(1);
    }

    /// <summary>Two-request rendezvous: the second arrival releases both callers.</summary>
    private sealed class RequestBarrier
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        internal Task ArriveAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref _arrived) == 2)
            {
                _ready.TrySetResult();
            }

            return _ready.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
        }
    }

    /// <summary>The registered run-scope read, released only once both racing requests stand in front of it.</summary>
    private sealed class SynchronizedScope(IRunEventScope inner, RequestBarrier barrier) : IRunEventScope
    {
        public async ValueTask<RunEventScope?> ReadAsync(
            Guid runId,
            IReadOnlyCollection<string> executionKeys,
            CancellationToken cancellationToken)
        {
            await barrier.ArriveAsync(cancellationToken);
            return await inner.ReadAsync(runId, executionKeys, cancellationToken);
        }
    }
}
