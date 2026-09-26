using Evidgate.BuildingBlocks.Persistence.Tasks;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 01.4.2-T2.1 / AC7 and 01.5.1-T1.1: the claim helper against real PostgreSQL —
/// two or more concurrent claimers hand each row to exactly one of them, the claim
/// respects `available_at`/`state`, it leaves a task type this process cannot run
/// alone, and the visibility timeout makes a lease that ran out claimable again.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class ClaimHelperTests
{
    private const string Queue = "claims";

    private static readonly string[] RecordingTypes = [TaskHandlerTypes.Recording];

    private readonly PostgresTasksFixture _fixture;

    public ClaimHelperTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// AC7 (F6): with N=4 claimers on one queue, every seeded row is claimed exactly
    /// once — the union of the batches is the whole queue and no row appears in two
    /// batches, which is what `for update skip locked` buys over a plain
    /// `for update`.
    /// </summary>
    [Fact]
    public async Task Concurrent_Claimers_Split_Rows()
    {
        const int rows = 24;
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        for (var index = 0; index < rows; index++)
        {
            await TaskSql.SeedTaskAsync(owner, Queue, TaskHandlerTypes.Recording, idempotencyKey: $"seed:{index}");
        }

        await using var host = TasksTestHost.Start(_fixture, database, maximumPoolSize: 8);

        // The four claimers start together (each call runs to its first await as
        // soon as it is made), so their claims really overlap on the queue.
        var claimers = Enumerable
            .Range(0, 4)
            .Select(_ => host.Claimer.ClaimAsync(Queue, RecordingTypes, rows, Token))
            .ToArray();

        var batches = await Task.WhenAll(claimers);

        var claimed = batches.SelectMany(batch => batch).ToList();
        claimed.Count.ShouldBe(rows, "every seeded row is claimed once");
        claimed.Select(claim => claim.Id).Distinct().Count().ShouldBe(rows, "no row may be claimed twice");
        claimed.ShouldAllBe(claim => claim.State == "active");
        claimed.ShouldAllBe(claim => claim.Attempts == 1, "attempts is incremented by the claim itself");

        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.task where queue = @queue and state = 'active' and attempts = 1",
            ("queue", Queue))).ShouldBe(rows);
    }

    /// <summary>
    /// T1.1: the claim takes exactly the rows that are due and leasable — nothing
    /// scheduled for later, nothing terminal, nothing another consumer holds.
    /// </summary>
    [Fact]
    public async Task Claim_RespectsAvailableAtAndState()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        var dueKey = $"due:{Guid.NewGuid():N}";
        var retryKey = $"retry:{Guid.NewGuid():N}";

        await TaskSql.SeedTaskAsync(owner, Queue, TaskHandlerTypes.Recording, idempotencyKey: dueKey);
        await TaskSql.SeedTaskAsync(owner, Queue, TaskHandlerTypes.Recording, idempotencyKey: retryKey, state: "retry");
        await TaskSql.SeedTaskAsync(owner, Queue, TaskHandlerTypes.Recording, idempotencyKey: "future", availableAt: DateTime.UtcNow.AddHours(1));
        await TaskSql.SeedTaskAsync(owner, Queue, TaskHandlerTypes.Recording, idempotencyKey: "done", state: "done");
        await TaskSql.SeedTaskAsync(owner, Queue, TaskHandlerTypes.Recording, idempotencyKey: "leased", state: "active", lockedUntil: DateTime.UtcNow.AddHours(1));
        await TaskSql.SeedTaskAsync(owner, "another-queue", TaskHandlerTypes.Recording, idempotencyKey: "other-queue");

        await using var host = TasksTestHost.Start(_fixture, database);
        var batch = await host.Claimer.ClaimAsync(Queue, RecordingTypes, 50, Token);

        batch.Select(claim => claim.IdempotencyKey).ShouldBe([dueKey, retryKey], ignoreOrder: true);
    }

    /// <summary>
    /// 01.5.1 §11: a task type this worker has no handler for must stay `queued`
    /// with its attempts untouched — otherwise a payload a later release introduces
    /// would burn its retries and land in `dead_letter` while nobody can run it.
    /// </summary>
    [Fact]
    public async Task Claim_Leaves_Undeployed_Type_Queued()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(owner, Queue, TaskHandlerTypes.Unhandled, idempotencyKey: "unhandled");

        await using var host = TasksTestHost.Start(_fixture, database);
        var batch = await host.Claimer.ClaimAsync(Queue, RecordingTypes, 50, Token);

        batch.ShouldBeEmpty();

        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = 'unhandled'"))
            .ShouldBe("queued");
        (await TaskSql.ScalarAsync<int>(owner, "select attempts from public.task where idempotency_key = 'unhandled'"))
            .ShouldBe(0);
    }

    /// <summary>
    /// T1.3 / §6: a lease that ran out without a completion makes the row claimable
    /// again — the crashed-consumer path, here made deterministic by moving the
    /// lease into the past instead of waiting for a real timeout.
    /// </summary>
    [Fact]
    public async Task ReclaimExpiredLease_Makes_Row_Claimable()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(owner, Queue, TaskHandlerTypes.Recording, idempotencyKey: "crash");

        await using var host = TasksTestHost.Start(_fixture, database);

        var first = await host.Claimer.ClaimAsync(Queue, RecordingTypes, 1, Token);
        first.Count.ShouldBe(1);

        // The lease is live: nobody else may claim the row.
        (await host.Claimer.ClaimAsync(Queue, RecordingTypes, 1, Token)).ShouldBeEmpty();

        await TaskSql.ExecuteAsync(
            owner,
            "update public.task set locked_until = now() - interval '1 second' where id = @id",
            ("id", first[0].Id));

        var second = await host.Claimer.ClaimAsync(Queue, RecordingTypes, 1, Token);

        second.Count.ShouldBe(1);
        second[0].Id.ShouldBe(first[0].Id);
        second[0].Attempts.ShouldBe(2, "the re-claim is a second attempt of the same row");
        second[0].State.ShouldBe("active");
    }

    /// <summary>
    /// 00 §5.7 P4 / 01.5.1 §9 <c>Claim_UsesTaskClaimIndex</c>: the claim statement
    /// walks `task_claim_idx` and the lease reclaim walks `task_active_idx`. The
    /// assertion runs `EXPLAIN` on the very statements production runs, with
    /// sequential scans disabled so a small table cannot make the planner prefer one.
    /// </summary>
    [Fact]
    public async Task Claim_UsesTaskClaimIndex()
    {
        const int rows = 30;
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        for (var index = 0; index < rows; index++)
        {
            await TaskSql.SeedTaskAsync(owner, Queue, TaskHandlerTypes.Recording, idempotencyKey: $"seed:{index}");
        }

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(Token);
        await TaskSql.ExecuteAsync(connection, "set enable_seqscan = off");

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"explain (format json) {TaskQueueSql.ClaimBatch}";
            command.Parameters.Add(new NpgsqlParameter("queue", Queue));
            command.Parameters.Add(new NpgsqlParameter("task_types", RecordingTypes));
            command.Parameters.Add(new NpgsqlParameter("batch_size", 50));
            command.Parameters.Add(new NpgsqlParameter("visibility_timeout", 300));

            var plan = (string)(await command.ExecuteScalarAsync(Token))!;
            plan.ShouldContain("task_claim_idx");

            // The reclaim runs once per poll per queue, so it is the statement whose
            // plan actually has to be an index scan: its predicate is one queue and
            // one state, which is exactly `task_active_idx`.
            await using var reclaim = connection.CreateCommand();
            reclaim.CommandText = $"explain (format json) {TaskQueueSql.ReclaimExpiredLeases}";
            reclaim.Parameters.Add(new NpgsqlParameter("queue", Queue));
            reclaim.Parameters.Add(new NpgsqlParameter("batch_size", 50));

            var reclaimPlan = (string)(await reclaim.ExecuteScalarAsync(Token))!;
            reclaimPlan.ShouldContain("task_active_idx");
        }
        finally
        {
            await TaskSql.ExecuteAsync(connection, "set enable_seqscan = on");
        }
    }
}
