using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Dapper;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 01.5.1-T3: the outbox dispatcher over real PostgreSQL 18 (Docker-backed). The
/// suite drives the production mechanism as the production role —
/// <c>evidgate_app</c> on the app-role data source — so it proves at once that
/// migration 0009's `select` / `update (dispatched_at)` grant is sufficient for the
/// batch, that it is not more than that, and that 0009 reaches head (the fixture
/// applies the real <c>migrations/</c> through the real Migrator).
/// </summary>
/// <remarks>
/// Statement counting is an Npgsql logging capture, not a database-side counter:
/// this Npgsql version (10.0.3) has no <c>DbCommandInterceptor</c> (checked in the
/// package's own XML and assembly), and <c>pg_stat_statements</c> is not preloaded
/// in the digest-pinned image. Npgsql logs <c>Executing command: {CommandText}</c>
/// once per command, before it is sent, so the capture is exactly the set of
/// statements the batch issued — and the crash test can kill the backend from
/// inside that callback, deterministically, before the statement reaches the
/// server.
/// </remarks>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class OutboxDispatcherTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");

    private const string RoutedTaskType = "identity.organization.provision";
    private const string UnroutedTaskType = "catalog.run.evaluate";
    private const string RoutedQueue = "ingest";

    /// <summary>Queue the dispatcher falls back to for a task_type the deployment did not route (the TasksOptions default).</summary>
    private const string DefaultQueue = "default";

    private const string Payload = """{"organization_id":"01J0F0RGANIZATION00000000"}""";

    private readonly PostgresTasksFixture _fixture;

    public OutboxDispatcherTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 01.5.1-T3.1 (00 §5.7 P2): one dispatch batch costs a constant number of
    /// statements, never one per row — an empty outbox costs the claim alone, and a
    /// full batch costs claim + <c>insert … select</c> + stamp whether it moves five
    /// rows or one. Also covers §5/§7 queue routing: a routed task_type lands in its
    /// configured queue, an unrouted one in the default queue, and the task row
    /// carries the outbox row's tenant, payload and <c>'outbox:' || id</c> dedupe
    /// key.
    /// </summary>
    [Fact]
    public async Task Dispatcher_BatchIsOneRoundTrip()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = DispatcherHost.Start(_fixture, database, batchSize: 5);

        // Nothing is committed yet: a poll costs the claim statement and no more.
        var empty = await host.Dispatcher.DispatchBatchAsync(Token);
        empty.ShouldBe(OutboxDispatchResult.Empty);
        var emptyStatements = host.Statements.Snapshot();
        emptyStatements.Length.ShouldBe(1);
        emptyStatements[0].ShouldBe(Normalize(OutboxDispatchSql.ClaimUndispatched));

        var seeded = await SeedAsync(database, 4, RoutedTaskType, 2, UnroutedTaskType);

        var first = await host.Dispatcher.DispatchBatchAsync(Token);
        first.Claimed.ShouldBe(5);
        first.Enqueued.ShouldBe(5);

        var second = await host.Dispatcher.DispatchBatchAsync(Token);
        second.Claimed.ShouldBe(1);
        second.Enqueued.ShouldBe(1);

        var drained = await host.Dispatcher.DispatchBatchAsync(Token);
        drained.ShouldBe(OutboxDispatchResult.Empty);

        // Two full-or-partial batches (three statements each) and one empty poll
        // (the claim alone), whatever the row count: the batch never loops per row.
        var batchStatements = host.Statements.Snapshot().Skip(1).ToArray();
        batchStatements.Length.ShouldBe(7);
        foreach (var start in new[] { 0, 3 })
        {
            batchStatements[start].ShouldBe(Normalize(OutboxDispatchSql.ClaimUndispatched));
            batchStatements[start + 1].ShouldBe(Normalize(OutboxDispatchSql.EnqueueTasks));
            batchStatements[start + 2].ShouldBe(Normalize(OutboxDispatchSql.MarkDispatched));
        }

        batchStatements[6].ShouldBe(Normalize(OutboxDispatchSql.ClaimUndispatched));

        await using var owner = new NpgsqlConnection(_fixture.OwnerConnectionString(database));
        await owner.OpenAsync(Token);

        // Routing: the routed type in its queue, the unrouted type in the default.
        (await owner.ExecuteScalarAsync<long>(
            "select count(*) from public.task where queue = @queue and task_type = @taskType",
            new { queue = RoutedQueue, taskType = RoutedTaskType })).ShouldBe(4);

        (await owner.ExecuteScalarAsync<long>(
            "select count(*) from public.task where queue = @queue and task_type = @taskType",
            new { queue = DefaultQueue, taskType = UnroutedTaskType })).ShouldBe(2);

        // The outbox row's tenant and payload are the task's, the dedupe key is
        // derived from the outbox id (§7), and the row arrives ready to claim.
        (await owner.ExecuteScalarAsync<long>(
            "select count(*) from public.task where org_id = @org and payload ->> 'organization_id' = '01J0F0RGANIZATION00000000' and state = 'queued' and attempts = 0",
            new { org = OrgA })).ShouldBe(6);

        var keys = (await owner.QueryAsync<string>(
            "select idempotency_key from public.task order by idempotency_key")).ToArray();
        var expectedKeys = seeded.OutboxIds
            .Select(id => "outbox:" + id)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        keys.ShouldBe(expectedKeys);

        (await owner.ExecuteScalarAsync<long>(
            "select count(*) from public.outbox where dispatched_at is null")).ShouldBe(0);
        (await owner.ExecuteScalarAsync<long>(
            "select count(*) from public.outbox where dispatched_at is not null")).ShouldBe(6);
    }

    /// <summary>
    /// AC2 at the dispatcher's level: running the batch over the same undispatched
    /// rows twice yields exactly one <c>task</c> row per outbox row — the second run
    /// finds nothing to claim — and a replay of a row whose outbox write is repeated
    /// still cannot produce a second piece of work, because the dedupe key is the
    /// outbox id.
    /// </summary>
    [Fact]
    public async Task Dispatcher_RunTwice_OneTaskRow()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = DispatcherHost.Start(_fixture, database);
        var seeded = await SeedAsync(database, 3, RoutedTaskType);

        var first = await host.Dispatcher.DispatchBatchAsync(Token);
        first.Claimed.ShouldBe(3);
        first.Enqueued.ShouldBe(3);

        var again = await host.Dispatcher.DispatchBatchAsync(Token);
        again.ShouldBe(OutboxDispatchResult.Empty);

        await using var owner = new NpgsqlConnection(_fixture.OwnerConnectionString(database));
        await owner.OpenAsync(Token);

        (await owner.ExecuteScalarAsync<long>("select count(*) from public.task")).ShouldBe(3);
        (await owner.ExecuteScalarAsync<long>("select count(distinct idempotency_key) from public.task")).ShouldBe(3);
        (await owner.ExecuteScalarAsync<long>(
            "select count(*) from public.outbox where dispatched_at is not null")).ShouldBe(3);

        var replay = await owner.ExecuteScalarAsync<long>(
            """
            insert into public.task (id, org_id, queue, task_type, payload, idempotency_key)
            values (@id, @org, @queue, @taskType, @payload::jsonb, @key)
            on conflict do nothing
            returning 1
            """,
            new
            {
                id = Guid.CreateVersion7(),
                org = OrgA,
                queue = RoutedQueue,
                taskType = RoutedTaskType,
                payload = Payload,
                key = "outbox:" + seeded.OutboxIds[0],
            });
        replay.ShouldBe(0);

        (await owner.ExecuteScalarAsync<long>("select count(*) from public.task")).ShouldBe(3);
    }

    /// <summary>
    /// AC1 at the dispatcher's level. The business commit happened — the outbox row
    /// is durable — then the worker dies in the middle of its batch: the backend of
    /// the dispatch transaction is terminated between the claim and the
    /// <c>insert … select</c>. A batch is one transaction, so the claim's locks and
    /// the partial work roll back and nothing is dispatched. Restarting dispatches
    /// the row exactly once, and a further run dispatches nothing: the
    /// commit-then-crash window loses no work and duplicates none.
    /// </summary>
    [Fact]
    public async Task CrashAfterCommit_TaskStillDispatched()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var seeded = await SeedAsync(database, 1, RoutedTaskType);
        var outboxId = seeded.OutboxIds[0];

        await using var superuser = await _fixture.OpenSuperuserAsync(database);

        await using (var killed = DispatcherHost.Start(
            _fixture,
            database,
            applicationName: DispatcherHost.KilledApplicationName,
            killerConnection: superuser,
            killOnStatementContaining: "insert into public.task"))
        {
            // The kill lands before the insert reaches the server, so the batch
            // fails and its transaction can never commit.
            await Should.ThrowAsync<Exception>(() => killed.Dispatcher.DispatchBatchAsync(Token));
        }

        await using (var owner = new NpgsqlConnection(_fixture.OwnerConnectionString(database)))
        {
            await owner.OpenAsync(Token);

            (await owner.ExecuteScalarAsync<long>("select count(*) from public.task")).ShouldBe(0);
            (await owner.ExecuteScalarAsync<long>(
                "select count(*) from public.outbox where id = @id and dispatched_at is null",
                new { id = outboxId })).ShouldBe(1);
        }

        // Restart: the row that survived the crash is dispatched exactly once.
        await using var restarted = DispatcherHost.Start(_fixture, database);
        var dispatched = await restarted.Dispatcher.DispatchBatchAsync(Token);
        dispatched.Claimed.ShouldBe(1);
        dispatched.Enqueued.ShouldBe(1);

        var repeat = await restarted.Dispatcher.DispatchBatchAsync(Token);
        repeat.ShouldBe(OutboxDispatchResult.Empty);

        await using var verification = new NpgsqlConnection(_fixture.OwnerConnectionString(database));
        await verification.OpenAsync(Token);

        (await verification.ExecuteScalarAsync<long>(
            "select count(*) from public.task where idempotency_key = @key",
            new { key = "outbox:" + outboxId })).ShouldBe(1);
        (await verification.ExecuteScalarAsync<long>(
            "select count(*) from public.outbox where id = @id and dispatched_at is not null",
            new { id = outboxId })).ShouldBe(1);
    }

    /// <summary>
    /// 01.5.1-T3.2 (01.5.1 §7, 00 §11 SLI): the dispatcher publishes
    /// <c>outbox_undispatched</c> as its backlog depth and
    /// <c>task_dispatch_lag_seconds</c> once per dispatched row. Asserted through a
    /// <see cref="MeterListener"/> over the application meter — the surface the OTel
    /// pipeline subscribes to — with one row deliberately aged so the lag is a real
    /// wait rather than a rounding artefact.
    /// </summary>
    [Fact]
    public async Task Dispatcher_EmitsUndispatchedGaugeAndDispatchLagHistogram()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var lags = new ConcurrentQueue<double>();
        var undispatched = new ConcurrentQueue<long>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, publisher) =>
            {
                if (instrument.Meter.Name == OutboxDispatchMetrics.MeterName
                    && (instrument.Name == OutboxDispatchMetrics.DispatchLagHistogramName
                        || instrument.Name == OutboxDispatchMetrics.UndispatchedGaugeName))
                {
                    publisher.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == OutboxDispatchMetrics.DispatchLagHistogramName)
            {
                lags.Enqueue(measurement);
            }
        });
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == OutboxDispatchMetrics.UndispatchedGaugeName)
            {
                undispatched.Enqueue(measurement);
            }
        });
        listener.Start();

        await using var host = DispatcherHost.Start(_fixture, database);
        await SeedAsync(database, 1, RoutedTaskType, 1, RoutedTaskType, TimeSpan.FromSeconds(3));

        // The dispatcher owns the instruments, and it is created on first
        // resolution — a listener only sees instruments that already exist when it
        // collects, so resolving it (once) is what puts the gauge on the meter.
        var dispatcher = host.Dispatcher;

        // An observable gauge publishes only when it is collected.
        listener.RecordObservableInstruments();
        var beforeDispatch = undispatched.ToArray();
        beforeDispatch.ShouldBe(new long[] { 2 });

        var batch = await dispatcher.DispatchBatchAsync(Token);
        batch.Claimed.ShouldBe(2);

        listener.RecordObservableInstruments();
        var measured = lags.ToArray();
        measured.Length.ShouldBe(2);
        measured.ShouldContain(value => value >= 2.5 && value < 60);
        measured.ShouldAllBe(value => value >= 0 && value < 60);

        var afterDispatch = undispatched.ToArray();
        afterDispatch.ShouldBe(new long[] { 2, 0 });
    }

    /// <summary>
    /// Migration 0009's contract, from the role that has to live with it: the app
    /// role can select <c>outbox</c> and stamp exactly <c>dispatched_at</c>, and
    /// nothing more — no table-level UPDATE (so a committed row's payload,
    /// task_type, org_id or idempotency_key cannot be rewritten after the business
    /// transaction committed) and no DELETE. The positive half — dispatch running
    /// as this role — is what every other test in this class exercises.
    /// </summary>
    [Fact]
    public async Task Dispatcher_Grant_IsMinimal_And_Sufficient()
    {
        var database = await _fixture.CreateDatabaseAsync();

        await using var app = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await app.OpenAsync(Token);

        (await app.ExecuteScalarAsync<bool>(
            "select has_table_privilege('evidgate_app', 'public.outbox', 'SELECT')")).ShouldBeTrue();
        (await app.ExecuteScalarAsync<bool>(
            "select has_table_privilege('evidgate_app', 'public.outbox', 'INSERT')")).ShouldBeTrue();
        (await app.ExecuteScalarAsync<bool>(
            "select has_column_privilege('evidgate_app', 'public.outbox', 'dispatched_at', 'UPDATE')")).ShouldBeTrue();
        (await app.ExecuteScalarAsync<bool>(
            "select has_table_privilege('evidgate_app', 'public.outbox', 'UPDATE')")).ShouldBeFalse();
        (await app.ExecuteScalarAsync<bool>(
            "select has_table_privilege('evidgate_app', 'public.outbox', 'DELETE')")).ShouldBeFalse();

        var rewriteDenied = await Should.ThrowAsync<PostgresException>(() => app.ExecuteAsync(
            "update public.outbox set task_type = task_type where false"));
        rewriteDenied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        var deleteDenied = await Should.ThrowAsync<PostgresException>(() => app.ExecuteAsync(
            "delete from public.outbox"));
        deleteDenied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    /// <summary>Seeds committed outbox rows directly (the app role's insert path of 01.5.1-T2).</summary>
    private async Task<SeededOutbox> SeedAsync(
        string database,
        int routedCount,
        string routedType,
        int unroutedCount = 0,
        string unroutedType = UnroutedTaskType,
        TimeSpan? agedBy = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(_fixture.AppConnectionString(database)) { Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(Token);

        var ids = new List<Guid>();
        var createdAt = DateTime.UtcNow - (agedBy ?? TimeSpan.Zero);

        for (var i = 0; i < routedCount + unroutedCount; i++)
        {
            var id = Guid.CreateVersion7();
            ids.Add(id);

            await connection.ExecuteAsync(
                """
                insert into public.outbox
                  (id, org_id, aggregate_type, aggregate_id, task_type, payload, idempotency_key, created_at)
                values
                  (@id, @org, 'organization', @aggregateId, @taskType, @payload::jsonb, @key, @createdAt)
                """,
                new
                {
                    id,
                    org = OrgA,
                    aggregateId = Guid.CreateVersion7(),
                    taskType = i < routedCount ? routedType : unroutedType,
                    payload = Payload,
                    key = $"suite:outbox:{i}:{id}",
                    createdAt,
                });
        }

        return new SeededOutbox(ids);
    }

    /// <summary>
    /// Whitespace-normalised statement text with every parameter placeholder
    /// collapsed to <c>?</c>, so a comparison cannot fail on indentation or on the
    /// driver's own rewrite: Npgsql turns the <c>@name</c> placeholders of the SQL
    /// constants into positional <c>$n</c> before it sends the command, and the
    /// capture sees what was sent, not what the caller wrote.
    /// </summary>
    private static string Normalize(string sql)
    {
        var collapsed = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var normalized = new StringBuilder(collapsed.Length);

        for (var i = 0; i < collapsed.Length; i++)
        {
            var current = collapsed[i];

            if (current == '@' && i + 1 < collapsed.Length && (char.IsLetter(collapsed[i + 1]) || collapsed[i + 1] == '_'))
            {
                normalized.Append('?');
                i++;
                while (i + 1 < collapsed.Length && (char.IsLetterOrDigit(collapsed[i + 1]) || collapsed[i + 1] == '_'))
                {
                    i++;
                }
            }
            else if (current == '$' && i + 1 < collapsed.Length && char.IsDigit(collapsed[i + 1]))
            {
                normalized.Append('?');
                i++;
                while (i + 1 < collapsed.Length && char.IsDigit(collapsed[i + 1]))
                {
                    i++;
                }
            }
            else
            {
                normalized.Append(current);
            }
        }

        return normalized.ToString();
    }

    /// <summary>The ids of the rows the suite seeded; the outbox id is the task dedupe key's suffix.</summary>
    private sealed record SeededOutbox(IReadOnlyList<Guid> OutboxIds);

    /// <summary>
    /// Captures every command Npgsql executes on the data source it is attached to
    /// and, when a test asks for it, terminates that transaction's backend at one
    /// chosen statement — the crashed worker of AC1.
    /// </summary>
    /// <remarks>
    /// Npgsql logs <c>Executing command: {CommandText}</c> before sending a command,
    /// which is the only statement-level hook this driver version offers (no
    /// <c>DbCommandInterceptor</c>). Capturing only messages with that exact prefix
    /// keeps command-completion events (which also carry a command text) from
    /// counting a statement twice.
    /// </remarks>
    private sealed class StatementCapture : ILoggerFactory, ILogger
    {
        private const string ExecutingCommandPrefix = "Executing command: ";

        private readonly ConcurrentQueue<string> _statements = new();
        private readonly string? _killOnStatementContaining;
        private readonly NpgsqlConnection? _killerConnection;
        private readonly string? _killedApplicationName;
        private int _killed;

        internal StatementCapture(
            string? killOnStatementContaining = null,
            NpgsqlConnection? killerConnection = null,
            string? killedApplicationName = null)
        {
            _killOnStatementContaining = killOnStatementContaining;
            _killerConnection = killerConnection;
            _killedApplicationName = killedApplicationName;
        }

        /// <summary>Every non-transaction-control statement seen so far, whitespace-normalised.</summary>
        internal string[] Snapshot() => _statements.Select(Normalize).ToArray();

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => this;

        /// <inheritdoc />
        public void AddProvider(ILoggerProvider provider)
        {
            // One capture instance serves every category; providers are not used.
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Nothing to release: the capture is a queue of strings.
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
            if (message is null || !message.StartsWith(ExecutingCommandPrefix, StringComparison.Ordinal))
            {
                return;
            }

            var commandText = message[ExecutingCommandPrefix.Length..];
            var normalized = Normalize(commandText);
            if (IsTransactionControl(normalized))
            {
                return;
            }

            _statements.Enqueue(commandText);

            if (_killOnStatementContaining is not null
                && _killerConnection is not null
                && normalized.Contains(_killOnStatementContaining, StringComparison.Ordinal)
                && Interlocked.Exchange(ref _killed, 1) == 0)
            {
                KillBackend();
            }
        }

        private static bool IsTransactionControl(string normalized) =>
            normalized.StartsWith("begin", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("commit", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("rollback", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("savepoint", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Terminates the dispatch transaction's backend. Runs synchronously on the
        /// dispatcher's own thread, before the logged command is sent, so the kill is
        /// deterministic rather than a race against the statement.
        /// </summary>
        private void KillBackend()
        {
            using var command = new NpgsqlCommand(
                "select count(pg_terminate_backend(pid)) from pg_stat_activity where application_name = @applicationName",
                _killerConnection);
            command.Parameters.AddWithValue("applicationName", _killedApplicationName ?? string.Empty);
            command.ExecuteScalar();
        }
    }

    /// <summary>
    /// The production mechanism over one test database: an app-role
    /// <c>NpgsqlDataSource</c> whose commands are captured, and
    /// <c>AddEvidgateTasks</c> — the registration that puts the dispatcher beside
    /// the queue it feeds.
    /// </summary>
    private sealed class DispatcherHost : IAsyncDisposable
    {
        internal const string KilledApplicationName = "evidgate-test-killed-dispatcher";

        private readonly ServiceProvider _provider;

        private DispatcherHost(NpgsqlDataSource dataSource, ServiceProvider provider, StatementCapture statements)
        {
            DataSource = dataSource;
            _provider = provider;
            Statements = statements;
        }

        internal NpgsqlDataSource DataSource { get; }

        internal OutboxDispatcher Dispatcher => _provider.GetRequiredService<OutboxDispatcher>();

        /// <summary>The statements the dispatcher issued through this host's data source.</summary>
        internal StatementCapture Statements { get; }

        internal static DispatcherHost Start(
            PostgresTasksFixture fixture,
            string database,
            int batchSize = 200,
            string? applicationName = null,
            NpgsqlConnection? killerConnection = null,
            string? killOnStatementContaining = null)
        {
            ArgumentNullException.ThrowIfNull(fixture);

            var capture = new StatementCapture(killOnStatementContaining, killerConnection, KilledApplicationName);

            var connectionString = new NpgsqlConnectionStringBuilder(fixture.AppConnectionString(database))
            {
                MaxPoolSize = 2,
                MaxAutoPrepare = 0,
                ApplicationName = applicationName ?? "evidgate-test-dispatcher",
            };

            var builder = new NpgsqlDataSourceBuilder(connectionString.ConnectionString);
            builder.UseLoggerFactory(capture);
            var dataSource = builder.Build();

            var values = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{TasksOptions.SectionName}:DispatchBatchSize"] = batchSize.ToString(CultureInfo.InvariantCulture),
                [$"{TasksOptions.SectionName}:QueueRoutes:0:TaskType"] = RoutedTaskType,
                [$"{TasksOptions.SectionName}:QueueRoutes:0:Queue"] = RoutedQueue,
            };

            var services = new ServiceCollection();
            services.AddSingleton(dataSource);
            services.AddLogging();
            services.AddEvidgateTasks(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

            return new DispatcherHost(dataSource, services.BuildServiceProvider(), capture);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await DataSource.DisposeAsync();
        }
    }
}
