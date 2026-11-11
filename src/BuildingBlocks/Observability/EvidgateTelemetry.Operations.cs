using System.Diagnostics.Metrics;

namespace Evidgate.BuildingBlocks.Observability;

/// <summary>
/// The operational outcome instruments the metrics catalogue adds to the application meter: kept
/// separate from the request-path vocabulary so the two files stay reviewable.
/// </summary>
public static partial class EvidgateTelemetry
{
    /// <summary>Counter of runs whose reconcile closed them without complete evidence.</summary>
    public const string RunIncompleteMetricName = "evidgate_run_incomplete_total";

    /// <summary>Counter of artifacts a completion or parse verdict refused.</summary>
    public const string ArtifactFailureMetricName = "evidgate_artifact_failure_total";

    private static readonly Counter<long> RunsIncomplete = Meter.CreateCounter<long>(
        RunIncompleteMetricName,
        unit: "{run}",
        description: "Runs closed by a reconcile as incomplete (a planned shard or required result never arrived). No labels: the rate is per deployment, not per run.");

    private static readonly Counter<long> ArtifactFailures = Meter.CreateCounter<long>(
        ArtifactFailureMetricName,
        unit: "{artifact}",
        description: "Artifacts a completion or parse verdict refused, by stage (upload, parse) and the bounded failure-code vocabulary. Never labelled with an artifact id, key or object path.");

    /// <summary>
    /// Counts one run that a reconcile settled as <c>incomplete</c>: the verdict was committed, so the
    /// denominator of the incomplete rate grows with every decision the platform actually made.
    /// </summary>
    public static void RecordRunIncomplete() => RunsIncomplete.Add(1);

    /// <summary>
    /// Counts one artifact failure. <paramref name="stage"/> is the closed stage vocabulary
    /// (<c>upload</c>, <c>parse</c>) and <paramref name="reason"/> the domain failure code; a value
    /// outside either vocabulary — including nothing — collapses to <c>other</c>, so a caller can never
    /// turn a metric tag into free text.
    /// </summary>
    /// <param name="stage">The stage that refused the artifact.</param>
    /// <param name="reason">The artifact failure code, or nothing when the caller has none.</param>
    public static void RecordArtifactFailure(string? stage, string? reason) =>
        ArtifactFailures.Add(
            1,
            new KeyValuePair<string, object?>("stage", stage is "upload" or "parse" ? stage : "other"),
            new KeyValuePair<string, object?>("reason", ArtifactReason(reason)));

    /// <summary>Counter of units the usage ledger committed, per metered metric.</summary>
    public const string UsageUnitsMetricName = "evidgate_usage_units_total";

    private static readonly Counter<double> UsageUnits = Meter.CreateCounter<double>(
        UsageUnitsMetricName,
        unit: "{unit}",
        description: "Billed units committed to the usage ledger, by the closed metric vocabulary of that table (results, artifact_gb_month, …). No money and no tenant: prices are absent by ruling, so a cost view multiplies a published price by this rate.");

    /// <summary>
    /// Adds one committed ledger row's quantity, labelled with its metric. Any metric outside the table's
    /// closed vocabulary collapses to <c>other</c>; the column's own check refuses such a row, so the label
    /// can never become free text.
    /// </summary>
    /// <param name="metric">The ledger row's metric wire name.</param>
    /// <param name="quantity">The measured amount; zero is indistinguishable from absent, and the caller only reports committed rows.</param>
    public static void RecordUsageUnits(string? metric, double quantity) =>
        UsageUnits.Add(
            quantity,
            new KeyValuePair<string, object?>("metric", UsageMetricToken(metric)));

    /// <summary>Counter of billing keys a ledger batch refused because each already claims another payload.</summary>
    public const string UsageBillingKeyConflictMetricName = "evidgate_usage_billing_key_conflicts_total";

    private static readonly Counter<long> UsageBillingKeyConflicts = Meter.CreateCounter<long>(
        UsageBillingKeyConflictMetricName,
        unit: "{key}",
        description: "Billing keys whose ledger payload conflicted (a stored row with another payload, or a request repeating a key with a different payload). The batch is rolled back and nothing is billed: any sustained rate means a producer reuses a key for new work. No labels: the error log names the key and the digests.");

    /// <summary>
    /// Counts one conflicting billing key. The batch it belongs to wrote nothing, so this counts a refusal, not billed
    /// work; the key and the two digests are only in the error log.
    /// </summary>
    public static void RecordUsageBillingKeyConflict() => UsageBillingKeyConflicts.Add(1);

    /// <summary>Bounds the metric label to the table's seven declared values.</summary>
    private static string UsageMetricToken(string? metric) => metric switch
    {
        "results" => metric,
        "artifact_gb_month" => metric,
        "egress_gb" => metric,
        "ai_tokens" => metric,
        "support_hours" => metric,
        "runner_minutes" => metric,
        "device_minutes" => metric,
        _ => "other",
    };

    /// <summary>Bounds the failure-code label to the codes this build can write, so an unknown code is one series.</summary>
    private static string ArtifactReason(string? reason) => reason switch
    {
        "size_mismatch" => reason,
        "checksum_mismatch" => reason,
        "media_type_mismatch" => reason,
        "too_large" => reason,
        "cap_exceeded" => reason,
        "object_missing" => reason,
        "secret_rejected" => reason,
        "parse_oom" => reason,
        "parse_timeout" => reason,
        "parse_crashed" => reason,
        _ => "other",
    };
}
