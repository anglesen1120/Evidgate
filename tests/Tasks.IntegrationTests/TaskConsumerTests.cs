using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Globalization;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 01.5.1-T1.2/T1.3 and §6/§8: the consumer runs a claimed row exactly once per
/// attempt, marks it `done` after the handler's commit, requeues a row whose lease
/// ran out, retries with backoff, dead-letters at the cap, and opens the tenant
/// scope from the row's `org_id`.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TaskConsumerTests
{
    private const string Queue = "consumer";

    private readonly PostgresTasksFixture _fixture;

    public TaskConsumerTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>One due row runs its handler and ends `done` with the lease released.</summary>
    [Fact]
    public async Task Consumer_Runs_Handler_And_MarksDone()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"one"}""",
            idempotencyKey: "one");

        await using var host = TasksTestHost.Start(_fixture, database);

        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        host.Recorder.Runs["one"].ShouldBe(1);
        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = 'one'")).ShouldBe("done");
        (await TaskSql.ScalarAsync<int>(owner, "select attempts from public.task where idempotency_key = 'one'")).ShouldBe(1);
        (await TaskSql.ScalarAsync<bool>(owner, "select locked_until is null from public.task where idempotency_key = 'one'")).ShouldBeTrue();
    }

    /// <summary>
    /// T1.2: two concurrent claim loops over one queue run every row exactly once —
    /// the end-to-end form of AC7 (the claim hands each row to one consumer, and the
    /// handler therefore sees each payload once).
    /// </summary>
    [Fact]
    public async Task Consumer_TwoConsumers_NoDoubleExecute()
    {
        const int rows = 20;
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        for (var index = 0; index < rows; index++)
        {
            await TaskSql.SeedTaskAsync(
                owner,
                Queue,
                TaskHandlerTypes.Recording,
                payload: $$"""{"value":"task-{{index}}"}""",
                idempotencyKey: $"double-{index}");
        }

        await using var host = TasksTestHost.Start(_fixture, database, maximumPoolSize: 4);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = host.Consumer.RunAsync(Queue, concurrency: 2, stop.Token);

        var drained = await WaitUntilAsync(
            async () => await TaskSql.ScalarAsync<long>(
                owner,
                "select count(*) from public.task where queue = @queue and state = 'done'",
                ("queue", Queue)) == rows,
            TimeSpan.FromSeconds(30));

        await stop.CancelAsync();
        await running;

        drained.ShouldBeTrue("the two loops must drain the queue");

        host.Recorder.TotalRuns.ShouldBe(rows, "each payload is handled exactly once");
        host.Recorder.Runs.Count.ShouldBe(rows);
        host.Recorder.Runs.Values.ShouldAllBe(runs => runs == 1);
    }

    /// <summary>
    /// T1.3 / AC7: a consumer that died holding a lease leaves an `active` row; once
    /// `locked_until` passes, the row is claimed and run again (attempt two).
    /// </summary>
    [Fact]
    public async Task Consumer_Crash_LockedUntilExpiry_Requeues()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"crashed"}""",
            idempotencyKey: "crashed");

        await using var host = TasksTestHost.Start(_fixture, database);

        // The crashed consumer: it claimed the row and never completed it.
        var claimed = await host.Claimer.ClaimAsync(Queue, [TaskHandlerTypes.Recording], 1, Token);
        claimed.Count.ShouldBe(1);
        host.Recorder.TotalRuns.ShouldBe(0);

        await TaskSql.ExecuteAsync(
            owner,
            "update public.task set locked_until = now() - interval '1 second' where id = @id",
            ("id", claimed[0].Id));

        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        host.Recorder.Runs["crashed"].ShouldBe(1);
        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = 'crashed'")).ShouldBe("done");
        (await TaskSql.ScalarAsync<int>(owner, "select attempts from public.task where idempotency_key = 'crashed'")).ShouldBe(2);
    }

    /// <summary>
    /// §6: a handler that throws moves the row to `retry` with the backoff and the
    /// redacted error, and at the cap to `dead_letter` with `last_error` set —
    /// without a second poll ever running the row before `available_at`.
    /// </summary>
    [Fact]
    public async Task Consumer_Handler_Throws_Retries_Then_DeadLetters()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"poison"}""",
            idempotencyKey: "poison",
            maxAttempts: 2);

        await using var host = TasksTestHost.Start(_fixture, database);
        host.Recorder.Failure = new InvalidOperationException(TaskRunRecorder.PoisonMessage);

        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = 'poison'")).ShouldBe("retry");
        (await TaskSql.ScalarAsync<string>(owner, "select last_error from public.task where idempotency_key = 'poison'"))
            .ShouldBe($"InvalidOperationException: {TaskRunRecorder.PoisonMessage}");
        (await TaskSql.ScalarAsync<bool>(
            owner,
            "select (available_at > now()) and locked_until is null from public.task where idempotency_key = 'poison'"))
            .ShouldBeTrue("a retry waits for its backoff and releases the lease");

        // The backoff has not elapsed: the row is not due, so the next pass leaves it.
        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(0);
        host.Recorder.Runs["poison"].ShouldBe(1);

        // Make it due again and poll once more: the retry cap is reached.
        await TaskSql.ExecuteAsync(
            owner,
            "update public.task set available_at = now() where idempotency_key = 'poison'");

        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = 'poison'")).ShouldBe("dead_letter");
        (await TaskSql.ScalarAsync<int>(owner, "select attempts from public.task where idempotency_key = 'poison'")).ShouldBe(2);
        (await TaskSql.ScalarAsync<string>(owner, "select last_error from public.task where idempotency_key = 'poison'"))
            .ShouldBe($"InvalidOperationException: {TaskRunRecorder.PoisonMessage}");
    }

    /// <summary>
    /// §6 without an in-process failure branch: a row whose lease expired while it
    /// already used up its attempts is not run again — the reclaim moves it to
    /// `dead_letter` instead, so a handler that kills the worker (rather than
    /// throwing, which is the branch above) ends at the cap instead of looping for
    /// ever. The cap is also a claim predicate, so a capped row cannot come back
    /// either way.
    /// </summary>
    [Fact]
    public async Task Consumer_LeaseExpired_AtTheCap_DeadLetters_InsteadOfLooping()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        // The crashed consumer's row: claimed (attempts = max) and never completed.
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"killed"}""",
            idempotencyKey: "killed-the-worker",
            state: "active",
            attempts: 2,
            maxAttempts: 2,
            lockedUntil: DateTime.UtcNow.AddSeconds(-5));

        // …and a capped row someone put back in the queue: still not claimable.
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"capped"}""",
            idempotencyKey: "capped",
            attempts: 8,
            maxAttempts: 8);

        await using var host = TasksTestHost.Start(_fixture, database);

        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(0, "a row at the cap is not claimable");

        host.Recorder.TotalRuns.ShouldBe(0, "neither row may run the handler");

        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = 'killed-the-worker'"))
            .ShouldBe("dead_letter");
        var leaseError = await TaskSql.ScalarAsync<string>(
            owner,
            "select last_error from public.task where idempotency_key = 'killed-the-worker'");
        leaseError.ShouldNotBeNull().ShouldContain("lease expired");
        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = 'capped'"))
            .ShouldBe("queued");
    }

    /// <summary>
    /// T1.3's batch path: one claim stamps one lease deadline for the whole batch, so a
    /// batch that outlives it must not hand the rows it never started to the cap — they
    /// are released and redelivered with their attempts intact (the handler never ran),
    /// instead of eating one retry each until they dead-letter.
    /// </summary>
    [Fact]
    public async Task Consumer_Slow_Batch_Releases_Unstarted_Rows()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        // Explicit due times, a second apart: the claim orders by `available_at`
        // (task_claim_idx is in (queue, available_at) order) and two rows enqueued in
        // one instant — which is what a dispatcher batch does — are ties whose order
        // that index does not define.
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"first"}""",
            idempotencyKey: "slow-a",
            availableAt: DateTime.UtcNow.AddSeconds(-2));
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"second"}""",
            idempotencyKey: "slow-b",
            availableAt: DateTime.UtcNow.AddSeconds(-1));

        await using var host = TasksTestHost.Start(
            _fixture,
            database,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [TasksOptions.SectionName + ":VisibilityTimeoutSeconds"] = "1",
                [TasksOptions.SectionName + ":BatchSize"] = "2",
            });

        // The two rows are due in the order they were seeded, and the claim takes the
        // queue's oldest work first (task_claim_idx is in (queue, available_at) order).
        var dueOrder = await TaskSql.QueryAsync<string>(
            owner,
            "select idempotency_key from public.task where queue = @queue order by available_at, idempotency_key",
            ("queue", Queue));
        dueOrder.ShouldBe(["slow-a", "slow-b"]);

        // The handler outlives the one-second lease of the claim that took both rows.
        host.Recorder.Delay = TimeSpan.FromMilliseconds(1500);

        var processed = await host.Consumer.PollOnceAsync(Queue, Token);

        processed.ShouldBe(1, "only the row whose turn came was run");
        host.Recorder.Runs.Keys.ShouldBe(["first"], ignoreOrder: true);

        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = 'slow-b'"))
            .ShouldBe("queued", "the unstarted row went back to the queue, not to a spent attempt");
        (await TaskSql.ScalarAsync<int>(owner, "select attempts from public.task where idempotency_key = 'slow-b'"))
            .ShouldBe(0, "the attempt of a claim that never ran is given back");
        (await TaskSql.ScalarAsync<bool>(owner, "select locked_until is null from public.task where idempotency_key = 'slow-b'"))
            .ShouldBeTrue();

        // …and the row is claimable again right away (available_at = now()).
        (await host.Claimer.ClaimAsync(Queue, [TaskHandlerTypes.Recording], 1, Token)).Count.ShouldBe(1);
    }

    /// <summary>
    /// AC7 under a slow batch: a claim whose lease is already gone when its turn
    /// comes belongs to whoever reclaimed it, so this consumer must not run it — the
    /// completion's lease check would refuse the second `done`, but the handler would
    /// already have run. The lease check is monotonic (measured against the stamp
    /// taken before the claim, never the host clock — a clock that leads the database
    /// by the visibility timeout would read every fresh claim as expired), so this
    /// test drives the skip deterministically with a 1 ms visibility timeout: a real
    /// claim cannot wait in a batch longer than its own lease.
    /// </summary>
    [Fact]
    public async Task Consumer_Skips_Claim_Whose_Lease_Already_Expired()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = TasksTestHost.Start(_fixture, database);

        // A 1 s lease the claimer answers 1.2 s late: the batch arrives already
        // past its own deadline, which is exactly the "slow handler ahead of it
        // outlived the lease" case — without depending on host vs database clocks.
        var claimer = Substitute.For<ITaskClaimer>();
        claimer
            .ClaimAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await Task.Delay(1200);
                return (IReadOnlyList<TaskClaim>)
                [
                    new TaskClaim(
                        Guid.CreateVersion7(),
                        OrgId: null,
                        Queue: Queue,
                        TaskType: TaskHandlerTypes.Recording,
                        Payload: """{"value":"stale"}""",
                        IdempotencyKey: "stale",
                        ScheduleName: null,
                        FireAt: null,
                        State: "active",
                        Attempts: 1,
                        MaxAttempts: 8,
                        AvailableAt: DateTime.UtcNow.AddMinutes(-10),
                        LockedUntil: DateTime.UtcNow.AddSeconds(1),
                        Version: 7),
                ];
            });

        var leases = Substitute.For<ITaskLeaseWriter>();
        var consumer = new TaskConsumer(
            claimer,
            leases,
            host.Handlers,
            Substitute.For<Evidgate.BuildingBlocks.Persistence.Tenancy.ITenantScopeFactory>(),
            Options.Create(new TasksOptions { VisibilityTimeoutSeconds = 1 }),
            NullLogger<TaskConsumer>.Instance);

        (await consumer.PollOnceAsync(Queue, Token)).ShouldBe(0, "a stale claim is not processed");
        host.Recorder.TotalRuns.ShouldBe(0, "the handler must not run for a claim whose lease expired");

        await leases.DidNotReceiveWithAnyArgs().MarkDoneAsync(null!, Token);
        await leases.DidNotReceiveWithAnyArgs().MarkDeadLetterAsync(null!, string.Empty, Token);
    }

    /// <summary>
    /// 01.5.1 §8 S1 / §9 <c>Handler_OpensTenantScopeFromRow</c>: the consumer opens
    /// the tenant scope from the row's `org_id`, and RLS narrows the handler's SQL to
    /// that org — the handler counts one row while the table holds three across two
    /// orgs, and the row it writes carries the row's org.
    /// </summary>
    [Fact]
    public async Task Handler_OpensTenantScopeFromRow()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        var orgA = Guid.CreateVersion7();
        var orgB = Guid.CreateVersion7();

        await TaskSql.ExecuteAsync(
            owner,
            """
            insert into public.rls_probe (org_id, id, label)
            values (@a, @idA, 'seed-a'), (@b, @idB1, 'seed-b1'), (@b, @idB2, 'seed-b2')
            """,
            ("a", orgA),
            ("b", orgB),
            ("idA", Guid.CreateVersion7()),
            ("idB1", Guid.CreateVersion7()),
            ("idB2", Guid.CreateVersion7()));

        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Tenant,
            payload: """{"marker":"tenant"}""",
            orgId: orgA,
            idempotencyKey: "tenant");

        await using var host = TasksTestHost.Start(_fixture, database);

        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        host.Recorder.TenantRuns.ShouldBe(1);

        var written = await TaskSql.QueryAsync<Guid>(owner, "select org_id from public.rls_probe where label = 'tenant:seen=1'");
        written.ShouldHaveSingleItem().ShouldBe(orgA);

        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = 'tenant'")).ShouldBe("done");
    }

    /// <summary>
    /// Polls the assertion until it holds or the deadline passes; the concurrency test
    /// watches the database rather than sleeping a fixed time.
    /// </summary>
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(50, Token);
        }

        return await condition();
    }
}
