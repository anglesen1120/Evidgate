using Evidgate.BuildingBlocks.Persistence.Tasks;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 01.5.1 §7 / T11: the retention sweep of the mechanism's own table, end-to-end
/// through the registered `task.sweep` schedule — the schedule fires, the consumer
/// claims the fire, the handler deletes only the terminal rows past their retention,
/// and the fire itself is finished.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TaskSweepTests
{
    private const string SeedQueue = "sweep-seed";

    /// <summary>Just after the registered `task.sweep` cron, and far enough from the purge cron that only one fires.</summary>
    private static readonly DateTimeOffset SweepTime = new(2026, 9, 26, 3, 15, 30, TimeSpan.Zero);

    private readonly PostgresTasksFixture _fixture;

    public TaskSweepTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TaskSweep_DeletesOnly_Terminal_Rows_OlderThan_Retention()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        var old = DateTime.UtcNow.AddDays(-(EvidgateSweeps.TerminalRetentionDays + 10));
        var recent = DateTime.UtcNow.AddDays(-1);

        await TaskSql.SeedTaskAsync(owner, SeedQueue, TaskHandlerTypes.Recording, state: "done", idempotencyKey: "old-done", createdAt: old);
        await TaskSql.SeedTaskAsync(owner, SeedQueue, TaskHandlerTypes.Recording, state: "dead_letter", idempotencyKey: "old-dead", createdAt: old);
        await TaskSql.SeedTaskAsync(owner, SeedQueue, TaskHandlerTypes.Recording, state: "done", idempotencyKey: "young-done", createdAt: recent);

        // Not terminal: retention has no business with it, however old it is.
        await TaskSql.SeedTaskAsync(owner, SeedQueue, TaskHandlerTypes.Recording, state: "queued", idempotencyKey: "old-queued", createdAt: old);

        await using var host = TasksTestHost.Start(_fixture, database);

        (await host.Runner.RunOnceAsync(SweepTime, Token)).ShouldBe(1);
        (await host.Consumer.PollOnceAsync(EvidgateSweeps.MaintenanceQueue, Token)).ShouldBe(1);

        (await TaskSql.ScalarAsync<long>(owner, "select count(*) from public.task where idempotency_key in ('old-done', 'old-dead')"))
            .ShouldBe(0, "terminal rows past the retention window are deleted");
        (await TaskSql.ScalarAsync<long>(owner, "select count(*) from public.task where idempotency_key in ('young-done', 'old-queued')"))
            .ShouldBe(2, "a young terminal row and an old queued row stay");

        (await TaskSql.ScalarAsync<string>(
            owner,
            "select state from public.task where schedule_name = @name",
            ("name", EvidgateSweeps.TaskSweepSchedule))).ShouldBe("done");
    }

    /// <summary>
    /// The retention payload is a task row an operator can replay, so it is not trusted
    /// to stay in range: a negative day count would compute
    /// `created_at &lt; now() + n days` and delete fresh terminal rows, which is a
    /// data-loss path rather than a retention sweep. The window is clamped to at least
    /// one day.
    /// </summary>
    [Fact]
    public async Task TaskSweep_NonPositiveWindowDoesNotDeleteFreshRows()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        await TaskSql.SeedTaskAsync(
            owner,
            SeedQueue,
            TaskHandlerTypes.Recording,
            state: "done",
            idempotencyKey: "fresh-terminal",
            createdAt: DateTime.UtcNow.AddHours(-1));
        await TaskSql.SeedTaskAsync(
            owner,
            SeedQueue,
            TaskHandlerTypes.Recording,
            state: "done",
            idempotencyKey: "old-terminal",
            createdAt: DateTime.UtcNow.AddDays(-40));

        await TaskSql.SeedTaskAsync(
            owner,
            EvidgateSweeps.MaintenanceQueue,
            EvidgateSweeps.TaskSweepTaskType,
            payload: """{"older_than_days":-5}""",
            idempotencyKey: "sweep-negative-window");

        await using var host = TasksTestHost.Start(_fixture, database);

        (await host.Consumer.PollOnceAsync(EvidgateSweeps.MaintenanceQueue, Token)).ShouldBe(1);

        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.task where idempotency_key = 'fresh-terminal'"))
            .ShouldBe(1, "a fresh terminal row must survive a negative retention window");
        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.task where idempotency_key = 'old-terminal'"))
            .ShouldBe(0, "the clamped window still sweeps what is genuinely old");
    }
}
