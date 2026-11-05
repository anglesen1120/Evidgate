// A canceled run keeps accepting evidence: the batch is stored (never refused) and each event row is stamped
// after_cancel, derived inside the ingest transaction from the run row it locks FOR SHARE. These cases prove the
// stamp, that accepting is not a validation bypass, and that the lock serialises with a cancelling transaction.
#pragma warning disable CA1861
using System.Net;
using Evidgate.Modules.Orchestrator.Contracts;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.IngestEventBatchTests;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Post-cancel acceptance of <c>POST /v1/runs/{r}/events</c> through the real host and a real PostgreSQL: the
/// stored <c>after_cancel</c> flag, the unchanged replay/validation answers, and the ingest-versus-cancel race.
/// </summary>
[Trait("Category", "Docker")]
public sealed class IngestAfterCancelTests
{
    /// <summary>A well-formed execution key that no seeded plan carries: 64 lowercase hex.</summary>
    private const string UnplannedKey = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    /// <summary>The advisory gate that parks a real ingest transaction inside its own insert.</summary>
    private const long IngestGateKey = 7_282_365_123_001;

    private readonly PostgresTenancyFixture _fixture;

    public IngestAfterCancelTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ accepted and stamped

    [Fact]
    public async Task EventBatch_AfterCancel_Accepted_AfterCancelTrue()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // The positive control: the same run, the same route, an open run stores the row unstamped.
        var open = await PostEventsAsync(world, Batch(Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1)));
        open.Status.ShouldBe(HttpStatusCode.OK, open.ErrorMessage);

        await world.SetRunStateAsync("canceled");
        var accepted = await PostEventsAsync(world, Batch(Event(Guid.CreateVersion7(), KeyOf(world, 1), sequence: 2)));

        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        accepted.Body.GetProperty("accepted").GetInt32().ShouldBe(1);
        (await world.EventColumnAsync<bool>("after_cancel")).ShouldBe([false, true]);

        // The attempt rows the same batches wrote are stamped from the run row, exactly like the event rows: the
        // pre-cancel attempt stays false, the post-cancel one is true.
        (await AttemptFlagAsync(world, KeyOf(world, 0))).ShouldBeFalse("the attempt arrived before the cancel");
        (await AttemptFlagAsync(world, KeyOf(world, 1))).ShouldBeTrue("the attempt arrived after the cancel");
        (await RunStateAsync(world)).ShouldBe("canceled", "post-cancel evidence never completes or reopens the run");

        // Negative control: a second run of the same database that is never canceled keeps both flags false.
        await using var openRun = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        var control = await PostEventsAsync(
            openRun, Batch(Event(Guid.CreateVersion7(), KeyOf(openRun, 0), sequence: 1)));

        control.Status.ShouldBe(HttpStatusCode.OK, control.ErrorMessage);
        (await openRun.EventColumnAsync<bool>("after_cancel")).ShouldBe([false]);
        (await AttemptFlagAsync(openRun, KeyOf(openRun, 0))).ShouldBeFalse("a run that was never canceled stamps nothing");
    }

    [Fact]
    public async Task EventBatch_AfterCancel_ReplayAcknowledged_DifferentPayloadRejected()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        await world.SetRunStateAsync("canceled");

        var eventId = Guid.CreateVersion7();
        var batch = Batch(Event(eventId, KeyOf(world, 0), sequence: 1));

        (await PostEventsAsync(world, batch)).Status.ShouldBe(HttpStatusCode.OK);
        (await FlagOfAsync(world, eventId)).ShouldBeTrue();

        // The identical retry replays: one row, duplicate ack, and the stored flag is not rewritten.
        var replay = await PostEventsAsync(world, batch);

        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.Body.GetProperty("accepted").GetInt32().ShouldBe(0);
        replay.Body.GetProperty("duplicates").GetInt32().ShouldBe(1);
        replay.Body.GetProperty("acks")[0].GetProperty("ack").GetString().ShouldBe("duplicate");
        (await world.EventColumnAsync<bool>("after_cancel")).Count.ShouldBe(1);
        (await FlagOfAsync(world, eventId)).ShouldBeTrue();

        // The same event_id with a different payload is still a conflict, so post-cancel acceptance is not a
        // way around the payload-hash dedupe.
        var conflict = await PostEventsAsync(
            world, Batch(Event(eventId, KeyOf(world, 0), sequence: 1, status: "failed")));

        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("duplicate_different_payload");
        (await world.EventColumnAsync<bool>("after_cancel")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task EventBatch_AfterCancel_UnplannedExecutionKeyStillRejected()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        await world.SetRunStateAsync("canceled");

        var refused = await PostEventsAsync(
            world,
            Batch(
                Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1),
                Event(Guid.CreateVersion7(), UnplannedKey, sequence: 2)));

        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("unknown_execution_key");
        (await world.EventColumnAsync<bool>("after_cancel")).ShouldBeEmpty();

        // Control: the same batch with both keys planned is accepted and each row is stamped.
        var accepted = await PostEventsAsync(
            world,
            Batch(
                Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1),
                Event(Guid.CreateVersion7(), KeyOf(world, 1), sequence: 2)));

        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        (await world.EventColumnAsync<bool>("after_cancel")).ShouldBe([true, true]);
    }

    // ------------------------------------------------------------------ the race with a cancel commit

    [Fact]
    public async Task EventBatch_InFlightAtCancelCommit_MarkedAfterCancel()
    {
        // The probes read lock waits database-wide, so the race owns a database no other test inserts into.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();

        // A running run waits on the shared scope read; a queued one waits on the running transition the ingest
        // makes first, and must still be accepted once that transition finds the run terminal.
        await CancelCommitFirstAsync(database, "running");
        await CancelCommitFirstAsync(database, "queued");
        await IngestCommitFirstAsync(database);
    }

    /// <summary>
    /// The cancel holds the run row and commits while the batch is in flight: the batch's own statement waits, then
    /// reads the committed state and stamps the row — a committed cancel can never be missed.
    /// </summary>
    private async Task CancelCommitFirstAsync(string database, string runState)
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), database: database);
        await world.SetRunStateAsync(runState);

        // The write half of the cancel's lock discipline: the run row held uncommitted in state `canceled`.
        await using var cancel = await _fixture.OpenSuperuserAsync(database);
        await using var cancelTx = await cancel.BeginTransactionAsync(TestContext.Current.CancellationToken);
        (await ScalarAsync(cancel, cancelTx, LockRunSql(world)))
            .ShouldNotBeNull("the run row must exist to take its lock");
        (await ExecuteAsync(
            cancel,
            cancelTx,
            $"update public.run set state = 'canceled' where id = '{world.RunId:D}'")).ShouldBe(1);

        var eventId = Guid.CreateVersion7();
        var ingest = PostEventsAsync(world, Batch(Event(eventId, KeyOf(world, 0), sequence: 1)));

        await AwaitParkedAsync(world, runState == "queued" ? "for no key update" : "for share of r", ingest);
        await cancelTx.CommitAsync(TestContext.Current.CancellationToken);

        var answer = await ingest;

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(1);
        (await FlagOfAsync(world, eventId)).ShouldBeTrue("the batch serialised after the cancel commit");
        (await RunStateAsync(world)).ShouldBe("canceled");
    }

    /// <summary>
    /// The batch commits first: the cancelling transition parks on the batch's shared lock until it commits, so the
    /// row that was stored before the cancel stays unstamped and the next batch is stamped.
    /// </summary>
    private async Task IngestCommitFirstAsync(string database)
    {
        await using var gate = await _fixture.OpenSuperuserAsync(database);
        await ExecuteAsync(gate, null, $"select pg_advisory_lock({IngestGateKey})");
        await ArmIngestGateAsync(database);

        try
        {
            await using var world = await IngestTestWorld.StartAsync(
                _fixture, IngestTestWorld.Writer(), database: database);
            await world.SetRunStateAsync("running");

            var eventId = Guid.CreateVersion7();
            var ingest = PostEventsAsync(world, Batch(Event(eventId, KeyOf(world, 0), sequence: 1)));

            // The batch is past its shared read and parked inside the insert, so it holds the run row.
            await AwaitParkedAsync(world, "insert into public.event", ingest);

            // The same guarded transition a cancel goes through, in its own transaction.
            var cancel = Detached(
                async () =>
                {
                    await using var scope = await RunTransitionScope.BeginAsync(world.Api, world.Org, world.Project);
                    var result = await scope.TransitionAsync(new TransitionRunStateCommand(world.RunId, "canceled"));
                    await scope.FlushAuditAsync();
                    await scope.CommitAsync();

                    return result;
                },
                TestContext.Current.CancellationToken);

            await AwaitParkedAsync(world, "for no key update", cancel);
            await ExecuteAsync(gate, null, $"select pg_advisory_unlock({IngestGateKey})");

            var answer = await ingest;

            answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
            (await FlagOfAsync(world, eventId)).ShouldBeFalse("the row was committed before the cancel");

            var canceled = await cancel;

            canceled.IsSuccess.ShouldBeTrue(canceled.Error?.Message);
            canceled.Value.Outcome.ShouldBe(TransitionOutcome.Applied);
            (await RunStateAsync(world)).ShouldBe("canceled");

            var laterId = Guid.CreateVersion7();
            (await PostEventsAsync(world, Batch(Event(laterId, KeyOf(world, 1), sequence: 2))))
                .Status.ShouldBe(HttpStatusCode.OK);
            (await FlagOfAsync(world, laterId)).ShouldBeTrue("the cancel had committed before this batch");
        }
        finally
        {
            await DisarmIngestGateAsync(database);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string LockRunSql(IngestTestWorld world) =>
        $"select id from public.run where id = '{world.RunId:D}' for no key update";

    private static Task<bool> FlagOfAsync(IngestTestWorld world, Guid eventId) =>
        world.ScalarAsync<bool>(
            $"select after_cancel from public.event where org_id = '{world.Org:D}' "
            + $"and run_id = '{world.RunId:D}' and event_id = '{eventId:D}'");

    /// <summary>The stored <c>after_cancel</c> of the attempt one planned execution of the run wrote.</summary>
    private static Task<bool> AttemptFlagAsync(IngestTestWorld world, string executionKey) =>
        world.ScalarAsync<bool>(
            $"select after_cancel from public.case_attempt where org_id = '{world.Org:D}' "
            + $"and run_id = '{world.RunId:D}' and execution_key = '{executionKey}' and attempt_no = 1");

    private static Task<string> RunStateAsync(IngestTestWorld world) =>
        world.ScalarAsync<string>($"select state from public.run where id = '{world.RunId:D}'");

    /// <summary>Runs <paramref name="work"/> in a fresh execution flow, so its scope is a second connection.</summary>
    private static Task<T> Detached<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work, cancellationToken);
        }
    }

    /// <summary>Waits until a session of this database is parked on <paramref name="statement"/>'s lock.</summary>
    private async Task AwaitParkedAsync(IngestTestWorld world, string statement, Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await WaitingSessionsAsync(world.Database, statement) == 0)
        {
            task.IsCompleted.ShouldBeFalse($"the request finished before it parked on {statement}");
            DateTime.UtcNow.ShouldBeLessThan(deadline, $"no session parked on {statement}");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        task.IsCompleted.ShouldBeFalse("the request must still wait while the other transaction is uncommitted");
    }

    /// <summary>Other sessions of this database waiting on a lock while running the named statement.</summary>
    private Task<int> WaitingSessionsAsync(string database, string statement) =>
        _fixture.ScalarAsync<int>(
            database,
            "select count(*)::int from pg_stat_activity where datname = current_database() "
            + "and wait_event_type = 'Lock' and pid <> pg_backend_pid() "
            + $"and query ilike '%{statement}%'");

    /// <summary>
    /// A trigger that parks every inserted event on the test-held advisory lock, so one real ingest transaction can
    /// be observed holding the run row without a sleep.
    /// </summary>
    private Task ArmIngestGateAsync(string database) =>
        _fixture.ExecuteAsync(
            database,
            $"""
            create or replace function public.ingest_after_cancel_gate() returns trigger
              language plpgsql as $gate$
            begin
              perform pg_advisory_xact_lock({IngestGateKey});
              return new;
            end $gate$;
            drop trigger if exists ingest_after_cancel_gate on public.event;
            create trigger ingest_after_cancel_gate before insert on public.event
              for each row execute function public.ingest_after_cancel_gate();
            """);

    private Task DisarmIngestGateAsync(string database) =>
        _fixture.ExecuteAsync(
            database,
            """
            drop trigger if exists ingest_after_cancel_gate on public.event;
            drop function if exists public.ingest_after_cancel_gate();
            """);

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
