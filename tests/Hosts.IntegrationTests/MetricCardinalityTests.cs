using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// 10.3.1-T1.4 / AC3 (S3, cardinality): every metric tag the platform emits is one of a closed set of
/// keys, and every tag value is a token from a closed vocabulary — never free text, never a tenant,
/// run, job or request id. The rule this pins is the one the plan's §8 states: no customer identifier
/// in any label, because a label is a series and a series is a bill.
/// </summary>
/// <remarks>
/// The measurement surface is a <see cref="MeterListener"/> over every platform meter (each
/// <c>Evidgate*</c> meter name, which includes the dispatcher's own meter of 01.5.1), the same surface the
/// OTel pipeline subscribes to. Every public recording method of <see cref="EvidgateTelemetry"/> is
/// discovered by reflection and invoked, and the dispatcher's instruments are published by constructing
/// the real class, so a new recording method, a new instrument or a new tag fails here rather than in
/// the billing dashboard: a method whose parameters have no probe value throws, an instrument that is
/// not declared as a constant trips the inventory assertion, and a tag key outside the allowlist trips
/// the measurement assertion.
/// </remarks>
[Collection(ProcessGlobalTelemetrySuite.Name)]
public sealed class MetricCardinalityTests
{
    /// <summary>
    /// A connection string nothing listens on: the dispatcher's instruments are created by its
    /// constructor, and no code in this test opens the data source (the gauge callback is only run by
    /// an explicit collection). It carries no credential, so the secret scan stays clean.
    /// </summary>
    private const string DispatcherProbeConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Database=evidgate;Timeout=2";

    /// <summary>
    /// The closed set of tag KEYS. Adding one is a reviewable decision about what may travel in a
    /// label; it is not a formality, and a key not listed here fails the test.
    /// </summary>
    private static readonly HashSet<string> AllowedTagKeys = new(StringComparer.Ordinal)
    {
        EvidgateTelemetry.ModuleTag,
        EvidgateTelemetry.PipelineMessageTag,
        "reason",
        "code",
        "protocol_major",
        "normalized_status",
        "error",
        "provider",
        "outcome",
        "change_type",
        "trigger",
        "written",
        "cap",
        "issue",
        "kind",
        "stage",
        "metric",
    };

    /// <summary>Id-shaped key names that must never become a label (they would be one series per row).</summary>
    private static readonly string[] IdentifierKeyMarkers =
    [
        "tenant",
        "org",
        "run_id",
        "job_id",
        "request_id",
        "session_id",
        "trace_id",
        "email",
        "subject",
        "user",
        "ip",
    ];

    /// <summary>A tag value is a token: lowercase words, digits, separators — no spaces, no prose.</summary>
    private static readonly System.Text.RegularExpressions.Regex TokenValue =
        new("^[A-Za-z0-9_.:-]{1,64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    [Fact]
    public void Every_Emitted_Tag_Key_And_Value_Is_From_A_Closed_Vocabulary()
    {
        var measurements = new ConcurrentQueue<(string Instrument, KeyValuePair<string, object?>[] Tags)>();
        var published = new ConcurrentQueue<string>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, publisher) =>
            {
                if (IsPlatformMeter(instrument.Meter.Name))
                {
                    published.Enqueue(instrument.Name);
                    publisher.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>(
            (instrument, _, tags, _) => measurements.Enqueue((instrument.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>(
            (instrument, _, tags, _) => measurements.Enqueue((instrument.Name, tags.ToArray())));
        listener.Start();

        // The dispatcher creates its own meter and instruments in its constructor; publishing them
        // is what proves its two instruments sit on a watched meter.
        using var dataSource = new NpgsqlDataSourceBuilder(DispatcherProbeConnectionString)
        {
            // A fixed name, like the production builders: an unnamed pool reports its DSN as the label.
            Name = "metric_probe",
        }.Build();
        int measurementsInvoked;
        using (new OutboxDispatcher(
                   dataSource,
                   Options.Create(new TasksOptions()),
                   NullLogger<OutboxDispatcher>.Instance))
        {
            measurementsInvoked = RecordEveryMeasurement();
        }

        var recorded = measurements.ToArray();
        Assert.True(
            recorded.Length >= measurementsInvoked + 1,
            $"only {recorded.Length} measurements were captured for {measurementsInvoked} recording methods and the "
            + "module counter — a recording method returned without emitting (a count of 0 is a no-op) and its tags "
            + "would never be seen");

        foreach (var (instrument, tags) in recorded)
        {
            foreach (var tag in tags)
            {
                Assert.True(
                    AllowedTagKeys.Contains(tag.Key),
                    $"{instrument} emitted a tag key outside the closed set: {tag.Key}");

                var key = tag.Key.ToLowerInvariant();
                Assert.DoesNotContain(
                    IdentifierKeyMarkers,
                    marker => key.Contains(marker, StringComparison.Ordinal));

                var value = tag.Value?.ToString() ?? string.Empty;
                Assert.Matches(TokenValue, value);
            }
        }

        // Every instrument's tag key showed up, so the allowlist is not passing by omission.
        var observedKeys = recorded
            .SelectMany(measurement => measurement.Tags)
            .Select(tag => tag.Key)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(EvidgateTelemetry.ModuleTag, observedKeys);
        Assert.Contains(EvidgateTelemetry.PipelineMessageTag, observedKeys);
        Assert.Contains("reason", observedKeys);
        Assert.Contains("code", observedKeys);
        Assert.Contains("normalized_status", observedKeys);
        Assert.Contains("provider", observedKeys);
        Assert.Contains("outcome", observedKeys);
        Assert.Contains("change_type", observedKeys);
        Assert.Contains("trigger", observedKeys);
        Assert.Contains("written", observedKeys);
        Assert.Contains("kind", observedKeys);

        AssertInstrumentInventory(
            published.ToArray(),
            recorded.Select(measurement => measurement.Instrument).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Asserts the instrument inventory: the dispatcher's two instruments are published on a watched
    /// meter, every instrument this suite measured is a declared <see cref="EvidgateTelemetry"/>
    /// constant, and every declared constant produced a measurement.
    /// </summary>
    /// <remarks>
    /// The inventory is checked against what was <em>measured</em>, not against what was published:
    /// the other platform meters state the same meter name by value (<c>TaskQueueMetrics</c>,
    /// <c>GoogleJwksCache</c>) or a sibling name (<c>Evidgate.Audit</c>), so their instruments replay
    /// into the published set as soon as any other test in this process initialised them. Those are
    /// watched for tag keys, and are never measured by this suite, so a new one cannot fail here —
    /// while a new recording method or an undeclared instrument name still does.
    /// </remarks>
    private static void AssertInstrumentInventory(string[] published, HashSet<string> measured)
    {
        // The pipeline subscribes once, by name: AddMeter(EvidgateTelemetry.MeterName). The
        // dispatcher states the same name by value (Persistence does not reference Observability),
        // so the two constants must agree or its metrics silently leave the pipeline.
        Assert.Equal(EvidgateTelemetry.MeterName, OutboxDispatchMetrics.MeterName);

        Assert.Contains(OutboxDispatchMetrics.DispatchLagHistogramName, published);
        Assert.Contains(OutboxDispatchMetrics.UndispatchedGaugeName, published);

        var declared = DeclaredInstrumentNames();
        foreach (var name in measured)
        {
            Assert.Contains(name, declared);
        }

        foreach (var name in declared)
        {
            Assert.Contains(name, measured);
        }
    }

    /// <summary>
    /// Every metric-name constant of <see cref="EvidgateTelemetry"/>: a new instrument that is not
    /// declared here fails <see cref="AssertInstrumentInventory"/>, and so does a constant nothing
    /// records.
    /// </summary>
    private static HashSet<string> DeclaredInstrumentNames() =>
        typeof(EvidgateTelemetry)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral
                && field.FieldType == typeof(string)
                && (field.Name.EndsWith("MetricName", StringComparison.Ordinal)
                    || field.Name.EndsWith("CounterName", StringComparison.Ordinal)))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="meterName"/> is a meter the platform owns and the pipeline subscribes to.</summary>
    private static bool IsPlatformMeter(string meterName) =>
        meterName.StartsWith("Evidgate", StringComparison.Ordinal);

    /// <summary>
    /// Exercises the whole recording surface of <see cref="EvidgateTelemetry"/>: the two span starters
    /// (the module one also counts on <c>evidgate.module.operations</c>, the only instrument whose
    /// labels come from a span API) and then every public static <c>Record*</c> method once, deriving
    /// its arguments from the signature. The list is discovered, not maintained by hand, so a new
    /// recording method is exercised without an edit here — and one whose parameters have no probe
    /// value fails loudly instead of being skipped.
    /// </summary>
    /// <returns>How many recording methods were invoked, so the caller can require one measurement each.</returns>
    private static int RecordEveryMeasurement()
    {
        using (EvidgateTelemetry.StartModuleActivity("ingestion", "Probe"))
        using (EvidgateTelemetry.StartPipelineActivity("Probe"))
        {
        }

        var methods = typeof(EvidgateTelemetry)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name.StartsWith("Record", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(methods);

        foreach (var method in methods)
        {
            var arguments = method.GetParameters().Select(ArgumentFor).ToArray();
            method.Invoke(null, arguments);
        }

        return methods.Length;
    }

    /// <summary>A closed-vocabulary argument for one parameter of a recording method.</summary>
    private static object? ArgumentFor(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(string))
        {
            return "probe";
        }

        if (type == typeof(long))
        {
            return 2L;
        }

        // The usage-units counter adds a fractional quantity, so 2 is a valid committed amount.
        if (type == typeof(double))
        {
            return 2d;
        }

        // The apply batch-size histogram counts changes rather than tagging them; 2 is inside its 1..200 range.
        if (type == typeof(int))
        {
            return 2;
        }

        if (type == typeof(int?))
        {
            return 3;
        }

        if (type == typeof(bool))
        {
            return true;
        }

        if (type == typeof(TimeSpan))
        {
            return TimeSpan.FromMilliseconds(5);
        }

        throw new InvalidOperationException(
            $"the parameter '{parameter.Name}' of a public recording method has type {type}, which this "
            + "probe has no closed-vocabulary value for: add one so the new tag vocabulary is verified "
            + "here rather than discovered in the billing dashboard");
    }

    /// <summary>
    /// S1: Npgsql reports <c>db.client.connection.pool.name</c> from the data source's own name, and the
    /// driver's default is the connection string without its password — so the host, the user and the
    /// database would become a metric label, one series per deployment. The app-role pool is built by the
    /// production wiring, and both the name and the label it exports are a fixed token, never the DSN this
    /// test hands the builder.
    /// </summary>
    [Fact]
    public void TheAppRolePool_IsLabelledWithAFixedToken_NotItsConnectionString()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Database:ConnectionString"] = DispatcherProbeConnectionString,
            })
            .Build();
        services.AddEvidgatePersistence(configuration);

        using var provider = services.BuildServiceProvider();
        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();

        // The production builder names the pool: the driver's default is the connection string, which
        // would put the host, the user and the database on a label.
        Assert.Equal(PersistenceExtensions.AppDataSourceName, dataSource.Name);

        using var listener = PoolNames(out var reported);
        listener.RecordObservableInstruments();

        Assert.Contains(PersistenceExtensions.AppDataSourceName, reported);

        // The tag carries the name, never the credential-bearing string the name was derived from.
        Assert.DoesNotContain(DispatcherProbeConnectionString, reported);
    }

    /// <summary>
    /// Listens for Npgsql's <c>db.client.connection.count</c> and collects the pool name of every
    /// measurement an observable collection reports — the label an operator's series is cut by.
    /// </summary>
    private static MeterListener PoolNames(out ConcurrentQueue<string> names)
    {
        var queue = new ConcurrentQueue<string>();
        names = queue;
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == TelemetryMeters.Npgsql
                    && instrument.Name == "db.client.connection.count")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<int>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "db.client.connection.pool.name" && tag.Value is string name)
                {
                    queue.Enqueue(name);
                }
            }
        });
        listener.Start();

        return listener;
    }
}
