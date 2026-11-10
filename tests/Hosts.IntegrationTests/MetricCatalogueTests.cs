using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Dapper;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Shouldly;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Hosts.IntegrationTests;

/// <summary>
/// Pins <c>infra/metrics/catalogue.yaml</c>: every entry names exactly one of a real instrument or a
/// non-empty unavailable reason, every declared instrument is published by the registration the hosts
/// use, and no label escapes the bounded vocabulary. The rule behind it is the operations plan's: a
/// metric that cannot be measured is reported as unavailable with a reason, never as zero.
/// </summary>
/// <remarks>
/// The publication probe is a <see cref="MeterListener"/> over the four meter names in the catalogue,
/// fed by the real producers — the application's own recording methods, the queue-state gauge, the
/// dispatcher, a real PostgreSQL connection through the pool, and the runtime instrumentation built by
/// <c>AddEvidgateTelemetry</c>. A rename in a library, a missing subscription or a new entry with no
/// source fails here instead of silently dropping a series.
/// </remarks>
[Collection(ProcessGlobalTelemetrySuite.Name)]
public sealed class MetricCatalogueTests
{
    /// <summary>
    /// Label keys no entry may carry: a correlation id, a tenant, a subject or a credential in a label
    /// is one series per request, and a series is a bill. Matching is exact, case-insensitive.
    /// </summary>
    private static readonly HashSet<string> BannedLabelKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "org_id",
        "organisation_id",
        "organization_id",
        "org",
        "tenant",
        "tenant_id",
        "project_id",
        "run_id",
        "job_id",
        "trace_id",
        "span_id",
        "request_id",
        "session_id",
        "user_id",
        "email",
        "token",
        "secret",
        "billing_key",
        "artifact_id",
        "id",
    };

    /// <summary>The failure-code label is an exception: it is a closed vocabulary of words, not a key.</summary>
    private static readonly HashSet<string> AllowedTokenLabelKeys = new(StringComparer.Ordinal)
    {
        "code",
        "reason",
        "error.type",
    };

    private readonly PostgresFixture _postgres;

    public MetricCatalogueTests(PostgresFixture postgres) => _postgres = postgres;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void EveryEntryNamesExactlyOneOfARealSourceOrAReason()
    {
        var catalogue = LoadCatalogue();

        Validate(catalogue, published: static (_, _) => true).ShouldBeEmpty();
    }

    [Fact]
    public async Task EverySourceInstrumentIsPublishedByTheRealRegistration()
    {
        using var listener = new InstrumentProbe();
        using var host = BuildTelemetryHost();
        await PublishApplicationInstrumentsAsync();
        await PublishDatabaseInstrumentsAsync();
        PublishServerInstruments();
        ProbeRuntimeInstruments();

        var catalogue = LoadCatalogue();
        var problems = Validate(catalogue, listener.IsPublished);

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void NoLabelEscapesTheBoundedVocabulary()
    {
        var catalogue = LoadCatalogue();

        foreach (var entry in Entries(catalogue))
        {
            var labels = LabelKeys(entry);
            labels.ShouldBeUnique($"entry '{Text(entry, "id")}' repeats a label key");

            foreach (var label in labels)
            {
                BannedLabelKeys.Contains(label).ShouldBeFalse(
                    $"entry '{Text(entry, "id")}' labels a series with '{label}', which is an identifier "
                    + "or a credential; labels must be bounded token vocabularies");
            }
        }
    }

    [Fact]
    public void AnEntryWithNeitherSourceNorReasonIsRefused()
    {
        var document = Parse(
            """
            version: 1
            environment_attribute: deployment.environment
            meters:
              evidgate: Evidgate
            entries:
              - id: orphan
                instrument: nothing_emits_me
                meter: evidgate
                kind: counter
                unit: "{row}"
                description: an entry that claims a series nobody publishes
                labels: []
            """);

        Validate(document, published: static (_, _) => false)
            .ShouldContain(problem => problem.Contains("orphan", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEntryWithBothASourceAndAReasonIsRefused()
    {
        var document = Parse(
            """
            version: 1
            environment_attribute: deployment.environment
            meters:
              evidgate: Evidgate
            entries:
              - id: ambiguous
                instrument: task_dead_letter_total
                meter: evidgate
                kind: counter
                unit: "{row}"
                description: claims both a source and an absence
                labels: []
                source: src/BuildingBlocks/Persistence/Tasks/TaskAlertNotifier.cs
                unavailable: plan 08 is unbuilt
            """);

        Validate(document, published: static (_, _) => true)
            .ShouldContain(problem => problem.Contains("ambiguous", StringComparison.Ordinal));
    }

    [Fact]
    public void ABannedLabelIsRefused()
    {
        var document = Parse(
            """
            version: 1
            environment_attribute: deployment.environment
            meters:
              evidgate: Evidgate
            entries:
              - id: leaky
                instrument: task_dead_letter_total
                meter: evidgate
                kind: counter
                unit: "{row}"
                description: labels a tenant, which is one series per customer
                labels: [org_id]
                source: src/BuildingBlocks/Persistence/Tasks/TaskAlertNotifier.cs
            """);

        Validate(document, published: static (_, _) => true)
            .ShouldContain(problem => problem.Contains("leaky", StringComparison.Ordinal));
    }

    private static YamlStream LoadCatalogue() => Parse(File.ReadAllText(CataloguePath()));

    /// <summary>The repository's copy of the catalogue, found by walking up to the solution file.</summary>
    private static string CataloguePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "infra", "metrics", "catalogue.yaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("infra/metrics/catalogue.yaml is not above the test output directory");
    }

    private static YamlStream Parse(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(new YamlDotNet.Core.Parser(reader));
        return stream;
    }

    /// <summary>
    /// Applies every rule to one parsed catalogue and returns the problems rather than throwing, so a
    /// negative test can prove the rule fires and a positive test can print them all at once.
    /// </summary>
    /// <param name="catalogue">The parsed document.</param>
    /// <param name="published">True when the meter publishes that instrument name.</param>
    private static List<string> Validate(YamlStream catalogue, Func<string, string, bool> published)
    {
        var problems = new List<string>();
        var document = (YamlMappingNode)catalogue.Documents[0].RootNode;

        var meterNames = MeterNames(document);
        var entries = Entries(document).ToList();
        entries.ShouldNotBeEmpty("the catalogue declares no entries");

        foreach (var entry in entries)
        {
            var id = Text(entry, "id");
            var source = Text(entry, "source");
            var unavailable = Text(entry, "unavailable");

            if (string.IsNullOrWhiteSpace(source) == string.IsNullOrWhiteSpace(unavailable))
            {
                problems.Add(
                    $"entry '{id}' must name exactly one of source or unavailable: a metric with no source is "
                    + "reported as unavailable with a reason, never as zero");
                continue;
            }

            if (string.IsNullOrWhiteSpace(Text(entry, "description")))
            {
                problems.Add($"entry '{id}' has no description");
            }

            // An unavailable metric has no instrument, so naming one would claim a series that does not exist.
            var fields = string.IsNullOrWhiteSpace(unavailable) ? SeriesFields : AbsentFields;
            foreach (var field in fields)
            {
                var value = Text(entry, field);
                if (string.IsNullOrWhiteSpace(unavailable) && string.IsNullOrWhiteSpace(value))
                {
                    problems.Add($"entry '{id}' has no {field}");
                }
                else if (!string.IsNullOrWhiteSpace(unavailable) && !string.IsNullOrWhiteSpace(value))
                {
                    problems.Add($"entry '{id}' is unavailable but still names {field}, which claims a series");
                }
            }

            if (!string.IsNullOrWhiteSpace(unavailable))
            {
                continue;
            }

            if (!meterNames.TryGetValue(Text(entry, "meter"), out var meter))
            {
                problems.Add($"entry '{id}' names meter alias '{Text(entry, "meter")}', which meters: does not declare");
                continue;
            }

            if (!published(meter, Text(entry, "instrument")))
            {
                problems.Add(
                    $"entry '{id}' claims {meter}/{Text(entry, "instrument")}, which no registration publishes");
            }
        }

        foreach (var entry in entries)
        {
            foreach (var label in LabelKeys(entry))
            {
                if (BannedLabelKeys.Contains(label) && !AllowedTokenLabelKeys.Contains(label))
                {
                    problems.Add(
                        $"entry '{Text(entry, "id")}' labels a series with '{label}', which is an identifier or a "
                        + "credential, not a bounded token vocabulary");
                }
            }
        }

        return problems;
    }

    /// <summary>The fields a published series must name: what the exporter sees and how it is labelled.</summary>
    private static readonly string[] SeriesFields = ["instrument", "meter", "kind", "unit"];

    /// <summary>The same fields, which an unavailable entry must leave empty rather than invent a series.</summary>
    private static readonly string[] AbsentFields = ["instrument", "meter", "kind", "unit"];

    private static Dictionary<string, string> MeterNames(YamlMappingNode document) =>
        ((YamlMappingNode)document.Children[new YamlScalarNode("meters")])
        .Children
        .ToDictionary(
            child => ((YamlScalarNode)child.Key).Value!,
            child => ((YamlScalarNode)child.Value).Value!,
            StringComparer.Ordinal);

    private static IEnumerable<YamlMappingNode> Entries(YamlStream catalogue) =>
        Entries((YamlMappingNode)catalogue.Documents[0].RootNode);

    private static IEnumerable<YamlMappingNode> Entries(YamlMappingNode document) =>
        ((YamlSequenceNode)document.Children[new YamlScalarNode("entries")])
        .Children
        .Cast<YamlMappingNode>();

    private static IEnumerable<string> LabelKeys(YamlMappingNode entry) =>
        entry.Children.TryGetValue(new YamlScalarNode("labels"), out var node) && node is YamlSequenceNode labels
            ? labels.Children.Select(label => ((YamlScalarNode)label).Value ?? string.Empty)
            : [];

    private static string Text(YamlMappingNode entry, string field) =>
        entry.Children.TryGetValue(new YamlScalarNode(field), out var node) && node is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : string.Empty;

    /// <summary>
    /// Builds the metrics pipeline the hosts build, so the runtime instrumentation subscribes to the
    /// runtime's own meter exactly as it does in the Api and the Worker.
    /// </summary>
    private static IHost BuildTelemetryHost()
    {
        var builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = "Staging" });
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Telemetry:OtlpEndpoint"] = null,
            });
        builder.AddEvidgateTelemetry("evidgate-catalogue-test");

        var host = builder.Build();
        _ = host.Services.GetRequiredService<MeterProvider>();
        return host;
    }

    /// <summary>
    /// Publishes the application's own instruments the way the hosts do: every recording method the
    /// catalogue names is called once, and the queue gauge, dispatcher and dead-letter counter are
    /// constructed. The dead-port data source carries no credential and is never opened by a gauge.
    /// </summary>
    private static async Task PublishApplicationInstrumentsAsync()
    {
        EvidgateTelemetry.RecordRunIncomplete();
        EvidgateTelemetry.RecordArtifactFailure("upload", "checksum_mismatch");
        EvidgateTelemetry.RecordUsageUnits("results", 1);
        EvidgateTelemetry.RecordEvaluationDecisionLatency(TimeSpan.FromMilliseconds(1));

        // The dead-letter counter is created by an internal notifier, and the lease counter by the
        // public claimer: running each static constructor is what publishes their instruments.
        var notifier = typeof(TaskQueueMetrics).Assembly.GetType(
            "Evidgate.BuildingBlocks.Persistence.Tasks.TaskAlertNotifier", throwOnError: true)!;
        RuntimeHelpers.RunClassConstructor(notifier.TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(TaskClaimer).TypeHandle);

        await using var dataSource = new NpgsqlDataSourceBuilder(
            "Host=127.0.0.1;Port=1;Username=evidgate_app;Database=evidgate;Timeout=2").Build();
        using var gauge = new TaskQueueStateGauge(dataSource, NullLogger<TaskQueueStateGauge>.Instance);
        using var dispatcher = new OutboxDispatcher(
            dataSource,
            Options.Create(new TasksOptions()),
            NullLogger<OutboxDispatcher>.Instance,
            TimeProvider.System);
        _ = gauge;
        _ = dispatcher;
    }

    /// <summary>
    /// Publishes the pool and command instruments through a real connection: Npgsql creates them with
    /// the data source, and one command proves the operation histogram exists.
    /// </summary>
    private async Task PublishDatabaseInstrumentsAsync()
    {
        // Creating a database is what starts the shared container; the fixture connects lazily.
        await using var database = await _postgres.CreateMigratedDatabaseAsync();
        using var connection = new NpgsqlConnection(database.AppConnectionString);
        connection.Open();
        _ = connection.ExecuteScalar<int>("select 1");
    }

    /// <summary>
    /// Builds the server metrics subscription the Api configures (its <c>AddAspNetCoreInstrumentation</c>),
    /// which is what creates the hosting meter and its request-duration histogram.
    /// </summary>
    private static void PublishServerInstruments()
    {
        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(TelemetryMeters.AspNetCoreHosting)
            .AddAspNetCoreInstrumentation()
            .Build();
    }

    /// <summary>
    /// Reads every built-in runtime metric the catalogue names, which is what makes the runtime
    /// publish the instrument behind it on the System.Runtime meter.
    /// </summary>
    private static void ProbeRuntimeInstruments()
    {
        GC.Collect();
        _ = GC.GetTotalMemory(forceFullCollection: true);
        _ = GC.GetTotalAllocatedBytes();
        _ = GC.GetGCMemoryInfo();
        _ = GC.GetTotalPauseDuration();
        ThreadPool.GetAvailableThreads(out _, out _);
        _ = ThreadPool.ThreadCount;
        _ = ThreadPool.CompletedWorkItemCount;
        _ = ThreadPool.PendingWorkItemCount;
        _ = Monitor.LockContentionCount;
        _ = System.Runtime.JitInfo.GetCompilationTime();
        _ = System.Runtime.JitInfo.GetCompiledILBytes();
        _ = System.Runtime.JitInfo.GetCompiledMethodCount();
        _ = Environment.WorkingSet;
    }

    /// <summary>Records which instruments were published to the listener, by meter name and instrument name.</summary>
    private sealed class InstrumentProbe : IDisposable
    {
        private readonly ConcurrentDictionary<(string Meter, string Instrument), bool> _published = new();
        private readonly MeterListener _listener;

        internal InstrumentProbe()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, _) =>
                    _published[(instrument.Meter.Name, instrument.Name)] = true,
            };
            _listener.Start();
        }

        internal bool IsPublished(string meter, string instrument) => _published.ContainsKey((meter, instrument));

        public void Dispose() => _listener.Dispose();
    }
}
