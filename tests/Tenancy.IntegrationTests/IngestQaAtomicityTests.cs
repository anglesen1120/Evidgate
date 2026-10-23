// 04.1.1-T1 independent QA (S33): the two atomicity/concurrency claims the implementer's suite leaves open —
// a failure at transaction commit leaves zero rows and a retry is accepted (task file AC8), and the run row's
// FOR SHARE read never misses a cancel that commits while the ingest is in flight (ruling T91). Both drive the
// real host and a real PostgreSQL; the fault is a test-owned deferred constraint trigger, not a seam in the
// production composition.
#pragma warning disable CA1861
using System.Net;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Commit-failure atomicity (AC8) and the T91 cancel-vs-ingest race of <c>POST /v1/runs/{r}/events</c>.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class IngestQaAtomicityTests
{
    private readonly PostgresTenancyFixture _fixture;

    public IngestQaAtomicityTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ---------------------------------------------------------------- AC8: a commit-time failure rolls back whole

    [Fact]
    public async Task crash_between_insert_and_commit_leaves_zero_rows_and_retry_is_accepted()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        var batch = IngestQaRequests.Batch(
            IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1),
            IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 1), sequence: 2));

        var slotBefore = await SlotAsync(world);

        await ArmCommitFaultAsync(world);
        try
        {
            // The fault is armed: every `event` insert of the next transaction raises at COMMIT, after the
            // handler wrote its rows, so the failure lands between the insert and the commit (AC8).
            await _fixture.ExecuteAsync(world.Database, "insert into public.ingest_qa_fault (id) values (1)");

            var crashed = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, batch);

            crashed.Status.ShouldBe(HttpStatusCode.InternalServerError, crashed.ErrorMessage);
            crashed.ErrorCode.ShouldBe("internal_error");

            // Nothing of the aborted transaction survives: no event, no batch, no attempt, no outbox row,
            // and the producer's high-water mark is untouched.
            (await world.CountAsync("event")).ShouldBe(0);
            (await world.CountAsync("event_batch")).ShouldBe(0);
            (await world.CountAsync("case_attempt")).ShouldBe(0);
            (await world.CountAsync("outbox")).ShouldBe(0);
            (await SlotAsync(world)).ShouldBe(slotBefore);

            // The fault is off: the same batch retried commits cleanly and every event is newly accepted.
            await _fixture.ExecuteAsync(world.Database, "delete from public.ingest_qa_fault");

            var retried = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, batch);

            retried.Status.ShouldBe(HttpStatusCode.OK, retried.ErrorMessage);
            retried.Body.GetProperty("accepted").GetInt32().ShouldBe(2);
            retried.Body.GetProperty("duplicates").GetInt32().ShouldBe(0);
            IngestQaRequests.Acks(retried).Select(ack => ack.GetProperty("ack").GetString()).ShouldBe(["accepted", "accepted"]);
            (await world.CountAsync("event")).ShouldBe(2);
            (await world.CountAsync("event_batch")).ShouldBe(1);
        }
        finally
        {
            await DisarmCommitFaultAsync(world);
        }
    }

    // ---------------------------------------------------------------- T91: a committed cancel is never missed

    [Fact]
    public async Task flag_never_misses_committed_cancel()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // Inverse half first: an event committed while the run is open is not after_cancel.
        var open = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1)));

        open.Status.ShouldBe(HttpStatusCode.OK, open.ErrorMessage);
        (await world.ScalarAsync<bool>("select after_cancel from public.event where sequence = 1")).ShouldBeFalse();

        // A separate (owner/superuser) connection holds the run row FOR UPDATE and flips it to canceled, but does
        // not commit: the ingest that follows must block on the row's FOR SHARE.
        await using var cancel = await _fixture.OpenSuperuserAsync(world.Database);
        await using var cancelTx = await cancel.BeginTransactionAsync(TestContext.Current.CancellationToken);

        await using (var lockRun = cancel.CreateCommand())
        {
            lockRun.Transaction = cancelTx;
            lockRun.CommandText = $"select id from public.run where id = '{world.RunId:D}' for update";
            (await lockRun.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        }

        await using (var cancelRun = cancel.CreateCommand())
        {
            cancelRun.Transaction = cancelTx;
            cancelRun.CommandText = $"update public.run set state = 'canceled' where id = '{world.RunId:D}'";
            (await cancelRun.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        }

        var ingest = IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 2)));

        // The ingest must reach the run row and wait on its FOR SHARE before the cancel commits.
        await WaitUntilIngestWaitsOnRunRowAsync(world);

        await cancelTx.CommitAsync(TestContext.Current.CancellationToken);

        var answer = await ingest;

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(1);
        (await world.ScalarAsync<bool>("select after_cancel from public.event where sequence = 2")).ShouldBeTrue();
    }

    // ---------------------------------------------------------------- helpers

    private static Task<long> SlotAsync(IngestTestWorld world) =>
        world.ScalarAsync<long>("select last_sequence from public.producer_slot where producer_id = 'shard-1'");

    /// <summary>Polls <c>pg_stat_activity</c> (bounded) until the ingest backend waits on the run row's lock.</summary>
    private async Task WaitUntilIngestWaitsOnRunRowAsync(IngestTestWorld world)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var waiting = await _fixture.ScalarAsync<long>(
                world.Database,
                "select count(*) from pg_stat_activity "
                + $"where datname = '{world.Database}' and wait_event_type = 'Lock' and query ilike '%for share%'");

            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("the ingest request never blocked on the run row FOR SHARE");
    }

    /// <summary>
    /// Creates the test-owned fault: a flag table and a deferred constraint trigger on <c>event</c> that raises
    /// at COMMIT only while the flag row exists, so the failure is injected between the insert and the commit.
    /// </summary>
    private static Task ArmCommitFaultAsync(IngestTestWorld world) =>
        world.Fixture.ExecuteAsync(
            world.Database,
            """
            create table if not exists public.ingest_qa_fault (id integer primary key);
            create or replace function public.ingest_qa_fault_fn() returns trigger
              language plpgsql security definer as $qa$
            begin
              if exists (select 1 from public.ingest_qa_fault) then
                raise exception 'ingest qa: injected commit failure' using errcode = 'P0001';
              end if;
              return null;
            end $qa$;
            drop trigger if exists ingest_qa_fault_event on public.event;
            create constraint trigger ingest_qa_fault_event
              after insert on public.event
              deferrable initially deferred
              for each row
              execute function public.ingest_qa_fault_fn();
            """);

    private static Task DisarmCommitFaultAsync(IngestTestWorld world) =>
        world.Fixture.ExecuteAsync(
            world.Database,
            """
            drop trigger if exists ingest_qa_fault_event on public.event;
            drop function if exists public.ingest_qa_fault_fn();
            drop table if exists public.ingest_qa_fault;
            """);
}
