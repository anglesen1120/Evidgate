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

/// <summary>
/// The world and driving half of the forced-incomplete cases: the seeded world composed with the production queue
/// mechanism and a fake clock, the sweep/dispatch/reconcile drivers, and the row reads each case asserts on.
/// </summary>
public sealed partial class ReconcileForcedIncompleteTests
{
    // ------------------------------------------------------------------ world and driving

    /// <summary>The queue the sweep's own fire lands on (the platform sweeps share it).</summary>
    private const string MaintenanceQueue = "maintenance";

    /// <summary>The queue a reconcile lands on when the dispatcher has no route for its type.</summary>
    private const string DefaultQueue = "default";

    /// <summary>
    /// One migrated database with an org, a project, a run and one producer slot, composed with the production queue
    /// mechanism and a <see cref="FakeTimeProvider"/> so a case drives the schedule instead of waiting for it.
    /// </summary>
    /// <param name="clock">The fake clock the schedule's ticks read.</param>
    /// <param name="database">The case's private database: the sweep, the dispatcher and the task queue are database-global.</param>
    /// <param name="planned">How many executions the seeded run carries.</param>
    /// <param name="slots">How many producer slots the seeded run carries.</param>
    private async Task<IngestTestWorld> StartAsync(
        FakeTimeProvider clock,
        string database,
        int planned = 2,
        int slots = 1)
    {
        var databaseName = new DatabaseNameHolder();

        var world = await IngestTestWorld.StartAsync(
            _fixture,
            IngestTestWorld.Writer(),
            planned: planned,
            slots: slots,
            database: database,
            services: services =>
            {
                services.AddEvidgateTasks(new ConfigurationBuilder().Build());
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);

                // The fixture mints no login for evidgate_worker_sched: the double opens a superuser connection to this
                // database and switches to the sweep role, so grants and row level security are evaluated as that role.
                services.RemoveAll<IWorkerSchedDataSource>();
                services.AddSingleton<IWorkerSchedDataSource>(_ => new SweepRoleDataSource(_fixture, databaseName));
            });

        databaseName.Name = world.Database;
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

    /// <summary>A cron-aligned instant, so every fire lands on an exact ten-minute mark.</summary>
    private static FakeTimeProvider AlignedClock() =>
        new(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>Runs one schedule pass at the fake instant and waits for its handler to finish (it only enqueues).</summary>
    private static async Task SweepAsync(IngestTestWorld world, FakeTimeProvider clock)
    {
        (await world.Host.Services.GetRequiredService<TaskScheduleRunner>().RunOnceAsync(clock.GetUtcNow(), Token))
            .ShouldBeGreaterThan(0, "the schedule pass fires the registered sweeps");

        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await TaskStateAsync(world, RunReconcileContract.DeadlineSweepTaskType) == "done")
            {
                return;
            }

            await consumer.PollOnceAsync(MaintenanceQueue, Token);
        }

        (await TaskStateAsync(world, RunReconcileContract.DeadlineSweepTaskType)).ShouldBe(
            "done",
            "the maintenance consumer runs the sweep task to done");
    }

    /// <summary>Dispatches once and checks the sweep's triggers reached the queue.</summary>
    private static async Task DispatchAsync(IngestTestWorld world, int expectedReconciles)
    {
        (await world.Host.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(Token)).Enqueued
            .ShouldBeGreaterThanOrEqualTo(expectedReconciles, "the sweep's triggers reach the queue");
    }

    /// <summary>Claims on the reconcile queue until the expected attempts ran and completed.</summary>
    private static async Task ReconcileAsync(IngestTestWorld world, int expectedReconciles)
    {
        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        for (var attempt = 0; attempt < 3 * expectedReconciles + 3; attempt++)
        {
            if (await CountDoneReconcilesAsync(world) >= expectedReconciles)
            {
                return;
            }

            await consumer.PollOnceAsync(DefaultQueue, Token);
        }

        (await CountDoneReconcilesAsync(world)).ShouldBeGreaterThanOrEqualTo(
            expectedReconciles,
            "every queued reconcile is claimed and completed by the consumer");
    }

    /// <summary>Dispatches, then claims on the reconcile queue until the expected attempts ran and completed.</summary>
    private static async Task DispatchAndReconcileAsync(IngestTestWorld world, int expectedReconciles)
    {
        await DispatchAsync(world, expectedReconciles);
        await ReconcileAsync(world, expectedReconciles);
    }

    private static Task<long> CountDoneReconcilesAsync(IngestTestWorld world) =>
        world.CountAsync(
            "task",
            where: $"task_type = '{RunReconcileContract.OutboxTaskType}' and state = 'done'");

    /// <summary>The state of the newest task row of one type, or null when the type never ran.</summary>
    private static Task<string?> TaskStateAsync(IngestTestWorld world, string taskType) =>
        world.ScalarAsync<string?>(
            $"select state from public.task where task_type = '{taskType}' order by created_at desc limit 1");

    private static Task<long> DeadlineRowsAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<long>(
            "select count(*) from public.outbox where idempotency_key like "
            + $"'{RunReconcileContract.DeadlineIdempotencyKey(runId)}%'");

    private static Task<string> StateAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>($"select state from public.run where id = '{runId:D}'");

    private static Task<Guid?> PointerAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<Guid?>($"select current_evidence_revision_id from public.run where id = '{runId:D}'");

    private static Task<string> CompletenessAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>(
            "select completeness from public.evidence_revision "
            + $"where org_id = '{world.Org}' and run_id = '{runId:D}' order by revision_no desc limit 1");

    private static Task<string> MissingAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>(
            "select coalesce(array_to_string(missing, ','), '') from public.evidence_revision "
            + $"where org_id = '{world.Org}' and run_id = '{runId:D}' order by revision_no desc limit 1");

    private static Task<long> RevisionCountAsync(IngestTestWorld world, Guid runId) =>
        world.CountAsync("evidence_revision", where: $"org_id = '{world.Org}' and run_id = '{runId:D}'");

    private static Task<long> StateChangeAuditCountAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<long>(
            $"select count(*) from public.audit_log where org_id = '{world.Org}' "
            + $"and action = '{RunStateTransitionContract.AuditAction}' and object_type = 'run' "
            + $"and object_id = '{runId:D}' and actor_type = 'system'");

    private Task SetRunStateAsync(IngestTestWorld world, Guid runId, string state) =>
        RunStateWalk.MoveAsync(_fixture, world.Database, runId, state);

    /// <summary>
    /// Closes every slot of the run with a matching zero declaration, so the comparison names nothing. The update
    /// answers the rows it touched: a miss (the fixture names its slots from 1, not 0) would leave the slot open and
    /// make the open-shard reason masquerade as this case's subject.
    /// </summary>
    private Task<long> CloseSlotAsync(IngestTestWorld world, Guid runId) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            "with closed as (update public.producer_slot "
            + "set closed_at = now(), declared_last_sequence = 0, declared_count = 0 "
            + $"where org_id = '{world.Org}' and run_id = '{runId:D}' returning 1) "
            + "select count(*) from closed");

    /// <summary>Moves the deadline into the database's past, the only clock the scan and the attempt compare against.</summary>
    private Task PassCollectionDeadlineAsync(IngestTestWorld world, Guid runId) =>
        _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set collection_deadline_at = now() - interval '1 second' where id = '{runId:D}'");

    /// <summary>Moves the deadline a day ahead, for the cases that must stay inside it.</summary>
    private Task FutureDeadlineAsync(IngestTestWorld world, Guid runId) =>
        _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set collection_deadline_at = now() + interval '1 day' where id = '{runId:D}'");
}
