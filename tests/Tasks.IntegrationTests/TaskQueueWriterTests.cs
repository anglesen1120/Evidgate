using Evidgate.BuildingBlocks.Persistence.Tasks;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 01.5.1-T1.1/T2.2: the enqueue side. The row must commit with the caller's
/// transaction and vanish with its rollback, and the two unique keys of the table
/// must make a repeated enqueue a no-op — that is what turns at-least-once delivery
/// (A10) into exactly one piece of work.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TaskQueueWriterTests
{
    private const string Queue = "writer";

    private readonly PostgresTasksFixture _fixture;

    public TaskQueueWriterTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The C7 property applied to the queue: an enqueue on a transaction that rolls
    /// back is not work anyone will ever run.
    /// </summary>
    [Fact]
    public async Task Enqueue_InRolledBackTransaction_LeavesNoTask()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await using var host = TasksTestHost.Start(_fixture, database);

        await using (var connection = new NpgsqlConnection(_fixture.AppConnectionString(database)))
        {
            await connection.OpenAsync(Token);
            await using var transaction = await connection.BeginTransactionAsync(Token);

            var id = await host.Writer.EnqueueAsync(
                connection,
                transaction,
                new TaskEnqueue
                {
                    Queue = Queue,
                    TaskType = TaskHandlerTypes.Recording,
                    PayloadJson = """{"value":"rolled-back"}""",
                    IdempotencyKey = "rolled-back",
                },
                Token);

            id.ShouldNotBeNull();

            // Invisible outside the transaction (the owner connection reads 0)...
            (await TaskSql.ScalarAsync<long>(
                owner,
                "select count(*) from public.task where idempotency_key = 'rolled-back'")).ShouldBe(0);

            await transaction.RollbackAsync(Token);
        }

        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.task where idempotency_key = 'rolled-back'")).ShouldBe(0);
    }

    /// <summary>
    /// The outbox dispatcher's case (AC2): the same work key twice is one task row,
    /// and the second call reports that it inserted nothing.
    /// </summary>
    [Fact]
    public async Task Enqueue_SameIdempotencyKey_Twice_InsertsOneRow()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await using var host = TasksTestHost.Start(_fixture, database);

        var task = new TaskEnqueue
        {
            Queue = Queue,
            TaskType = TaskHandlerTypes.Recording,
            PayloadJson = """{"value":"once"}""",
            IdempotencyKey = "outbox:11111111-1111-7111-8111-111111111111",
            OrgId = Guid.CreateVersion7(),
        };

        (await host.Writer.EnqueueAsync(task, Token)).ShouldNotBeNull();
        (await host.Writer.EnqueueAsync(task, Token)).ShouldBeNull();

        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.task where idempotency_key = @key",
            ("key", task.IdempotencyKey))).ShouldBe(1);
    }

    /// <summary>
    /// AC3's storage guarantee: concurrent summary fires of one occurrence are one
    /// task row, whether they lose on the fire key or on the derived idempotency key
    /// — the two are the same fact, and the writer dedupes on both.
    /// </summary>
    [Fact]
    public async Task Enqueue_ScheduledFire_Concurrently_KeepsOneRow()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await using var host = TasksTestHost.Start(_fixture, database);

        var fireAt = new DateTimeOffset(2026, 9, 26, 3, 0, 0, TimeSpan.Zero);
        var task = new TaskEnqueue
        {
            Queue = Queue,
            TaskType = TaskHandlerTypes.Recording,
            PayloadJson = """{"value":"fire"}""",
            IdempotencyKey = TaskIdempotencyKeys.ScheduleFire("test.schedule", fireAt),
            ScheduleName = "test.schedule",
            FireAt = fireAt.UtcDateTime,
        };

        var enqueues = Enumerable
            .Range(0, 4)
            .Select(_ => host.Writer.EnqueueAsync(task, Token))
            .ToArray();

        var ids = await Task.WhenAll(enqueues);

        ids.Count(id => id is not null).ShouldBe(1, "exactly one of the concurrent fires inserts the row");
        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.task where schedule_name = 'test.schedule'")).ShouldBe(1);
    }
}
