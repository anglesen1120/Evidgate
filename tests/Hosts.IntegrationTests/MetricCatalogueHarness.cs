using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Dapper;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Shouldly;

namespace Hosts.IntegrationTests;

/// <summary>
/// The metrics pipeline the Api boots, built for the catalogue test: <c>AddEvidgateTelemetry</c> plus the
/// server instrumentation that subscribes to the hosting meter, on an ASP.NET Core host behind
/// <see cref="TestServer"/>, with an in-memory metric reader attached to that same builder.
/// </summary>
/// <remarks>
/// The catalogue test asserts on what this provider <em>collected</em>, never on a
/// <see cref="MeterListener"/>'s view of the process: a production <c>AddMeter</c> that disappears leaves
/// the instrument out of the collection and fails the test. The listener this class starts is only a
/// directory of the instruments the producers create. Three of them — the dead-letter counter, the
/// lease-loss counter and the dispatch-lag histogram — record inside a committed database transition,
/// which Tasks.IntegrationTests drives; here each gets one measurement so the pipeline's collection of the
/// instrument is what is pinned.
/// </remarks>
internal sealed class MetricCatalogueHarness : IAsyncDisposable
{
    private const string ProbeRoute = "/catalogue-probe";

    private readonly WebApplication _app;
    private readonly HttpClient _client;
    private readonly InstrumentDirectory _instruments;
    private readonly Probes _probes;
    private bool _disposed;

    private MetricCatalogueHarness(
        WebApplication app,
        HttpClient client,
        InstrumentDirectory instruments,
        Probes probes,
        MeterProvider provider)
    {
        _app = app;
        _client = client;
        _instruments = instruments;
        _probes = probes;
        Provider = provider;
    }

    /// <summary>The provider the production registration built: the only evidence the catalogue test reads.</summary>
    internal MeterProvider Provider { get; }

    /// <summary>
    /// Starts the host, subscribes the reader, sends one server request, and opens the producers that must
    /// outlive the collection (the two caches their gauges read).
    /// </summary>
    /// <param name="database">The migrated database the probes read and write through the app role.</param>
    /// <param name="collected">The in-memory reader's sink.</param>
    /// <param name="cancellationToken">Stops the start-up.</param>
    internal static async Task<MetricCatalogueHarness> StartAsync(
        TestDatabase database,
        ExportedMetrics collected,
        CancellationToken cancellationToken)
    {
        var instruments = new InstrumentDirectory();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Staging" });
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Telemetry:OtlpEndpoint"] = null,
            });
        builder.WebHost.UseTestServer();

        // The hosts' shared registration, then the server instrumentation that owns the hosting meter
        // (ApiHost). The reader is attached to this builder, so it collects exactly what production collects.
        builder.AddEvidgateTelemetry("evidgate-catalogue-test")
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddInMemoryExporter(collected));

        var app = builder.Build();
        app.MapGet(ProbeRoute, () => "ok");
        await app.StartAsync(cancellationToken);

        var provider = app.Services.GetRequiredService<MeterProvider>();
        var client = app.GetTestClient();

        // One completed server request is what publishes the request-duration histogram's measurement.
        using (var response = await client.GetAsync(ProbeRoute, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
        }

        return new MetricCatalogueHarness(
            app,
            client,
            instruments,
            new Probes(database, instruments),
            provider);
    }

    /// <summary>Runs every producer the catalogue names against the live pipeline.</summary>
    /// <param name="cancellationToken">Cancels the database probes.</param>
    internal Task ProduceAsync(CancellationToken cancellationToken) => _probes.MeasureAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _probes.DisposeAsync();
        _client.Dispose();
        _instruments.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>
    /// The instruments the producing classes created, by meter and instrument name. Discovery only: no
    /// assertion reads this directory.
    /// </summary>
    internal sealed class InstrumentDirectory : IDisposable
    {
        private readonly ConcurrentDictionary<(string Meter, string Instrument), Instrument> _byName = new();
        private readonly MeterListener _listener;

        internal InstrumentDirectory()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, _) =>
                    _byName[(instrument.Meter.Name, instrument.Name)] = instrument,
            };
            _listener.Start();
        }

        internal Instrument Get(string meter, string instrument) =>
            _byName.TryGetValue((meter, instrument), out var found)
                ? found
                : throw new InvalidOperationException($"no instrument {meter}/{instrument} exists in this process");

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>The producers, their data sources and the two cache refreshers the gauges report from.</summary>
    private sealed class Probes : IAsyncDisposable
    {
        private const string QueuePoolName = "catalogue_probe";
        private const string TimeoutPoolName = "catalogue_timeout_probe";
        private const int PoolTimeoutSeconds = 1;

        private readonly InstrumentDirectory _instruments;
        private readonly CancellationTokenSource _stop = new();
        private readonly NpgsqlDataSource _queueProbe;
        private readonly NpgsqlDataSource _timeoutProbe;
        private readonly TaskQueueStateGauge _queueGauge;
        private readonly OutboxDispatcher _dispatcher;
        private readonly Task _queueRefresher;
        private readonly Task _backlogRefresher;

        internal Probes(TestDatabase database, InstrumentDirectory instruments)
        {
            _instruments = instruments;
            _queueProbe = OpenDataSource(database.WorkerConnectionString, QueuePoolName, maximumPoolSize: 4);
            _timeoutProbe = OpenDataSource(
                database.WorkerConnectionString,
                TimeoutPoolName,
                maximumPoolSize: 1,
                timeoutSeconds: PoolTimeoutSeconds);

            _queueGauge = new TaskQueueStateGauge(_queueProbe, NullLogger<TaskQueueStateGauge>.Instance);
            _dispatcher = new OutboxDispatcher(
                _queueProbe,
                Options.Create(new TasksOptions()),
                NullLogger<OutboxDispatcher>.Instance,
                TimeProvider.System);

            // Both gauges report a cache, so each refresher must have read the database once before a
            // scrape can carry a measurement. Neither throws into this test: a failure logs and keeps the
            // previous value, which the collection deadline then reports as a missing entry.
            _queueRefresher = _queueGauge.RunRefreshAsync(_stop.Token);
            _backlogRefresher = _dispatcher.RunBacklogRefreshAsync(_stop.Token);
        }

        internal async Task MeasureAsync(CancellationToken cancellationToken)
        {
            EvidgateTelemetry.RecordRunIncomplete();
            EvidgateTelemetry.RecordArtifactFailure("upload", "checksum_mismatch");
            EvidgateTelemetry.RecordUsageUnits("results", 1);
            EvidgateTelemetry.RecordUsageBillingKeyConflict();
            EvidgateTelemetry.RecordEvaluationDecisionLatency(TimeSpan.FromMilliseconds(1));
            EvidgateTelemetry.RecordHealingLlmRefused("provider_disabled");

            // The classes that own the remaining instruments create them in a static constructor.
            RuntimeHelpers.RunClassConstructor(AlertNotifierType().TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(TaskClaimer).TypeHandle);

            RecordOneMeasurement(TaskQueueMetrics.MeterName, TaskQueueMetrics.DeadLetterCounterName);
            RecordOneMeasurement(TaskQueueMetrics.MeterName, TaskQueueMetrics.LeaseLostCounterName);
            RecordOneMeasurement(OutboxDispatchMetrics.MeterName, OutboxDispatchMetrics.DispatchLagHistogramName);

            // A real command: Npgsql records the operation duration only when an operation runs.
            await using (var connection = await _queueProbe.OpenConnectionAsync(cancellationToken))
            {
                _ = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                    "select 1",
                    cancellationToken: cancellationToken));
            }

            await RecordPoolTimeoutAsync(cancellationToken);

            // A completed work item, then the runtime probes: the runtime's instruments report either an
            // observable value or a counter that needs work to have happened.
            await Task.Run(static () => { }, cancellationToken);
            ProbeRuntime();
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
#pragma warning disable VSTHRD003 // Both refresher loops are started by this harness with its own token.
            await _queueRefresher;
            await _backlogRefresher;
#pragma warning restore VSTHRD003
            _stop.Dispose();
            _queueGauge.Dispose();
            _dispatcher.Dispose();
            await _queueProbe.DisposeAsync();
            await _timeoutProbe.DisposeAsync();
        }

        private static NpgsqlDataSource OpenDataSource(
            string connectionString,
            string name,
            int maximumPoolSize,
            int timeoutSeconds = 15) =>
            new NpgsqlDataSourceBuilder(
                new NpgsqlConnectionStringBuilder(connectionString)
                {
                    MaxPoolSize = maximumPoolSize,
                    Timeout = timeoutSeconds,
                }.ConnectionString)
            {
                // The pool name is a metric tag: a token, never the connection string Npgsql would report.
                Name = name,
            }.Build();

        /// <summary>
        /// Fills the one-slot pool, then waits out a check-out: that timeout is the only thing that records
        /// a measurement on the driver's timeout counter.
        /// </summary>
        private async Task RecordPoolTimeoutAsync(CancellationToken cancellationToken)
        {
            await using var occupied = await _timeoutProbe.OpenConnectionAsync(cancellationToken);
            var timedOut = false;
            try
            {
                await using var blocked = await _timeoutProbe.OpenConnectionAsync(cancellationToken);
            }
            catch (NpgsqlException)
            {
                timedOut = true;
            }

            timedOut.ShouldBeTrue("the pool probe must time out, or the Npgsql timeout counter has no measurement");
        }

        private void RecordOneMeasurement(string meter, string instrument)
        {
            switch (_instruments.Get(meter, instrument))
            {
                case Counter<long> counter:
                    counter.Add(1);
                    break;
                case Histogram<double> histogram:
                    histogram.Record(0.001);
                    break;
                default:
                    throw new InvalidOperationException($"{meter}/{instrument} is not a counter or a histogram");
            }
        }

        private static Type AlertNotifierType() =>
            typeof(TaskQueueMetrics).Assembly.GetType(
                "Evidgate.BuildingBlocks.Persistence.Tasks.TaskAlertNotifier",
                throwOnError: true)!;

        private static void ProbeRuntime()
        {
            GC.Collect();
            _ = GC.GetTotalMemory(forceFullCollection: true);
            _ = GC.GetTotalAllocatedBytes();
            _ = GC.GetGCMemoryInfo();
            _ = GC.GetTotalPauseDuration();
            _ = ThreadPool.ThreadCount;
            _ = ThreadPool.CompletedWorkItemCount;
            _ = ThreadPool.PendingWorkItemCount;
            _ = Monitor.LockContentionCount;
            _ = System.Runtime.JitInfo.GetCompilationTime();
            _ = System.Runtime.JitInfo.GetCompiledILBytes();
            _ = System.Runtime.JitInfo.GetCompiledMethodCount();
            _ = Environment.WorkingSet;
            ThrowAndCatch();
        }

        /// <summary>The runtime counts an exception when it is thrown, so the counter needs one thrown here.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowAndCatch()
        {
            try
            {
                throw new InvalidOperationException("catalogue probe: the exception counter needs one thrown");
            }
            catch (InvalidOperationException)
            {
                // Expected: the counter records on the throw, not on the catch.
            }
        }
    }
}

/// <summary>
/// The in-memory reader's sink: one entry per metric the production provider exported. A reader collects
/// on every flush, so the same instrument can appear more than once; the catalogue test unions them.
/// </summary>
internal sealed class ExportedMetrics : ICollection<MetricSnapshot>
{
    private readonly ConcurrentQueue<MetricSnapshot> _collected = new();

    /// <inheritdoc />
    public int Count => _collected.Count;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <summary>True when the provider exported that meter's instrument at least once.</summary>
    /// <param name="meter">The meter name the catalogue resolves its alias to.</param>
    /// <param name="instrument">The instrument name the catalogue declares.</param>
    internal bool Contains(string meter, string instrument) =>
        _collected.Any(metric => Is(metric, meter, instrument));

    /// <summary>Every export of one instrument, oldest first: copies the reader made, so a later collection cannot rewrite them.</summary>
    /// <param name="meter">The meter name the catalogue resolves its alias to.</param>
    /// <param name="instrument">The instrument name the catalogue declares.</param>
    internal MetricSnapshot[] Exports(string meter, string instrument) =>
        _collected.Where(metric => Is(metric, meter, instrument)).ToArray();

    /// <inheritdoc />
    public void Add(MetricSnapshot item) => _collected.Enqueue(item);

    /// <inheritdoc />
    public void Clear()
    {
        while (_collected.TryDequeue(out _))
        {
        }
    }

    /// <inheritdoc />
    public bool Contains(MetricSnapshot item) => _collected.Contains(item);

    /// <inheritdoc />
    public void CopyTo(MetricSnapshot[] array, int arrayIndex) =>
        _collected.ToArray().CopyTo(array, arrayIndex);

    /// <inheritdoc />
    public bool Remove(MetricSnapshot item) => false;

    /// <inheritdoc />
    public IEnumerator<MetricSnapshot> GetEnumerator() => _collected.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static bool Is(MetricSnapshot metric, string meter, string instrument) =>
        string.Equals(metric.MeterName, meter, StringComparison.Ordinal)
        && string.Equals(metric.Name, instrument, StringComparison.Ordinal);
}
