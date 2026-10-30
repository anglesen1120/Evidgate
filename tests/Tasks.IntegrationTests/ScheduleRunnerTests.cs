using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 01.5.1-T1.4 and AC3: the Cronos schedule runner. A due occurrence becomes exactly
/// one task row whichever and however many workers compute it, a re-run of the same
/// pass inserts nothing, and the story's own two sweeps are registered with the cron,
/// queue and handler the plan fixes (T6.2's "schedule row visible in the registry").
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class ScheduleRunnerTests
{
    private const string Queue = "schedules";
    private const string ScheduleName = "test.hourly";

    /// <summary>The instant every test's pass runs as; the crons below are chosen around it.</summary>
    private static readonly DateTimeOffset Noon = new(2026, 9, 26, 12, 0, 30, TimeSpan.Zero);

    private readonly PostgresTasksFixture _fixture;

    public ScheduleRunnerTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Schedule_DueOccurrence_InsertsTask()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await using var host = StartHost(database, "0 12 * * *");

        (await host.Runner.RunOnceAsync(Noon, Token)).ShouldBe(1);

        (await TaskSql.ScalarAsync<string>(
            owner,
            "select task_type from public.task where schedule_name = @name",
            ("name", ScheduleName))).ShouldBe(TaskHandlerTypes.Recording);

        (await TaskSql.ScalarAsync<DateTime>(
            owner,
            "select fire_at from public.task where schedule_name = @name",
            ("name", ScheduleName))).ShouldBe(new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc));

        (await TaskSql.ScalarAsync<string>(
            owner,
            "select state from public.task where schedule_name = @name",
            ("name", ScheduleName))).ShouldBe("queued");

        // A schedule fire is a platform task: no tenant, so no scope for the handler.
        (await TaskSql.ScalarAsync<bool>(
            owner,
            "select org_id is null from public.task where schedule_name = @name",
            ("name", ScheduleName))).ShouldBeTrue();

        var payload = await TaskSql.ScalarAsync<string>(
            owner,
            "select payload::text from public.task where schedule_name = @name",
            ("name", ScheduleName));
        payload.ShouldNotBeNull().ShouldContain("sched");

        // The story's own daily sweeps are not due inside this window.
        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.task where queue = @queue",
            ("queue", EvidgateSweeps.MaintenanceQueue))).ShouldBe(0);
    }

    /// <summary>
    /// The window is `(now - lookback, now]`: an occurrence after `now` is not fired
    /// yet, and one exactly at `now` is (a pass that just crossed a minute boundary
    /// must not push the tick a whole poll interval later).
    /// </summary>
    [Fact]
    public async Task Schedule_NotYetDue_InsertsNothing()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await using var host = StartHost(database, "0 12 * * *");

        // 11:59:30 — the 12:00 occurrence is still in the future, so nothing fires.
        (await host.Runner.RunOnceAsync(Noon.AddSeconds(-60), Token)).ShouldBe(0);
        (await CountFiresAsync(owner)).ShouldBe(0);

        // Exactly at the occurrence: it fires. The window's upper bound is inclusive,
        // so a pass that just crossed the minute does not defer the tick.
        (await host.Runner.RunOnceAsync(Noon.AddSeconds(-30), Token)).ShouldBe(1);
        (await CountFiresAsync(owner)).ShouldBe(1);
    }

    /// <summary>
    /// 00 §5.3: nothing has to remember that a fire already happened — a second pass
    /// over the same window inserts nothing, so a restart or a second worker is
    /// harmless.
    /// </summary>
    [Fact]
    public async Task Schedule_Rerun_SameFire_InsertsNothing()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await using var host = StartHost(database, "0 12 * * *");

        (await host.Runner.RunOnceAsync(Noon, Token)).ShouldBe(1);
        (await host.Runner.RunOnceAsync(Noon, Token)).ShouldBe(0);
        (await CountFiresAsync(owner)).ShouldBe(1);
    }

    /// <summary>
    /// AC3: two workers computing the same five occurrences produce exactly five task
    /// rows, one per `(schedule_name, fire_at)` — no leader, no lock held across the
    /// pass, and no duplicate.
    /// </summary>
    [Fact]
    public async Task Schedule_TwoWorkers_OneTaskRow()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await using var host = StartHost(database, "* * * * *");

        var workers = Enumerable
            .Range(0, 2)
            .Select(_ => host.Runner.RunOnceAsync(Noon, Token))
            .ToArray();

        var inserted = await Task.WhenAll(workers);

        // Five occurrences in (11:55:30, 12:00:30]: 11:56 … 12:00.
        inserted.Sum().ShouldBe(5);
        (await CountFiresAsync(owner)).ShouldBe(5);
        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(distinct fire_at) from public.task where schedule_name = @name",
            ("name", ScheduleName))).ShouldBe(5);
    }

    /// <summary>
    /// T6.2 (and 01.5.1 §7): the two sweeps this story owns are registered with the
    /// values the plan fixes — the name doubles as the fire key, so it is part of the
    /// contract rather than a label.
    /// </summary>
    [Fact]
    public async Task Schedule_Registry_Lists_Sweeps()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = TasksTestHost.Start(_fixture, database);

        var schedules = host.Schedules.Schedules;
        schedules.Count.ShouldBe(3);

        var purge = schedules.Single(schedule => schedule.Name == EvidgateSweeps.IdempotencyPurgeSchedule);
        purge.Cron.ShouldBe(EvidgateSweeps.IdempotencyPurgeCron);
        purge.Queue.ShouldBe(EvidgateSweeps.MaintenanceQueue);
        purge.TaskType.ShouldBe(EvidgateSweeps.IdempotencyPurgeTaskType);

        var sweep = schedules.Single(schedule => schedule.Name == EvidgateSweeps.TaskSweepSchedule);
        sweep.Cron.ShouldBe(EvidgateSweeps.TaskSweepCron);
        sweep.Queue.ShouldBe(EvidgateSweeps.MaintenanceQueue);
        sweep.TaskType.ShouldBe(EvidgateSweeps.TaskSweepTaskType);

        // The registered schedules are exactly the types the worker has handlers for,
        // so a fire is always claimable by the worker that made it.
        host.Handlers.TaskTypes.ShouldBe(
            [EvidgateSweeps.IdempotencyPurgeTaskType, EvidgateSweeps.TaskSweepTaskType, Evidgate.BuildingBlocks.Persistence.Audit.AuditSweeps.VerifyChainTaskType, TaskHandlerTypes.Recording, TaskHandlerTypes.Tenant, TaskHandlerTypes.Blocking],
            ignoreOrder: true);
    }

    private TasksTestHost StartHost(string database, string cron) =>
        TasksTestHost.Start(
            _fixture,
            database,
            services: services => services.AddTaskSchedule(
                new ScheduleDefinition(ScheduleName, cron, Queue, TaskHandlerTypes.Recording, _ => """{"value":"sched"}""")));

    private static Task<long> CountFiresAsync(string ownerConnectionString) =>
        TaskSql.ScalarAsync<long>(
            ownerConnectionString,
            "select count(*) from public.task where schedule_name = @name",
            ("name", ScheduleName));
}
