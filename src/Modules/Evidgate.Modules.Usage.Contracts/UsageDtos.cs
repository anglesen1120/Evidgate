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

    /// <summary>
    /// <c>egress_gb</c>: artifact egress, billed per GB. Its source does not exist yet. Whether an unmetered row is
    /// representable at all is 12.3.1-T2.2's open decision: <c>quantity</c> is NOT NULL today, so no null-quantity row can
    /// exist, while T60/AC8 want this metric reported as unavailable rather than 0.
    /// </summary>
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
/// <param name="Quantity">
/// The measured amount in <paramref name="Unit"/>, never negative: a correction is a new row with <c>adjustment_of</c>
/// set (12.3.1-T1's adjustment model, the two-person act of T69), so a plain row that subtracted from the roll-up would
/// be history nobody can audit. A row is a fact, never a counter: the roll-up sums rows (T43).
/// </param>
/// <param name="Unit">
/// The unit <paramref name="Quantity"/> is expressed in, e.g. <c>batch</c>, <c>gb_month</c>, <c>hour</c>; 1..32
/// characters, counted in code points as the column's check does.
/// </param>
/// <param name="Period">The billing period, exactly <c>YYYY-MM</c>.</param>
/// <param name="OccurredAt">
/// When the metered work happened (the ledger keeps it apart from <c>created_at</c>), stored to the microsecond as the
/// instant it names; the offset it was written with never reaches the row.
/// </param>
/// <remarks>
/// <see cref="BillingKey"/> and <see cref="Unit"/> are stored as PostgreSQL <c>text</c>: neither may carry U+0000 or a
/// lone UTF-16 surrogate, which the database cannot represent and would reject as a server error.
/// </remarks>
public sealed record UsageEntryInput(
    string BillingKey,
    UsageMetric Metric,
    Guid? ProjectId,
    decimal Quantity,
    string Unit,
    string Period,
    DateTimeOffset OccurredAt);

/// <summary>
/// One ledger row as recorded, read back by <see cref="IUsageLedger.FindAsync"/>: the billable content the ledger holds
/// for a key. <c>occurred_at</c> is deliberately absent — a
/// meter that re-derives a batch's instant from a different clock still bills the same fact, so the instant is not part
/// of the identity a replay must match.
/// </summary>
/// <param name="BillingKey">The key the row was recorded under (the first write's key, which is the caller's).</param>
/// <param name="Metric">The stored <c>metric</c> column: one of <see cref="UsageLedgerMetricNames"/>' values.</param>
/// <param name="ProjectId">The row's project, or null for an org-level metric.</param>
/// <param name="Quantity">The recorded amount in <paramref name="Unit"/>.</param>
/// <param name="Unit">The recorded unit.</param>
/// <param name="Period">
/// The period the row was stored in: the period the entry named, unless the row was metered for a month already
/// billed, in which case the ledger moved it into the next open one.
/// </param>
/// <param name="OriginalPeriod">
/// The <c>YYYY-MM</c> period the row was metered for when the ledger re-dated it, or null when nothing moved. A
/// payload comparison uses it instead of <paramref name="Period"/>, because the move is not the payload's claim.
/// </param>
public sealed record UsageEntryRecord(
    string BillingKey,
    string Metric,
    Guid? ProjectId,
    decimal Quantity,
    string Unit,
    string Period,
    string? OriginalPeriod);

/// <summary>
/// One <see cref="IUsageLedger.RecordAsync"/> call: the entries of one metering source (04.8.1's batch,
/// 06.5.2's period, 12.2.1's support hours).
/// <para>
/// There is no <c>Idempotency-Key</c> here on purpose: the billing keys already make the write idempotent per row
/// (T43), while 00 §9.1's key governs a whole <em>request</em> and belongs to the route that serves one — the manual
/// operator intake of 12.3.1-T2.4, whose command claims it through the idempotency pipeline. A member here that the
/// in-process write path cannot honour would be a promise the contract does not keep.
/// </para>
/// </summary>
/// <param name="Entries">The entries to record; empty is a no-op and at most 1000 are accepted per call.</param>
public sealed record RecordUsageBatchRequest(
    IReadOnlyList<UsageEntryInput> Entries);

/// <summary>
/// What one batch recorded: the billing keys of the rows this call inserted, the ones it did not, and the
/// ones whose payload disagreed with the key, all in request order and never containing a key twice.
/// </summary>
/// <param name="Inserted">The billing keys of the rows inserted by this call.</param>
/// <param name="Duplicates">
/// The billing keys already on file with the same payload (a resent metering event) or repeated inside the request with
/// the same payload. A duplicate is a verified replay: the stored row equals the one this call would have written.
/// </param>
/// <param name="Conflicts">
/// The billing keys that already hold a different payload, or that the request itself repeated with a different
/// payload. A conflict is permanent: the batch wrote nothing, the key is never rewritten, and retrying the same
/// payload cannot succeed.
/// </param>
public sealed record UsageBatchResult(
    IReadOnlyList<string> Inserted,
    IReadOnlyList<string> Duplicates,
    IReadOnlyList<string> Conflicts);
