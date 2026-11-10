using System.Diagnostics;
using Evidgate.BuildingBlocks.Observability;
using OpenTelemetry.Metrics;
using Shouldly;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Hosts.IntegrationTests;

/// <summary>
/// Pins <c>infra/metrics/catalogue.yaml</c>: every entry names exactly one of a real instrument or a
/// non-empty unavailable reason, the registration the hosts build actually collects every sourced
/// instrument, and no label escapes the bounded vocabulary. The rule behind it is the operations
/// plan's: a metric that cannot be measured is reported as unavailable with a reason, never as zero.
/// </summary>
/// <remarks>
/// The evidence is the production pipeline's own export, never a listener's view of the process:
/// <see cref="MetricCatalogueHarness"/> boots the hosts' <c>AddEvidgateTelemetry</c> (plus the Api's
/// server instrumentation) on an ASP.NET Core host and reads the <see cref="MeterProvider"/> it built
/// through an in-memory reader. A meter the production registration stops subscribing to exports
/// nothing, so that removal fails here instead of silently dropping a series. The same export proves
/// each instrument's unit and kind, and that its measured tag keys stay inside the entry's declared
/// labels.
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

    /// <summary>
    /// Tags the instrumentation adds on its own, by the meter that emits them: the OpenTelemetry
    /// semantic-convention server and database attributes. They name a host or a pool, never a tenant
    /// or a request, so an entry does not declare them. The map is scoped per meter on purpose: the
    /// application's own instruments must declare every key they use, so an undeclared label cannot
    /// hide behind a library attribute name.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> InstrumentationTagKeysByMeter =
        new(StringComparer.Ordinal)
        {
            [TelemetryMeters.Npgsql] = ["server.address", "server.port", "db.namespace"],
            [TelemetryMeters.AspNetCoreHosting] = ["server.address", "server.port"],
        };

    /// <summary>The OpenTelemetry metric types each catalogue kind may be exported as.</summary>
    private static readonly Dictionary<string, HashSet<MetricType>> ExportedKinds = new(StringComparer.Ordinal)
    {
        ["counter"] = [MetricType.LongSum, MetricType.DoubleSum],
        ["observable_counter"] = [MetricType.LongSum, MetricType.DoubleSum],
        ["observable_updowncounter"] = [MetricType.LongSumNonMonotonic, MetricType.DoubleSumNonMonotonic],
        ["histogram"] = [MetricType.Histogram, MetricType.ExponentialHistogram],
        ["observable_gauge"] = [MetricType.LongGauge, MetricType.DoubleGauge],
    };

    /// <summary>How long one flush may take before the collection is reported as incomplete.</summary>
    private const int ForceFlushTimeoutMilliseconds = 10_000;

    /// <summary>How long the provider is polled for the last instrument a producer creates on first use.</summary>
    private static readonly TimeSpan CollectionDeadline = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan CollectionPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly PostgresFixture _postgres;

    public MetricCatalogueTests(PostgresFixture postgres) => _postgres = postgres;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void EveryEntryNamesExactlyOneOfARealSourceOrAReason()
    {
        var catalogue = LoadCatalogue();

        Validate(catalogue, collected: static (_, _) => true).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task EverySourceInstrumentIsCollectedByTheProductionProvider()
    {
        await using var database = await _postgres.CreateMigratedDatabaseAsync();
        var collected = new ExportedMetrics();
        await using var harness = await MetricCatalogueHarness.StartAsync(database, collected, Token);

        await harness.ProduceAsync(Token);

        var catalogue = LoadCatalogue();
        var document = (YamlMappingNode)catalogue.Documents[0].RootNode;
        await CollectAsync(harness.Provider, collected, ExpectedSeries(document));

        var problems = Validate(catalogue, collected.Contains);
        problems.AddRange(ShapeProblems(document, collected));

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

        Validate(document, collected: static (_, _) => false)
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

        Validate(document, collected: static (_, _) => true)
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

        Validate(document, collected: static (_, _) => true)
            .ShouldContain(problem => problem.Contains("leaky", StringComparison.Ordinal));
    }

    [Fact]
    public void AnApplicationMetricMayNotClaimAnInstrumentationAttribute()
    {
        // The library exceptions are scoped to the meters that emit them: the same key on the
        // application's own meter is an undeclared label, so a tag cannot borrow a library name
        // to escape the bounded vocabulary.
        IsAllowedTag(TelemetryMeters.Npgsql, "server.address", declaredLabels: []).ShouldBeTrue();
        IsAllowedTag(TelemetryMeters.AspNetCoreHosting, "server.port", declaredLabels: []).ShouldBeTrue();
        IsAllowedTag("Evidgate", "server.address", declaredLabels: []).ShouldBeFalse();
        IsAllowedTag("Evidgate", "server.port", declaredLabels: []).ShouldBeFalse();
        IsAllowedTag("Evidgate", "db.namespace", declaredLabels: []).ShouldBeFalse();
        IsAllowedTag("Evidgate", "run_id", declaredLabels: []).ShouldBeFalse();
        IsAllowedTag("Evidgate", "pool", declaredLabels: ["pool"]).ShouldBeTrue();
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
    /// <param name="collected">True when the production provider exported that meter's instrument.</param>
    private static List<string> Validate(YamlStream catalogue, Func<string, string, bool> collected)
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

            if (!collected(meter, Text(entry, "instrument")))
            {
                problems.Add(
                    $"entry '{id}' claims {meter}/{Text(entry, "instrument")}, which the registration the hosts "
                    + "build does not collect");
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

    /// <summary>
    /// Compares each sourced entry's export with its declaration: the exported unit and metric type must be
    /// the declared ones, and every tag the instrumentation measured must be one the entry declares.
    /// </summary>
    /// <param name="document">The parsed catalogue.</param>
    /// <param name="exported">Everything the production provider collected.</param>
    private static List<string> ShapeProblems(YamlMappingNode document, ExportedMetrics exported)
    {
        var problems = new List<string>();
        var meterNames = MeterNames(document);

        foreach (var entry in Entries(document))
        {
            if (!string.IsNullOrWhiteSpace(Text(entry, "unavailable"))
                || !meterNames.TryGetValue(Text(entry, "meter"), out var meter))
            {
                continue;
            }

            var id = Text(entry, "id");
            var instrument = Text(entry, "instrument");
            var declaredKind = Text(entry, "kind");
            if (!ExportedKinds.TryGetValue(declaredKind, out var exportedKinds))
            {
                problems.Add($"entry '{id}' declares kind '{declaredKind}', which the catalogue does not define");
                continue;
            }

            var exports = exported.Exports(meter, instrument);
            if (exports.Length == 0)
            {
                problems.Add($"entry '{id}' claims {meter}/{instrument}, which the provider exported nothing for");
                continue;
            }

            var declaredUnit = Text(entry, "unit");
            var declaredLabels = LabelKeys(entry).ToHashSet(StringComparer.Ordinal);
            foreach (var export in exports)
            {
                if (!string.Equals(export.Unit, declaredUnit, StringComparison.Ordinal))
                {
                    problems.Add(
                        $"entry '{id}' declares unit '{declaredUnit}' but the provider exported '{export.Unit}'");
                }

                if (!exportedKinds.Contains(export.MetricType))
                {
                    problems.Add(
                        $"entry '{id}' declares kind '{declaredKind}' but the provider exported {export.MetricType}");
                }

                foreach (var point in export.MetricPoints)
                {
                    TagProblems(point, id, meter, declaredLabels, problems);
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// True when the entry's own labels declare that tag key, or when the meter's library adds it on
    /// its own. Scoped by meter, so an application metric using <c>server.address</c> without declaring
    /// it is still a problem.
    /// </summary>
    /// <param name="meter">The meter that exported the instrument.</param>
    /// <param name="tagKey">The key of one measured tag.</param>
    /// <param name="declaredLabels">The label keys the entry declares.</param>
    private static bool IsAllowedTag(string meter, string tagKey, HashSet<string> declaredLabels) =>
        declaredLabels.Contains(tagKey)
        || (InstrumentationTagKeysByMeter.TryGetValue(meter, out var instrumentation)
            && instrumentation.Contains(tagKey));

    /// <summary>
    /// Checks the tag keys of one exported measurement: every key must be one the entry declares, or an
    /// attribute that meter's instrumentation adds on its own.
    /// </summary>
    /// <param name="point">One exported measurement.</param>
    /// <param name="id">The catalogue entry that owns the instrument.</param>
    /// <param name="meter">The meter that exported the instrument.</param>
    /// <param name="declaredLabels">The label keys the entry declares.</param>
    /// <param name="problems">The problem list to extend.</param>
    private static void TagProblems(
        MetricPoint point,
        string id,
        string meter,
        HashSet<string> declaredLabels,
        List<string> problems)
    {
        foreach (var tag in point.Tags)
        {
            if (!IsAllowedTag(meter, tag.Key, declaredLabels))
            {
                problems.Add(
                    $"entry '{id}' measured tag '{tag.Key}' on {meter}, which the entry does not declare: an "
                    + "undeclared label is one series per value");
            }
        }
    }

    /// <summary>
    /// Flushes the production provider until every sourced entry has been exported, or the deadline
    /// passes: the last instrument a producer creates on first use needs a second collection.
    /// </summary>
    /// <param name="provider">The provider the production registration built.</param>
    /// <param name="collected">The reader's sink.</param>
    /// <param name="series">The meter and instrument of every sourced entry.</param>
    private static async Task CollectAsync(
        MeterProvider provider,
        ExportedMetrics collected,
        List<(string Meter, string Instrument)> series)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            _ = provider.ForceFlush(ForceFlushTimeoutMilliseconds);
            if (series.All(entry => collected.Contains(entry.Meter, entry.Instrument))
                || elapsed.Elapsed >= CollectionDeadline)
            {
                // A series still missing is reported by the caller as an uncollected catalogue entry.
                return;
            }

            await Task.Delay(CollectionPollInterval, Token);
        }
    }

    /// <summary>The meter and instrument of every entry that names a source, with the alias resolved.</summary>
    private static List<(string Meter, string Instrument)> ExpectedSeries(YamlMappingNode document)
    {
        var meterNames = MeterNames(document);
        var series = new List<(string Meter, string Instrument)>();
        foreach (var entry in Entries(document))
        {
            if (string.IsNullOrWhiteSpace(Text(entry, "unavailable"))
                && meterNames.TryGetValue(Text(entry, "meter"), out var meter))
            {
                series.Add((meter, Text(entry, "instrument")));
            }
        }

        return series;
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
}
