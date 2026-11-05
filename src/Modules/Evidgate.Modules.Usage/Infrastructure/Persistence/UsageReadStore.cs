using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.Modules.Usage.Infrastructure.Persistence;

/// <summary>
/// The billing operator's read path: Dapper over the caller's tenant scope, so every query runs
/// inside the open transaction, sees its uncommitted rows and answers zero rows for another tenant
/// whatever the arguments say.
/// </summary>
/// <remarks>
/// The store owns the SQL only. Bounds, cursor decoding, the period and metric rules and the audit
/// row belong to the application services that call it, so the same statement serves a page and a
/// test without a second copy.
/// </remarks>
internal sealed class UsageReadStore(ITenantDb db)
{
    /// <summary>
    /// Reads one page of the org's ledger rows, oldest first. <paramref name="limit"/> is the page
    /// size and the caller asks for one row more than it returns, so "is there more?" needs no count.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="period">The exact period, or null for every period.</param>
    /// <param name="metric">The exact wire metric, or null for every metric.</param>
    /// <param name="cursorCreatedAt">The last returned row's <c>created_at</c>, or null for the first page.</param>
    /// <param name="cursorId">The last returned row's id, or null for the first page.</param>
    /// <param name="limit">Rows to fetch, already bounded by the caller.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal Task<IReadOnlyList<UsageLedgerPageRow>> ReadLedgerPageAsync(
        Guid orgId,
        string? period,
        string? metric,
        DateTimeOffset? cursorCreatedAt,
        Guid? cursorId,
        int limit,
        CancellationToken cancellationToken) =>
        db.QueryAsync<UsageLedgerPageRow>(
            UsageReadStoreSql.LedgerPage,
            new
            {
                org_id = orgId,
                period,
                metric,
                cursor_created_at = cursorCreatedAt,
                cursor_id = cursorId,
                limit,
            },
            cancellationToken);

    /// <summary>
    /// Reads the period's comparison of the ledger against the roll-up: one row per
    /// <c>(project, metric)</c> either side holds, aggregated in the database, at most
    /// <paramref name="limit"/> rows; the caller asks for one more than it reports to detect truncation.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="period">The period to reconcile.</param>
    /// <param name="limit">Rows to fetch, already bounded by the caller.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal Task<IReadOnlyList<ReconciliationRow>> ReadReconciliationAsync(
        Guid orgId,
        string period,
        int limit,
        CancellationToken cancellationToken) =>
        db.QueryAsync<ReconciliationRow>(
            UsageReadStoreSql.Reconciliation,
            new { org_id = orgId, period, limit },
            cancellationToken);

    /// <summary>
    /// Reads the duplicate attempts of the period's ledger keys, newest first, at most
    /// <paramref name="limit"/> rows; the caller asks for one more than it reports to detect truncation.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="period">The period whose keys are asked about.</param>
    /// <param name="limit">Rows to fetch, already bounded by the caller.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal Task<IReadOnlyList<DuplicateAttemptRow>> ReadDuplicateAttemptsAsync(
        Guid orgId,
        string period,
        int limit,
        CancellationToken cancellationToken) =>
        db.QueryAsync<DuplicateAttemptRow>(
            UsageReadStoreSql.DuplicateAttempts,
            new { org_id = orgId, period, limit },
            cancellationToken);
}

/// <summary>Dapper shape of one <c>usage_ledger_entry</c> row of a page.</summary>
internal sealed class UsageLedgerPageRow
{
    public Guid Id { get; set; }

    public Guid? ProjectId { get; set; }

    public string Metric { get; set; } = string.Empty;

    public decimal Quantity { get; set; }

    public string Unit { get; set; } = string.Empty;

    public string Period { get; set; } = string.Empty;

    public string BillingKey { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; }

    public Guid? PriceRef { get; set; }

    public decimal? Amount { get; set; }

    public string? Currency { get; set; }

    public Guid? AdjustmentOf { get; set; }

    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Dapper shape of one reconciled <c>(project, metric)</c> of a period: both sides' totals, and
/// the roll-up's computed instant and gap flag. A null side is a side that holds no row.
/// </summary>
internal sealed class ReconciliationRow
{
    public Guid? ProjectId { get; set; }

    public string Metric { get; set; } = string.Empty;

    public decimal? LedgerTotalQuantity { get; set; }

    public long? LedgerRowCount { get; set; }

    public decimal? LedgerTotalAmount { get; set; }

    public string? LedgerCurrency { get; set; }

    public decimal? RollupTotalQuantity { get; set; }

    public long? RollupRowCount { get; set; }

    public decimal? RollupTotalAmount { get; set; }

    public string? RollupCurrency { get; set; }

    public DateTimeOffset? RollupComputedAt { get; set; }

    public bool GapFlag { get; set; }
}

/// <summary>Dapper shape of one <c>usage_duplicate_attempt</c> row joined to its ledger period.</summary>
internal sealed class DuplicateAttemptRow
{
    public string BillingKey { get; set; } = string.Empty;

    public int Attempts { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}
