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
public sealed partial class MetricCatalogueTests
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
}
