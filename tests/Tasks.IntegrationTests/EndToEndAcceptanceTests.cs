using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 01.5.1-T5.1/T5.2 — the AC-level suite of the whole durable-task chain
/// (<c>plans/features/01-foundation/01.5.1-durable-internal-tasks.md</c> §2 AC1–AC3 + AC7, §9):
/// every test here drives the production components <em>across</em> their
/// boundaries on real PostgreSQL 18 (migrations applied by the real
/// <c>Evidgate.Migrator</c>), which is what the per-component suites cannot show:
/// the producer's commit path, the dispatcher's claim/insert transaction and the
/// consumer's handler all run against one database in one test, so "exactly one
/// side effect" is observed end to end rather than inferred per component.
/// </summary>
/// <remarks>
/// Coverage map for the AC rows this class does not own (each already proven where
/// it says — re-asserting a covered mechanic here would duplicate, not strengthen,
/// coverage):
/// <list type="bullet">
/// <item>AC1's dispatcher-only half (kill mid-batch, restart, one row) is
/// <c>OutboxDispatcherTests.CrashAfterCommit_TaskStillDispatched</c>; this class adds
/// the half that AC1's text names — the handler ran exactly once.</item>
/// <item>AC2's sequential half (<c>Dispatcher_RunTwice_OneTaskRow</c>) and AC3's
/// fire key (<c>ScheduleRunnerTests.Schedule_TwoWorkers_OneTaskRow</c>,
/// <c>TaskQueueWriterTests.Enqueue_ScheduledFire_Concurrently_KeepsOneRow</c>) are
/// proven there; AC3's second clause — one side effect per occurrence — is
/// <see cref="Schedule_TwoWorkers_OneOccurrenceOneTaskRow_OneRunEach"/>.</item>
/// <item>AC4/AC5 (the operator routes) live in
/// <c>Hosts.IntegrationTests.DeadLetterChainTests</c>, where the HTTP surface is;
/// their route-shape halves are <c>OperatorDeadLetterEndpointTests</c>.</item>
/// <item>AC6 lives in <c>Persistence.IntegrationTests.IdempotencyEndToEndTests</c>
/// (real pipeline) with its store-level halves in <c>IdempotencyTests</c>,
/// <c>PipelineTests.Idempotent_Command_Replays_And_Conflicts</c> and
/// <c>PurgeTaskTests.PurgeTask_DeletesOnlyExpired</c>.</item>
/// <item>AC7's visibility-timeout requeue and the expired-lease-at-cap edge are
/// <c>TaskConsumerTests.Consumer_Crash_LockedUntilExpiry_Requeues</c>,
/// <c>Consumer_LeaseExpired_AtTheCap_DeadLetters_InsteadOfLooping</c> and
/// <c>ClaimHelperTests.Concurrent_Claimers_Split_Rows</c>;
/// <see cref="FourConsumers_ClaimedBatch_EachTaskExecutedOnce"/> adds the N=4 form
/// of AC7's first clause on a batch the dispatcher produced.</item>
/// </list>
/// </remarks>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class EndToEndAcceptanceTests
{
    /// <summary>The queue every task of this suite lands in (the dispatcher's default queue — no routing config needed).</summary>
    private const string Queue = "acceptance-chain";

    /// <summary>The tenant the outbox rows are committed for (the chain's producer is a tenant transaction).</summary>
    private static readonly Guid OrgId = Guid.Parse("11111111-1111-7111-8111-111111111111");

    private readonly PostgresTasksFixture _fixture;

    public EndToEndAcceptanceTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// AC1 (T5.1): an outbox row committed through the production
    /// <c>IOutboxWriter</c> inside a tenant transaction, then the worker killed in
    /// the middle of its dispatch batch (<c>pg_terminate_backend</c> on the dispatch
    /// connection, exactly as <c>OutboxDispatcherTests</c> does), then a restart:
    /// the row is still there, is dispatched exactly once, and the <b>handler runs
    /// exactly once</b> — the commit-then-crash window loses no work and duplicates
    /// none, observed across the producer, the dispatcher transaction and the
    /// consumer.
    /// </summary>
    [Fact]
    public async Task CrashAfterCommit_TaskStillDispatched_HandlerRanExactlyOnce()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        var committed = await CommitOutboxRowsAsync(database, 1, "crash");

        (await TaskSql.ScalarAsync<long>(owner, "select count(*) from public.outbox where dispatched_at is null"))
            .ShouldBe(1);

        await using var superuser = await _fixture.OpenSuperuserAsync(database);

        await using (var killed = TaskChainHost.Start(
            _fixture,
            database,
            applicationName: TaskChainHost.KilledApplicationName,
            killerConnection: superuser,
            killOnStatementContaining: "insert into public.task"))
        {
            // The kill lands before the insert reaches the server: the batch fails
            // and its transaction can never commit.
            await Should.ThrowAsync<Exception>(() => killed.Dispatcher.DispatchBatchAsync(Token));

            killed.Recorder.TotalRuns.ShouldBe(0, "the crashed worker ran no handler");
        }

        // The crash rolled the whole batch back: no task row, the committed outbox
        // row still undispatched — the durable half of the window.
        (await TaskSql.ScalarAsync<long>(owner, "select count(*) from public.task")).ShouldBe(0);
        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.outbox where id = @id and dispatched_at is null",
            ("id", committed[0]))).ShouldBe(1);

        await using var restarted = TaskChainHost.Start(_fixture, database);

        var dispatched = await restarted.Dispatcher.DispatchBatchAsync(Token);
        dispatched.Claimed.ShouldBe(1);
        dispatched.Enqueued.ShouldBe(1);

        (await restarted.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        restarted.Recorder.Runs["crash-0"].ShouldBe(1, "the restarted worker ran the handler exactly once");
        (await TaskSql.ScalarAsync<string>(
            owner,
            "select state from public.task where idempotency_key = @key",
            ("key", "outbox:" + committed[0]))).ShouldBe("done");
        (await TaskSql.ScalarAsync<long>(owner, "select count(*) from public.task")).ShouldBe(1);

        // Neither half may repeat: a second pass finds nothing to dispatch and
        // nothing to run.
        (await restarted.Dispatcher.DispatchBatchAsync(Token)).ShouldBe(OutboxDispatchResult.Empty);
        (await restarted.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(0);
        restarted.Recorder.TotalRuns.ShouldBe(1);
    }

    /// <summary>
    /// AC2 (T5.1): two workers dispatch the same undispatched outbox rows
    /// concurrently — the production shape of a duplicated dispatcher — and a second
    /// pass runs over the (already stamped) rows afterwards. Exactly one <c>task</c>
    /// row exists per outbox row, all carrying the <c>outbox:&lt;id&gt;</c> dedupe key,
    /// and consuming that queue runs <b>one recorded side effect per row</b>: the
    /// duplicate dispatch AC2 forbids cannot produce duplicate work.
    /// </summary>
    [Fact]
    public async Task Dispatcher_TwoWorkers_OneTaskRowPerOutboxRow_OneSideEffect()
    {
        const int rows = 5;
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        var committed = await CommitOutboxRowsAsync(database, rows, "dispatch");

        await using var workerA = TaskChainHost.Start(_fixture, database);
        await using var workerB = TaskChainHost.Start(_fixture, database);

        var batches = await Task.WhenAll(
            workerA.Dispatcher.DispatchBatchAsync(Token),
            workerB.Dispatcher.DispatchBatchAsync(Token));

        // `FOR UPDATE SKIP LOCKED` splits the batch between the two transactions
        // (either may take all of it); whichever split happened, every row is
        // dispatched exactly once.
        batches.Sum(batch => batch.Enqueued).ShouldBe(rows);
        batches.ShouldAllBe(batch => batch.Claimed == batch.Enqueued);

        (await workerA.Dispatcher.DispatchBatchAsync(Token)).ShouldBe(OutboxDispatchResult.Empty);
        (await workerB.Dispatcher.DispatchBatchAsync(Token)).ShouldBe(OutboxDispatchResult.Empty);

        (await TaskSql.ScalarAsync<long>(owner, "select count(*) from public.task")).ShouldBe(rows);
        (await TaskSql.ScalarAsync<long>(owner, "select count(distinct idempotency_key) from public.task")).ShouldBe(rows);
        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.outbox where dispatched_at is not null")).ShouldBe(rows);

        var expectedKeys = committed.Select(id => "outbox:" + id).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        (await TaskSql.QueryAsync<string>(owner, "select idempotency_key from public.task order by idempotency_key"))
            .ShouldBe(expectedKeys);

        (await workerA.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(rows);

        workerA.Recorder.TotalRuns.ShouldBe(rows, "each dispatched row produced exactly one side effect");
        workerA.Recorder.Runs.Count.ShouldBe(rows);
        workerA.Recorder.Runs.Values.ShouldAllBe(runs => runs == 1);
    }

    /// <summary>
    /// AC3 (T5.1), second clause: two workers compute the same Cronos occurrences
    /// concurrently and fire them (<c>ON CONFLICT (schedule_name, fire_at) DO
    /// NOTHING</c>), then a consumer runs the fired rows — one <c>task</c> row and
    /// <b>one recorded side effect per occurrence</b>. The row-level half of AC3 is
    /// <c>ScheduleRunnerTests.Schedule_TwoWorkers_OneTaskRow</c>; this test crosses
    /// the fire boundary into execution, which is where "one side effect" is
    /// observable.
    /// </summary>
    [Fact]
    public async Task Schedule_TwoWorkers_OneOccurrenceOneTaskRow_OneRunEach()
    {
        // Same instant as ScheduleRunnerTests: the window is (11:55:30, 12:00:30],
        // so the five occurrences are 11:56 … 12:00.
        var noon = new DateTimeOffset(2026, 9, 26, 12, 0, 30, TimeSpan.Zero);
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        Action<IServiceCollection> register = services => services.AddTaskSchedule(
            new ScheduleDefinition(
                "acceptance.minute",
                "* * * * *",
                Queue,
                TaskHandlerTypes.Recording,
                fireAt => $$"""{"value":"fire-{{fireAt:HHmm}}"}"""));

        await using var workerA = TaskChainHost.Start(_fixture, database, services: register);
        await using var workerB = TaskChainHost.Start(_fixture, database, services: register);

        var inserted = await Task.WhenAll(
            workerA.Runner.RunOnceAsync(noon, Token),
            workerB.Runner.RunOnceAsync(noon, Token));

        inserted.Sum().ShouldBe(5, "the two workers fired the five occurrences, duplicating none");

        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.task where schedule_name = 'acceptance.minute'")).ShouldBe(5);
        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(distinct fire_at) from public.task where schedule_name = 'acceptance.minute'")).ShouldBe(5);

        (await workerA.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(5);

        workerA.Recorder.Runs.Keys.OrderBy(key => key, StringComparer.Ordinal)
            .ShouldBe(["fire-1156", "fire-1157", "fire-1158", "fire-1159", "fire-1200"]);
        workerA.Recorder.Runs.Values.ShouldAllBe(runs => runs == 1);

        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.task where schedule_name = 'acceptance.minute' and state = 'done'")).ShouldBe(5);
    }

    /// <summary>
    /// AC7 (T5.2), first clause on a dispatched batch: N=4 concurrent consumer loops
    /// (two workers × two loops) sharing one queue claim the twelve rows a dispatch
    /// pass produced — <c>FOR UPDATE SKIP LOCKED</c> hands each row to exactly one
    /// consumer, so every payload is handled once and no attempt is ever given back
    /// (<c>attempts = 1</c>). AC7's requeue half and its expired-lease-at-cap edge
    /// are <c>TaskConsumerTests.Consumer_Crash_LockedUntilExpiry_Requeues</c>,
    /// <c>Consumer_LeaseExpired_AtTheCap_DeadLetters_InsteadOfLooping</c> and
    /// <c>ClaimHelperTests.Concurrent_Claimers_Split_Rows</c>; this test is the N=4,
    /// dispatcher-produced form the AC states.
    /// </summary>
    [Fact]
    public async Task FourConsumers_ClaimedBatch_EachTaskExecutedOnce()
    {
        const int rows = 12;
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await CommitOutboxRowsAsync(database, rows, "fanout");

        // A claim pass takes two rows at most, so the four loops really contend for
        // the twelve rows instead of one loop draining them in a single pass.
        await using var workerA = TaskChainHost.Start(_fixture, database, maximumPoolSize: 6, batchSize: 2);
        await using var workerB = TaskChainHost.Start(_fixture, database, maximumPoolSize: 6, batchSize: 2);

        (await workerA.Dispatcher.DispatchBatchAsync(Token)).Enqueued.ShouldBe(rows);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var loopA = workerA.Consumer.RunAsync(Queue, concurrency: 2, stop.Token);
        var loopB = workerB.Consumer.RunAsync(Queue, concurrency: 2, stop.Token);

        var drained = await WaitUntilAsync(
            async () => await TaskSql.ScalarAsync<long>(
                owner,
                "select count(*) from public.task where queue = @queue and state = 'done'",
                ("queue", Queue)) == rows,
            TimeSpan.FromSeconds(30));

        await stop.CancelAsync();
        await Task.WhenAll(loopA, loopB);

        drained.ShouldBeTrue("the four consumer loops must drain the dispatched batch");

        // Each payload ran once across all four loops. The two loops of one host
        // share that host's recorder; the workers' recorders are separate.
        var runs = new Dictionary<string, int>(workerA.Recorder.Runs, StringComparer.Ordinal);
        foreach (var (payload, count) in workerB.Recorder.Runs)
        {
            runs[payload] = runs.TryGetValue(payload, out var seen) ? seen + count : count;
        }

        runs.Count.ShouldBe(rows);
        runs.Values.ShouldAllBe(count => count == 1);

        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.task where queue = @queue and attempts = 1",
            ("queue", Queue))).ShouldBe(rows, "no row was ever redelivered");
    }

    /// <summary>
    /// The producer half of the chain: <paramref name="count"/> outbox rows written
    /// through the production <c>IOutboxWriter</c> inside one committed tenant
    /// transaction (01.5.1-T2.2 / 00 §5.5 C7 — no in-process bus). Each row's
    /// payload is the suite's recording payload, so the consumer's own invoker binds
    /// it and the recorder can count the side effects.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> CommitOutboxRowsAsync(string database, int count, string prefix)
    {
        await using var producer = OutboxIdempotencyTestHost.Start(_fixture, database);
        var ids = new List<Guid>(count);

        await using (var scope = await producer.Scopes.BeginAsync(OrgId, cancellationToken: Token))
        {
            for (var index = 0; index < count; index++)
            {
                ids.Add(await producer.Outbox.EnqueueAsync(
                    TaskHandlerTypes.Recording,
                    $$"""{"value":"{{prefix}}-{{index}}"}""",
                    "acceptance",
                    Guid.CreateVersion7(),
                    $"acceptance:{prefix}:{index}",
                    Token));
            }

            await scope.CommitAsync(Token);
        }

        return ids;
    }

    /// <summary>
    /// Polls the assertion until it holds or the deadline passes: the concurrency test
    /// watches the database rather than sleeping a fixed time (the same helper the
    /// consumer suite uses).
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

    /// <summary>
    /// The production mechanism over one test database — the dispatcher and the
    /// consumer in one container: <c>AddEvidgateTenancy</c>
    /// and <c>AddEvidgateTasks</c>, exactly what
    /// <c>Evidgate.Worker</c> composes, plus the suite's recording handler. The data
    /// source is the app role's, so the tests also prove the grants the chain needs
    /// are the grants it has.
    /// </summary>
    private sealed class TaskChainHost : IAsyncDisposable
    {
        /// <summary>Application name of the worker a test kills; the kill matches on it.</summary>
        internal const string KilledApplicationName = "evidgate-acceptance-killed-worker";

        private readonly ServiceProvider _provider;

        private TaskChainHost(NpgsqlDataSource dataSource, ServiceProvider provider)
        {
            DataSource = dataSource;
            _provider = provider;
        }

        internal NpgsqlDataSource DataSource { get; }

        internal OutboxDispatcher Dispatcher => _provider.GetRequiredService<OutboxDispatcher>();

        internal TaskConsumer Consumer => _provider.GetRequiredService<TaskConsumer>();

        internal TaskScheduleRunner Runner => _provider.GetRequiredService<TaskScheduleRunner>();

        internal TaskRunRecorder Recorder => _provider.GetRequiredService<TaskRunRecorder>();

        /// <summary>
        /// Starts the chain over one test database. <paramref name="killerConnection"/>
        /// plus <paramref name="killOnStatementContaining"/> turn the host into a worker
        /// that dies deterministically inside its dispatch transaction (the AC1 crash);
        /// <paramref name="services"/> registers a test's own schedules,
        /// <paramref name="batchSize"/> bounds one claim pass (a small value makes
        /// concurrent consumer loops genuinely contend for rows).
        /// </summary>
        internal static TaskChainHost Start(
            PostgresTasksFixture fixture,
            string database,
            string? applicationName = null,
            NpgsqlConnection? killerConnection = null,
            string? killOnStatementContaining = null,
            Action<IServiceCollection>? services = null,
            int maximumPoolSize = 4,
            int batchSize = 50)
        {
            ArgumentNullException.ThrowIfNull(fixture);

            var connectionString = new NpgsqlConnectionStringBuilder(fixture.AppConnectionString(database))
            {
                MaxPoolSize = maximumPoolSize,
                MaxAutoPrepare = 0,
            };
            var dataSource = NpgsqlDataSource.Create(connectionString.ConnectionString);

            var values = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{TasksOptions.SectionName}:PollIntervalMilliseconds"] = "50",
                [$"{TasksOptions.SectionName}:BatchSize"] = batchSize.ToString(CultureInfo.InvariantCulture),
                [$"{TasksOptions.SectionName}:VisibilityTimeoutSeconds"] = "300",
                [$"{TasksOptions.SectionName}:BackoffBaseSeconds"] = "1",
                [$"{TasksOptions.SectionName}:BackoffMaxSeconds"] = "4",
                [$"{TasksOptions.SectionName}:DispatchBatchSize"] = "50",
                [$"{TasksOptions.SectionName}:DispatchDefaultQueue"] = Queue,
                [$"{TasksOptions.SectionName}:SweepConnectionString"] = fixture.WorkerSchedConnectionString(database),
                [$"{TasksOptions.SectionName}:WorkerConnectionString"] = fixture.WorkerConnectionString(database),
            };

            var collection = new ServiceCollection();
            collection.AddSingleton(dataSource);
            collection.AddLogging();
            collection.AddEvidgateTenancy();
            collection.AddEvidgateTasks(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

            if (killerConnection is not null && killOnStatementContaining is not null)
            {
                // The queue's statements run on the keyed worker source, so the crash injection sits there.
                var worker = new NpgsqlConnectionStringBuilder(fixture.WorkerConnectionString(database))
                { MaxPoolSize = maximumPoolSize, MaxAutoPrepare = 0, ApplicationName = applicationName ?? KilledApplicationName };
                var builder = new NpgsqlDataSourceBuilder(worker.ConnectionString);
                builder.UseLoggerFactory(new KillOnStatementLogger(
                    killOnStatementContaining, killerConnection, worker.ApplicationName));
                var killed = builder.Build();
                collection.AddKeyedSingleton<NpgsqlDataSource>(TaskWorkerDataSource.Key, (_, _) => killed);
            }

            collection.AddSingleton<TaskRunRecorder>();
            collection.AddTaskHandler<RecordingPayload, RecordingTaskHandler>();

            services?.Invoke(collection);

            return new TaskChainHost(dataSource, collection.BuildServiceProvider());
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await DataSource.DisposeAsync();
        }
    }

    /// <summary>
    /// Terminates the dispatch transaction's backend at one chosen statement — the
    /// crashed worker of AC1. Npgsql logs <c>Executing command: {CommandText}</c>
    /// before sending the command (this driver version offers no
    /// <c>DbCommandInterceptor</c>), so the kill runs on the dispatcher's own thread
    /// before the statement reaches the server: deterministic rather than a race
    /// against it. The suite's <c>OutboxDispatcherTests</c> documents the same
    /// technique and uses it for the dispatcher-only half of AC1.
    /// </summary>
    private sealed class KillOnStatementLogger : ILoggerFactory, ILogger
    {
        private const string ExecutingCommandPrefix = "Executing command: ";

        private readonly string _statementFragment;
        private readonly NpgsqlConnection _killerConnection;
        private readonly string _killedApplicationName;
        private int _killed;

        internal KillOnStatementLogger(
            string statementFragment,
            NpgsqlConnection killerConnection,
            string killedApplicationName)
        {
            _statementFragment = statementFragment;
            _killerConnection = killerConnection;
            _killedApplicationName = killedApplicationName;
        }

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => this;

        /// <inheritdoc />
        public void AddProvider(ILoggerProvider provider)
        {
            // One logger serves every category; providers are not used.
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Nothing to release.
        }

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var message = formatter(state, exception);
            if (message is null
                || !message.StartsWith(ExecutingCommandPrefix, StringComparison.Ordinal)
                || !message.Contains(_statementFragment, StringComparison.OrdinalIgnoreCase)
                || Interlocked.Exchange(ref _killed, 1) != 0)
            {
                return;
            }

            using var command = new NpgsqlCommand(
                "select count(pg_terminate_backend(pid)) from pg_stat_activity where application_name = @applicationName",
                _killerConnection);
            command.Parameters.AddWithValue("applicationName", _killedApplicationName);
            command.ExecuteScalar();
        }
    }
}
