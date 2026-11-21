using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Shouldly;

namespace Tenancy.IntegrationTests;

public sealed partial class ReconcileDeadlineRearmTests
{
    // ------------------------------------------------------------------ world and driving

    /// <summary>
    /// A world in <paramref name="database"/> with an org, a project, a run and one producer slot, composed with the
    /// production queue mechanism and a <see cref="FakeTimeProvider"/> so a case drives the schedule instead of
    /// waiting. Each case owns its database: the sweeps, the dispatcher and the consumer work through the whole queue.
    /// </summary>
    private async Task<IngestTestWorld> StartAsync(FakeTimeProvider clock, string database, IngestLogSink? sink = null)
    {
        var name = new DatabaseNameHolder { Name = database };

        var world = await IngestTestWorld.StartAsync(
            _fixture,
            IngestTestWorld.Writer(),
            database: database,
            services: services =>
            {
                services.AddEvidgateTasks(new ConfigurationBuilder().Build());
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);

                if (sink is not null)
                {
                    services.AddLogging(logging => logging.AddProvider(sink));
                }

                // The fixture mints no login for evidgate_worker_sched: the double opens a superuser connection and
                // switches to the sweep role, the way the sweep suite exercises the same role.
                services.RemoveAll<IWorkerSchedDataSource>();
                services.AddSingleton<IWorkerSchedDataSource>(_ => new SweepRoleDataSource(_fixture, name));
            });

        return world;
    }

    /// <summary>The test's database name, read when the sweep first opens a connection.</summary>
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

    /// <summary>A fixed, cron-aligned instant, so each advance lands on the next ten-minute mark.</summary>
    private static FakeTimeProvider AlignedClock() =>
        new(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>Advances to the next tick, fires the schedule, and runs this fire's sweep handler to done.</summary>
    private static async Task SweepAsync(IngestTestWorld world, FakeTimeProvider clock)
    {
        clock.Advance(TimeSpan.FromMinutes(10));

        (await world.Host.Services.GetRequiredService<TaskScheduleRunner>().RunOnceAsync(clock.GetUtcNow(), Token))
            .ShouldBeGreaterThan(0, customMessage: "the schedule pass fires the registered sweeps");

        var completed = await CountDoneAsync(world, RunReconcileContract.DeadlineSweepTaskType);
        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        for (var attempt = 0; attempt < 10; attempt++)
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

    /// <summary>Moves the outbox rows into the task queue, which is what a reconcile attempt needs to exist.</summary>
    private static async Task DispatchAsync(IngestTestWorld world)
    {
        await world.Host.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(Token);
    }

    /// <summary>
    /// Spends the run's deadline attempt the way a crashing consumer does: the attempt is at its cap and its lease
    /// has run out, so the queue's own reclaim — not a writer call — takes it to <c>dead_letter</c>, leaving the run
    /// untouched exactly as a failing attempt would.
    /// </summary>
    private async Task DeadLetterAttemptAsync(IngestTestWorld world, Guid runId)
    {
        var claims = await ClaimReconcilesAsync(world, expected: 1);
        var claim = claims.Single(row => RunIdOf(row) == runId);

        await ForceDeadLetterAsync(world, claim);
    }

    /// <summary>
    /// Drives one claimed attempt through retry exhaustion: one try allowed and already spent, lease expired, then the
    /// reclaim that gives up at the cap. Writing those two columns is the only shortcut; the transition stays the
    /// queue's.
    /// </summary>
    private async Task ForceDeadLetterAsync(IngestTestWorld world, TaskClaim claim)
    {
        await _fixture.ExecuteAsync(world.Database, $"""
            update public.task
               set max_attempts = 1,
                   locked_until = now() - interval '1 second'
             where id = '{claim.Id:D}'
            """);

        // The return value counts rows requeued with attempts left, not the ones that gave up, so the evidence is
        // the row itself.
        await world.Host.Services.GetRequiredService<ITaskClaimer>().ReclaimExpiredAsync(DefaultQueue, Token);

        (await TaskRowAsync(world, claim.Id)).ShouldBe(
            "dead_letter 1/1",
            customMessage: "the attempt is at its cap and its lease expired, so the reclaim dead-letters it");
    }

    /// <summary>One persisted task row, as <c>state attempts/max_attempts</c>.</summary>
    private static async Task<string> TaskRowAsync(IngestTestWorld world, Guid taskId) =>
        await world.ScalarAsync<string>(
            $"select state || ' ' || attempts || '/' || max_attempts from public.task where id = '{taskId:D}'");

    /// <summary>Claims every due reconcile on the default queue, asserting the batch holds what the case expects.</summary>
    private static async Task<IReadOnlyList<TaskClaim>> ClaimReconcilesAsync(IngestTestWorld world, int expected)
    {
        var claims = await world.Host.Services.GetRequiredService<ITaskClaimer>()
            .ClaimAsync(DefaultQueue, [RunReconcileContract.OutboxTaskType], expected, Token);

        claims.Count.ShouldBe(expected, customMessage: "the queue holds exactly the attempts the case queued");
        return claims;
    }

    /// <summary>Dispatches, then claims on the reconcile queue until the attempt ran and completed.</summary>
    private static async Task DispatchAndReconcileAsync(IngestTestWorld world)
    {
        await DispatchAsync(world);

        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await CountDoneAsync(world, RunReconcileContract.OutboxTaskType) >= 1)
            {
                return;
            }

            await consumer.PollOnceAsync(DefaultQueue, Token);
        }

        (await CountDoneAsync(world, RunReconcileContract.OutboxTaskType)).ShouldBeGreaterThanOrEqualTo(
            1,
            customMessage: "the re-armed reconcile is claimed and completed by the consumer");
    }

    /// <summary>The run a claim's payload names: a reconcile payload always carries its run id.</summary>
    private static Guid RunIdOf(TaskClaim claim)
    {
        using var payload = JsonDocument.Parse(claim.Payload);
        return payload.RootElement.GetProperty("run_id").GetGuid();
    }

    private static Task<long> CountDoneAsync(IngestTestWorld world, string taskType) =>
        world.CountAsync("task", where: $"task_type = '{taskType}' and state = 'done'");

    /// <summary>How many reconcile task rows the queue holds, whatever their state.</summary>
    private static Task<long> CountQueueRowsAsync(IngestTestWorld world, Guid runId) =>
        world.CountAsync(
            "task",
            where: $"task_type = '{RunReconcileContract.OutboxTaskType}' and payload ->> 'run_id' = '{runId:D}'");

    private static Task<long> DeadlineRowsAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<long>(
            "select count(*) from public.outbox where idempotency_key like "
            + $"'orchestrator:run:{runId:D}:reconcile:deadline%'");

    /// <summary>How many outbox rows carry a re-arm key (anything past the first attempt).</summary>
    private static Task<long> AttemptRowsAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<long>(
            "select count(*) from public.outbox where idempotency_key like "
            + $"'orchestrator:run:{runId:D}:reconcile:deadline:%'");

    private static Task<long> ReArmKeyAsync(IngestTestWorld world, Guid runId, int attempt) =>
        world.ScalarAsync<long>(
            "select count(*) from public.outbox where idempotency_key = "
            + $"'{RunReconcileContract.DeadlineIdempotencyKey(runId, attempt)}'");

    private static Task<Guid?> OutboxOrgAsync(IngestTestWorld world, Guid runId, int attempt) =>
        world.ScalarAsync<Guid?>(
            "select org_id from public.outbox where idempotency_key = "
            + $"'{RunReconcileContract.DeadlineIdempotencyKey(runId, attempt)}'");

    private static Task<string> StateAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>($"select state from public.run where id = '{runId:D}'");

    private static Task<string> MissingAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>(
            "select coalesce(array_to_string(missing, ','), '') from public.evidence_revision "
            + $"where org_id = '{world.Org}' and run_id = '{runId:D}' order by revision_no desc limit 1");

    private static Task<long> RevisionCountAsync(IngestTestWorld world) => world.CountAsync("evidence_revision");

    /// <summary>Puts one run in the state the scan names — collectable and past its deadline — in two statements.</summary>
    private async Task MarkOverdueAsync(IngestTestWorld world, Guid runId)
    {
        await RunStateWalk.MoveAsync(_fixture, world.Database, runId, "collecting");
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set collection_deadline_at = now() - interval '1 minute' where id = '{runId:D}'");
    }
}
