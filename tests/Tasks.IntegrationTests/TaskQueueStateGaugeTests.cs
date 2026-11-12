using System.Diagnostics.Metrics;
using Dapper;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// The queue-state gauges read a snapshot a refresher keeps fresh on its own timer: a scrape runs no
/// SQL, an empty or absent queue reports zero age rather than a missing measurement, and a value the
/// refresher stopped renewing is not reported instead of looking fresh. Real PostgreSQL 18; the clock
/// is fake so no test sleeps for an interval.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TaskQueueStateGaugeTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");

    private readonly PostgresTasksFixture _fixture;

    public TaskQueueStateGaugeTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Gauge_ReportsNothingUntilTheFirstRefresh_ThenTheCachedSnapshot()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await SeedDueAsync(database, 2, TimeSpan.FromSeconds(30));
        await SeedDeadLettersAsync(database, 1);
        await using var dataSource = _fixture.OpenWorkerDataSource(database, maximumPoolSize: 2);
        var time = new FakeTimeProvider();
        using var gauge = new TaskQueueStateGauge(dataSource, NullLogger<TaskQueueStateGauge>.Instance, time);
        using var collector = new GaugeCollector();

        var before = collector.Collect();
        before.OldestAge.ShouldBeEmpty();
        before.DeadLetters.ShouldBeEmpty();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var refresher = gauge.RunRefreshAsync(stop.Token);

        // The two callbacks read the snapshot separately, so a scrape only has the full pair once each
        // has reported; waiting on one would let the other assert an empty list.
        var snapshot = await collector.WaitForAsync(
            values => values.DeadLetters.Length == 1 && values.OldestAge.Length == 1, Token);
        snapshot.DeadLetters.ShouldBe(new long[] { 1 });
        snapshot.OldestAge.Single().ShouldBeGreaterThanOrEqualTo(30);

        // A scrape between refreshes returns the cached pair, not a new query's answer.
        await SeedDeadLettersAsync(database, 2);
        collector.Collect().DeadLetters.ShouldBe(new long[] { 1 });

        time.Advance(TaskQueueStateGauge.RefreshInterval);
        var next = await collector.WaitForAsync(
            values => values.OldestAge.Length == 1 && values.DeadLetters.SequenceEqual(new long[] { 3 }), Token);
        next.DeadLetters.ShouldBe(new long[] { 3 });

        await stop.CancelAsync();
        await refresher;
    }

    [Fact]
    public async Task EmptyQueue_ReportsZeroAgeAndZeroDepth_RatherThanNoMeasurement()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var dataSource = _fixture.OpenWorkerDataSource(database, maximumPoolSize: 2);
        var time = new FakeTimeProvider();
        using var gauge = new TaskQueueStateGauge(dataSource, NullLogger<TaskQueueStateGauge>.Instance, time);
        using var collector = new GaugeCollector();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var refresher = gauge.RunRefreshAsync(stop.Token);

        var snapshot = await collector.WaitForAsync(
            values => values.OldestAge.Length == 1 && values.DeadLetters.Length == 1, Token);
        snapshot.OldestAge.ShouldBe(new double[] { 0 });
        snapshot.DeadLetters.ShouldBe(new long[] { 0 });

        await stop.CancelAsync();
        await refresher;
    }

    [Fact]
    public async Task FailedRefresh_KeepsTheSnapshot_ThenStopsReportingItOnceItAgesOut()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await SeedDeadLettersAsync(database, 1);
        var dataSource = _fixture.OpenWorkerDataSource(database, maximumPoolSize: 2);
        var time = new FakeTimeProvider();
        using var gauge = new TaskQueueStateGauge(dataSource, NullLogger<TaskQueueStateGauge>.Instance, time);
        using var collector = new GaugeCollector();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var refresher = gauge.RunRefreshAsync(stop.Token);
        await collector.WaitForAsync(values => values.OldestAge.Length == 1 && values.DeadLetters.Length == 1, Token);

        await dataSource.DisposeAsync();

        // One failed refresh leaves the last committed snapshot in place: no scrape-time I/O, so the
        // failure is a stale-value risk rather than a wrong number.
        time.Advance(TaskQueueStateGauge.RefreshInterval);
        collector.Collect().DeadLetters.ShouldBe(new long[] { 1 });

        // Past two intervals the snapshot is older than the gauge reports about, so it disappears
        // instead of pretending the queue has not changed.
        time.Advance(TaskQueueStateGauge.MaxAge);
        var aged = collector.Collect();
        aged.OldestAge.ShouldBeEmpty();
        aged.DeadLetters.ShouldBeEmpty();

        await stop.CancelAsync();
        await refresher;
    }

    /// <summary>
    /// The snapshot walks the live-backlog index for the age and the dead-letter index for the depth, with
    /// sequential scans disabled so a small table cannot hide a plan that would scan all task history.
    /// </summary>
    [Fact]
    public async Task Snapshot_WalksTheQueueIndexes_NotTheTaskTable()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await SeedDueAsync(database, 2, TimeSpan.FromSeconds(30));
        await SeedDeadLettersAsync(database, 2);

        await using var connection = new NpgsqlConnection(_fixture.WorkerConnectionString(database));
        await connection.OpenAsync(Token);
        await connection.ExecuteAsync("set enable_seqscan = off");

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"explain (format json) {TaskQueueSql.QueueStateSnapshot}";
            command.Parameters.Add(new NpgsqlParameter("dead_letter_cap", TaskQueueSql.DeadLetterGaugeCap));

            var plan = (string)(await command.ExecuteScalarAsync(Token))!;
            plan.ShouldContain("task_claim_idx");
            plan.ShouldContain("task_dead_idx");
            plan.ShouldNotContain("Seq Scan");
        }
        finally
        {
            await connection.ExecuteAsync("set enable_seqscan = on");
        }
    }

    /// <summary>
    /// The depth is capped inside the statement, so a large dead-letter backlog costs the cap and not the
    /// whole backlog. The cap is the parameter the refresher binds; a small one asserts the saturation
    /// without seeding 100 000 rows.
    /// </summary>
    [Fact]
    public async Task Snapshot_DeadLetterDepth_SaturatesAtTheCap()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await SeedDeadLettersAsync(database, 3);

        await using var connection = new NpgsqlConnection(_fixture.WorkerConnectionString(database));
        await connection.OpenAsync(Token);

        var belowCap = await ReadSnapshotAsync(connection, cap: 10);
        belowCap.DeadLetters.ShouldBe(3L, "below the cap the depth is the real count");

        var atCap = await ReadSnapshotAsync(connection, cap: 2);
        atCap.DeadLetters.ShouldBe(2L, "above the cap the depth saturates instead of counting the backlog");
    }

    private static async Task<(double OldestAgeSeconds, long DeadLetters)> ReadSnapshotAsync(
        NpgsqlConnection connection,
        int cap)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = TaskQueueSql.QueueStateSnapshot;
        command.Parameters.Add(new NpgsqlParameter("dead_letter_cap", cap));

        await using var reader = await command.ExecuteReaderAsync(Token);
        await reader.ReadAsync(Token);
        return (reader.GetDouble(0), reader.GetInt64(1));
    }

    private async Task SeedDueAsync(string database, int count, TimeSpan waited)
    {
        await using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(_fixture.WorkerConnectionString(database)) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Token);

        for (var i = 0; i < count; i++)
        {
            var id = Guid.CreateVersion7();
            await connection.ExecuteAsync(
                """
                insert into public.task
                  (id, org_id, queue, task_type, payload, idempotency_key, state, available_at)
                values
                  (@id, @org, 'default', 'queue.probe', '{}'::jsonb, @key, 'queued', now() - @waited::interval)
                """,
                new { id, org = OrgA, key = $"gauge:{id}", waited = $"{waited.TotalSeconds} seconds" });
        }
    }

    private async Task SeedDeadLettersAsync(string database, int count)
    {
        await using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(_fixture.WorkerConnectionString(database)) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Token);

        for (var i = 0; i < count; i++)
        {
            var id = Guid.CreateVersion7();
            await connection.ExecuteAsync(
                """
                insert into public.task
                  (id, org_id, queue, task_type, payload, idempotency_key, state, attempts, max_attempts)
                values
                  (@id, @org, 'default', 'queue.probe', '{}'::jsonb, @key, 'dead_letter', 8, 8)
                """,
                new { id, org = OrgA, key = $"gauge:{id}" });
        }
    }

    /// <summary>Collects both gauges through the <see cref="MeterListener"/> surface the OTel pipeline uses.</summary>
    private sealed class GaugeCollector : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<double> _oldestAge = [];
        private readonly List<long> _deadLetters = [];

        internal GaugeCollector()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, publisher) =>
                {
                    if (instrument.Meter.Name == TaskQueueMetrics.MeterName
                        && instrument.Name is TaskQueueMetrics.OldestAgeGaugeName or TaskQueueMetrics.DeadLetterDepthGaugeName)
                    {
                        publisher.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<double>((_, measurement, _, _) =>
            {
                lock (_oldestAge)
                {
                    _oldestAge.Add(measurement);
                }
            });
            _listener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
            {
                lock (_deadLetters)
                {
                    _deadLetters.Add(measurement);
                }
            });
            _listener.Start();
        }

        internal (double[] OldestAge, long[] DeadLetters) Collect()
        {
            lock (_oldestAge)
            {
                _oldestAge.Clear();
            }

            lock (_deadLetters)
            {
                _deadLetters.Clear();
            }

            _listener.RecordObservableInstruments();

            lock (_oldestAge)
            {
                lock (_deadLetters)
                {
                    return ([.. _oldestAge], [.. _deadLetters]);
                }
            }
        }

        internal async Task<(double[] OldestAge, long[] DeadLetters)> WaitForAsync(
            Func<(double[] OldestAge, long[] DeadLetters), bool> predicate,
            CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                var values = Collect();
                if (predicate(values))
                {
                    return values;
                }

                await Task.Delay(25, timeout.Token);
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
