namespace Evidgate.Modules.Usage.Contracts;

/// <summary>
/// The metered metrics of the ledger (12.3.1 §4). Each member is one value of
/// <c>usage_ledger_entry.metric</c>, stored under the wire name of
/// <see cref="UsageLedgerMetricNames"/> (<c>results</c>, <c>artifact_gb_month</c>, <c>egress_gb</c>,
/// <c>ai_tokens</c>, <c>support_hours</c>, <c>runner_minutes</c>, <c>device_minutes</c>); the set is closed
/// because the column carries a <c>check</c> constraint over exactly those seven strings.
/// </summary>
public enum UsageMetric
{
    /// <summary><c>results</c>: one unit per accepted event batch (rulings T50/T83 — metered per batch, never per event).</summary>
    Results,

    /// <summary><c>artifact_gb_month</c>: stored artifact size, billed per GB-month (06.5.2).</summary>
    ArtifactGbMonth,

    /// <summary><c>egress_gb</c>: artifact egress, billed per GB. Its source does not exist yet, so rows stay null-not-zero (T60).</summary>
    EgressGb,

    /// <summary><c>ai_tokens</c>: model tokens consumed by an authoring or healing run.</summary>
    AiTokens,

    /// <summary><c>support_hours</c>: operator support time, an org-level metric (no project).</summary>
    SupportHours,

    /// <summary><c>runner_minutes</c>: compute minutes of a run's executions.</summary>
    RunnerMinutes,

    /// <summary><c>device_minutes</c>: device-farm minutes of a run's executions.</summary>
    DeviceMinutes,
}

/// <summary>
/// The wire names of <see cref="UsageMetric"/>: the strings <c>usage_ledger_entry.metric</c> stores and the
/// migration's <c>check</c> constraint admits. A single home, so a caller that has to name a metric in SQL
/// (a test seed, an operator query) never re-spells one by hand.
/// </summary>
public static class UsageLedgerMetricNames
{
    /// <summary>The <c>results</c> metric (one row per accepted event batch).</summary>
    public const string Results = "results";

    /// <summary>The <c>artifact_gb_month</c> metric.</summary>
    public const string ArtifactGbMonth = "artifact_gb_month";

    /// <summary>The <c>egress_gb</c> metric.</summary>
    public const string EgressGb = "egress_gb";

    /// <summary>The <c>ai_tokens</c> metric.</summary>
    public const string AiTokens = "ai_tokens";

    /// <summary>The <c>support_hours</c> metric (org-level: <see cref="UsageEntryInput.ProjectId"/> is null).</summary>
    public const string SupportHours = "support_hours";

    /// <summary>The <c>runner_minutes</c> metric.</summary>
    public const string RunnerMinutes = "runner_minutes";

    /// <summary>The <c>device_minutes</c> metric.</summary>
    public const string DeviceMinutes = "device_minutes";

    /// <summary>Every wire name, in <see cref="UsageMetric"/> order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Results,
        ArtifactGbMonth,
        EgressGb,
        AiTokens,
        SupportHours,
        RunnerMinutes,
        DeviceMinutes,
    ];
}

/// <summary>
/// One ledger row a caller asks to record (12.3.1 §4, T43/T50): the dedupe identity is
/// <c>(org_id, billing_key)</c>, so the caller mints a billing key per source metering event
/// (<c>batch:&lt;event_batch_id&gt;:&lt;metric&gt;</c> for ingest) and resends are free.
/// </summary>
/// <param name="BillingKey">The per-source dedupe key, 1..200 characters, unique per org (ruling T43).</param>
/// <param name="Metric">The metered metric; it must be a declared <see cref="UsageMetric"/>.</param>
/// <param name="ProjectId">The project the usage belongs to, or null for an org-level metric (support hours).</param>
/// <param name="Quantity">The measured amount in <paramref name="Unit"/>. A row is a fact, never a counter: the roll-up sums rows (T43).</param>
/// <param name="Unit">The unit <paramref name="Quantity"/> is expressed in, e.g. <c>batch</c>, <c>gb_month</c>, <c>hour</c>.</param>
/// <param name="Period">The billing period, exactly <c>YYYY-MM</c>.</param>
/// <param name="OccurredAt">When the metered work happened (the ledger keeps it apart from <c>created_at</c>).</param>
public sealed record UsageEntryInput(
    string BillingKey,
    UsageMetric Metric,
    Guid? ProjectId,
    decimal Quantity,
    string Unit,
    string Period,
    DateTimeOffset OccurredAt);

/// <summary>
/// One <see cref="IUsageLedger.RecordAsync"/> call: the entries of one metering source (04.8.1's batch,
/// 06.5.2's period, 12.2.1's support hours) and, on the operator route 12.3.1-T2.4, the request's
/// <c>Idempotency-Key</c>.
/// </summary>
/// <param name="Entries">The entries to record; empty is a no-op and at most 1000 are accepted per call.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1), null on an in-process call (the billing key is the dedupe there).</param>
public sealed record RecordUsageBatchRequest(
    IReadOnlyList<UsageEntryInput> Entries,
    string? IdempotencyKey = null);

/// <summary>
/// What one batch recorded (12.3.1 §5): the billing keys of the rows this call inserted and the ones it did not,
/// both in request order and never containing a key twice.
/// </summary>
/// <param name="Inserted">The billing keys of the rows inserted by this call.</param>
/// <param name="Duplicates">The billing keys already on file (a resent metering event) or repeated inside the request; the first occurrence wins.</param>
public sealed record UsageBatchResult(
    IReadOnlyList<string> Inserted,
    IReadOnlyList<string> Duplicates);
