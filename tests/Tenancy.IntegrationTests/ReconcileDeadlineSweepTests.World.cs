using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReconcileDeadlineSweepTests
{
    // ------------------------------------------------------------------ world and driving

    /// <summary>
    /// One migrated database with an org, a project, a run and one producer slot, composed with the production queue
    /// mechanism and a <see cref="FakeTimeProvider"/> so a case drives the schedule instead of waiting for it.
    /// </summary>
    /// <param name="database">
    /// A private database for the cases that drive the sweep: its scan names every overdue run in the database and the
    /// dispatcher reconciles whatever it queued, so sharing one would decide another test's runs.
    /// </param>
    private async Task<IngestTestWorld> StartAsync(FakeTimeProvider clock, string? database = null)
    {
        var holder = new DatabaseNameHolder();

        var world = await IngestTestWorld.StartAsync(
            _fixture,
            IngestTestWorld.Writer(),
            services: services =>
            {
                services.AddEvidgateTasks(new ConfigurationBuilder().Build());
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);

                // The fixture mints no login for evidgate_worker_sched, and the database name is only known once the
                // world has started: the double opens a superuser connection to it and switches to the sweep role, the
                // way InviteSchemaTests exercises the same role. Grants and row level security are evaluated against the
                // switched role, so the scan really runs with the sweep role's privileges.
                services.RemoveAll<IWorkerSchedDataSource>();
                services.AddSingleton<IWorkerSchedDataSource>(_ => new SweepRoleDataSource(_fixture, holder));
            },
            database: database);

        holder.Name = world.Database;
        return world;
    }

    /// <summary>The database name, filled in once the world exists and read when the sweep first opens a connection.</summary>
    private sealed class DatabaseNameHolder
    {
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>Opens the sweep role's connection: a superuser connection switched with <c>set role</c>.</summary>
    private sealed class SweepRoleDataSource(PostgresTenancyFixture fixture, DatabaseNameHolder database)
        : IWorkerSchedDataSource
    {
        public async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            var connection = await fixture.OpenSuperuserAsync(database.Name).ConfigureAwait(false);
            try
            {
                await using var switchRole = connection.CreateCommand();
                switchRole.CommandText = "set role evidgate_worker_sched";
                await switchRole.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return connection;
        }
    }

    /// <summary>
    /// A fixed, cron-aligned instant. The runner composes the occurrences due in its lookback window (300 s) from the
    /// schedule's cron, so a boundary-aligned start puts every fire on an exact ten-minute mark and makes a second
    /// advance land on the next one — with `UtcNow` neither property holds.
    /// </summary>
    private static FakeTimeProvider AlignedClock() =>
        new(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>Runs one schedule pass at the fake instant (which only ENQUEUES the sweep's task row).</summary>
    private static Task<int> FireAsync(IngestTestWorld world, FakeTimeProvider clock) =>
        world.Host.Services.GetRequiredService<TaskScheduleRunner>().RunOnceAsync(clock.GetUtcNow(), Token);

    /// <summary>
    /// The whole platform path up to the outbox: one schedule pass, then the maintenance consumer claims the sweep row
    /// and runs its handler, which is what writes the reconcile rows.
    /// </summary>
    private static async Task SweepAsync(IngestTestWorld world, FakeTimeProvider clock)
    {
        (await FireAsync(world, clock)).ShouldBeGreaterThan(
            0,
            customMessage: "the schedule pass fires the registered sweeps");

        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await IsDoneAsync(world, RunReconcileContract.DeadlineSweepTaskType))
            {
                return;
            }

            await consumer.PollOnceAsync(MaintenanceQueue, Token);
        }

        (await IsDoneAsync(world, RunReconcileContract.DeadlineSweepTaskType)).ShouldBeTrue(
            customMessage: "the maintenance consumer runs the sweep task to done");
    }

    /// <summary>Moves the outbox rows into the task queue: the dispatcher is what a reconcile task needs to exist.</summary>
    private static Task<OutboxDispatchResult> DispatchAsync(IngestTestWorld world) =>
        world.Host.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(Token);

    /// <summary>
    /// One schedule fire and its handler, observed by the count of completed sweep rows: a case that fires twice must
    /// prove the second handler ran, which a "the newest row is done" read cannot — the first fire's row already is.
    /// <paramref name="pageLimit"/> narrows the queued fire's payload, the operator-replayable field a paging case
    /// needs, before the consumer claims it.
    /// </summary>
    private async Task SweepOnceAsync(IngestTestWorld world, FakeTimeProvider clock, int? pageLimit = null)
    {
        var completed = await CountDoneAsync(world, RunReconcileContract.DeadlineSweepTaskType);

        (await FireAsync(world, clock)).ShouldBeGreaterThan(
            0,
            customMessage: "the schedule pass fires the registered sweeps");

        if (pageLimit is { } limit)
        {
            await _fixture.ExecuteAsync(
                world.Database,
                $"update public.task set payload = '{{\"limit\":{limit}}}'::jsonb "
                + $"where task_type = '{RunReconcileContract.DeadlineSweepTaskType}' and state <> 'done'");

            (await world.ScalarAsync<int>(
                "select (payload ->> 'limit')::int from public.task "
                + $"where task_type = '{RunReconcileContract.DeadlineSweepTaskType}' and state <> 'done' "
                + "order by created_at desc limit 1"))
                .ShouldBe(limit, customMessage: "the queued fire carries the narrowed page size");
        }

        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (await CountDoneAsync(world, RunReconcileContract.DeadlineSweepTaskType) > completed)
            {
                return;
            }

            await consumer.PollOnceAsync(MaintenanceQueue, Token);
        }

        (await CountDoneAsync(world, RunReconcileContract.DeadlineSweepTaskType)).ShouldBeGreaterThan(
            completed,
            customMessage: "the maintenance consumer runs this fire's sweep task to done");
    }

    /// <summary>Dispatches, then claims on the reconcile queue until the expected tasks ran and completed.</summary>
    private static async Task DispatchAndReconcileAsync(IngestTestWorld world, int expectedReconciles)
    {
        (await DispatchAsync(world)).Enqueued.ShouldBeGreaterThanOrEqualTo(
            expectedReconciles,
            customMessage: "the sweep's triggers reach the queue");

        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        for (var attempt = 0; attempt < 3 * expectedReconciles + 3; attempt++)
        {
            if (await CountDoneAsync(world, RunReconcileContract.OutboxTaskType) >= expectedReconciles)
            {
                return;
            }

            await consumer.PollOnceAsync(DefaultQueue, Token);
        }

        (await CountDoneAsync(world, RunReconcileContract.OutboxTaskType)).ShouldBeGreaterThanOrEqualTo(
            expectedReconciles,
            customMessage: "every queued reconcile is claimed and completed by the consumer");
    }

    /// <summary>The state of the newest task row of one type, or null when the type never ran.</summary>
    private static async Task<string?> TaskStateAsync(IngestTestWorld world, string taskType) =>
        await world.ScalarAsync<string?>(
            $"select state from public.task where task_type = '{taskType}' order by created_at desc limit 1");

    private static async Task<bool> IsDoneAsync(IngestTestWorld world, string taskType) =>
        await TaskStateAsync(world, taskType) == "done";

    /// <summary>How many rows of one type ever reached <c>done</c>.</summary>
    private static Task<long> CountDoneAsync(IngestTestWorld world, string taskType) =>
        world.CountAsync("task", where: $"task_type = '{taskType}' and state = 'done'");

    /// <summary>How many reconcile task rows the dispatcher created (the queue's side of the one-key contract).</summary>
    private static Task<long> CountQueueRowsAsync(IngestTestWorld world) =>
        world.CountAsync("task", where: $"task_type = '{RunReconcileContract.OutboxTaskType}'");

    private static Task<long> DeadlineRowsAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<long>(
            "select count(*) from public.outbox where idempotency_key = "
            + $"'{RunReconcileContract.DeadlineIdempotencyKey(runId)}'");

    /// <summary>How many runs carry a collection-deadline trigger in the outbox, whatever their tenant.</summary>
    private static Task<long> QueuedRunCountAsync(IngestTestWorld world) =>
        world.CountAsync("outbox", where: "idempotency_key like 'orchestrator:run:%:reconcile:deadline'");

    private static Task<Guid?> OutboxOrgAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<Guid?>(
            "select org_id from public.outbox where idempotency_key = "
            + $"'{RunReconcileContract.DeadlineIdempotencyKey(runId)}'");

    private static Task<string> StateAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>($"select state from public.run where id = '{runId:D}'");

    private static Task<string> MissingAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>(
            "select coalesce(array_to_string(missing, ','), '') from public.evidence_revision "
            + $"where org_id = '{world.Org}' and run_id = '{runId:D}' order by revision_no desc limit 1");

    private static Task<long> RevisionCountAsync(IngestTestWorld world) => world.CountAsync("evidence_revision");

    /// <summary>How many revisions one tenant owns: a cross-tenant write would show up on the wrong org.</summary>
    private static Task<long> RevisionCountForAsync(IngestTestWorld world, Guid orgId) =>
        world.CountAsync("evidence_revision", where: $"org_id = '{orgId:D}'");

    private Task SetRunStateAsync(IngestTestWorld world, Guid runId, string state) =>
        RunStateWalk.MoveAsync(_fixture, world.Database, runId, state);

    /// <summary>
    /// Moves the deadline into the DB past. The scan compares it with <c>now()</c> in the database, so this column is
    /// the only clock a case can set — the fake clock moves the ticks, never this comparison.
    /// </summary>
    private Task PassCollectionDeadlineAsync(IngestTestWorld world, Guid runId) =>
        _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set collection_deadline_at = now() - interval '1 second' where id = '{runId:D}'");

    /// <summary>Puts one run in the state the scan names — collectable and past its deadline — in two statements.</summary>
    private async Task MarkOverdueAsync(IngestTestWorld world, Guid runId)
    {
        await RunStateWalk.MoveAsync(_fixture, world.Database, runId, "collecting");
        await PassCollectionDeadlineAsync(world, runId);
    }

    /// <summary>Moves the deadline a day ahead, for the case that must stay before it.</summary>
    private Task FutureDeadlineAsync(IngestTestWorld world, Guid runId) =>
        _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set collection_deadline_at = now() + interval '1 day' where id = '{runId:D}'");
}
