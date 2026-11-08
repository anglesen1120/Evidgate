using System.Diagnostics.Metrics;
using Dapper;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// The <c>outbox_undispatched</c> gauge reads a count that a refresher keeps
/// fresh on its own timer: the scrape callback does no database call, a refresh
/// failure keeps the last value, and a value the refresher stopped renewing is
/// not reported. Real PostgreSQL 18; the clock is fake so no test sleeps for an
/// interval.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class OutboxBacklogGaugeTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");

    private readonly PostgresTasksFixture _fixture;

    public OutboxBacklogGaugeTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Gauge_ReportsNothingUntilFirstCount_ThenCachedValueWithoutAnyDatabaseCall()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await SeedAsync(database, 3);
        var dataSource = _fixture.OpenAppDataSource(database, maximumPoolSize: 2);
        var time = new FakeTimeProvider();
        var log = new RefreshFailureLog();
        using var dispatcher = NewDispatcher(dataSource, time, log);
        using var collector = new GaugeCollector();

        collector.Collect().ShouldBeEmpty();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var refresher = dispatcher.RunBacklogRefreshAsync(stop.Token);
        (await collector.WaitForAsync(values => values.Length == 1, Token)).ShouldBe(new long[] { 3 });

        await stop.CancelAsync();
        await refresher;

        // No refresher and no reachable database: a callback that touched the data
        // source would throw (swallowed, so it would report nothing) instead of
        // returning the cached value.
        await dataSource.DisposeAsync();
        collector.Collect().ShouldBe(new long[] { 3 });
        log.Failures.ShouldBe(0);
    }

    [Fact]
    public async Task Gauge_FollowsTheBacklog_OnlyWhenTheRefreshIntervalElapses()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await SeedAsync(database, 2);
        await using var dataSource = _fixture.OpenAppDataSource(database, maximumPoolSize: 2);
        var time = new FakeTimeProvider();
        using var dispatcher = NewDispatcher(dataSource, time, new RefreshFailureLog());
        using var collector = new GaugeCollector();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var refresher = dispatcher.RunBacklogRefreshAsync(stop.Token);
        await collector.WaitForAsync(values => values.SequenceEqual(new long[] { 2 }), Token);

        await SeedAsync(database, 4);
        collector.Collect().ShouldBe(new long[] { 2 });

        time.Advance(OutboxDispatcher.BacklogRefreshInterval);
        (await collector.WaitForAsync(values => values.SequenceEqual(new long[] { 6 }), Token)).ShouldBe(new long[] { 6 });

        await stop.CancelAsync();
        await refresher;
    }

    [Fact]
    public async Task FailedRefresh_KeepsLastValue_ThenAgesOutInsteadOfShowingAStaleNumber()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await SeedAsync(database, 5);
        var dataSource = _fixture.OpenAppDataSource(database, maximumPoolSize: 2);
        var time = new FakeTimeProvider();
        var log = new RefreshFailureLog();
        using var dispatcher = NewDispatcher(dataSource, time, log);
        using var collector = new GaugeCollector();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var refresher = dispatcher.RunBacklogRefreshAsync(stop.Token);
        await collector.WaitForAsync(values => values.SequenceEqual(new long[] { 5 }), Token);

        await dataSource.DisposeAsync();

        time.Advance(OutboxDispatcher.BacklogRefreshInterval);
        await log.WaitForFailuresAsync(1, Token);
        collector.Collect().ShouldBe(new long[] { 5 });

        // The refresher keeps failing, so the last value is never renewed; once it
        // is older than the max age the gauge stops reporting it.
        time.Advance(OutboxDispatcher.BacklogRefreshInterval);
        await log.WaitForFailuresAsync(2, Token);
        time.Advance(TimeSpan.FromSeconds(1));
        collector.Collect().ShouldBeEmpty();

        await stop.CancelAsync();
        await refresher;
    }

    private static OutboxDispatcher NewDispatcher(NpgsqlDataSource dataSource, TimeProvider time, ILogger<OutboxDispatcher> logger) =>
        new(dataSource, Options.Create(new TasksOptions()), logger, time);

    private async Task SeedAsync(string database, int count)
    {
        await using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(_fixture.AppConnectionString(database)) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Token);

        for (var i = 0; i < count; i++)
        {
            var id = Guid.CreateVersion7();
            await connection.ExecuteAsync(
                """
                insert into public.outbox
                  (id, org_id, aggregate_type, aggregate_id, task_type, payload, idempotency_key, created_at)
                values
                  (@id, @org, 'organization', @aggregateId, 'identity.organization.provision', '{}'::jsonb, @key, now())
                """,
                new { id, org = OrgA, aggregateId = Guid.CreateVersion7(), key = $"gauge:{id}" });
        }
    }

    /// <summary>Collects the gauge through a <see cref="MeterListener"/>, the surface the OTel pipeline uses.</summary>
    private sealed class GaugeCollector : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<long> _values = [];

        internal GaugeCollector()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, publisher) =>
                {
                    if (instrument.Meter.Name == OutboxDispatchMetrics.MeterName
                        && instrument.Name == OutboxDispatchMetrics.UndispatchedGaugeName)
                    {
                        publisher.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            {
                lock (_values)
                {
                    _values.Add(measurement);
                }
            });
            _listener.Start();
        }

        internal long[] Collect()
        {
            lock (_values)
            {
                _values.Clear();
            }

            _listener.RecordObservableInstruments();
            lock (_values)
            {
                return [.. _values];
            }
        }

        internal async Task<long[]> WaitForAsync(Func<long[], bool> predicate, CancellationToken cancellationToken)
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

    /// <summary>Counts the refresh-failure warnings so a test can wait for a failed pass instead of sleeping.</summary>
    private sealed class RefreshFailureLog : ILogger<OutboxDispatcher>
    {
        private int _failures;

        internal int Failures => Volatile.Read(ref _failures);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning && exception is not null)
            {
                Interlocked.Increment(ref _failures);
            }
        }

        internal async Task WaitForFailuresAsync(int count, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (Failures < count)
            {
                await Task.Delay(25, timeout.Token);
            }
        }
    }
}
