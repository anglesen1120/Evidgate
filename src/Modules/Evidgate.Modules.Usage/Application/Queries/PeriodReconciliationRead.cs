using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Tenancy.Contracts;
using Evidgate.Modules.Usage.Domain;
using Evidgate.Modules.Usage.Infrastructure.Persistence;

namespace Evidgate.Modules.Usage.Application.Queries;

/// <summary>Why a reconciled <c>(project, metric)</c> of a period does not agree, or null when it does.</summary>
internal static class ReconciliationMismatch
{
    /// <summary>The two totals differ.</summary>
    internal const string Quantity = "quantity";

    /// <summary>The totals agree but the row counts do not.</summary>
    internal const string RowCount = "row_count";

    /// <summary>The ledger holds rows of this scope and the roll-up holds none.</summary>
    internal const string MissingRollup = "missing_rollup";

    /// <summary>The roll-up holds a row of this scope and the ledger holds none.</summary>
    internal const string MissingLedger = "missing_ledger";
}

/// <summary>
/// One reconciled <c>(project, metric)</c> of a period: the ledger's live totals beside the roll-up's
/// stored ones. The ledger side is a sum over no rows (0) when the roll-up has a row it does not;
/// the roll-up side is null when the roll-up has no row at all, which is what
/// <see cref="RollupComputedAt"/> being null repeats for the caller.
/// </summary>
/// <param name="ProjectId">The project, or null for an org-level metric.</param>
/// <param name="Metric">The wire metric name.</param>
/// <param name="LedgerTotalQuantity">The live sum of the period's ledger rows, adjustments included.</param>
/// <param name="LedgerRowCount">How many ledger rows that sum covers.</param>
/// <param name="RollupTotalQuantity">The stored roll-up total, or null without a roll-up row.</param>
/// <param name="RollupRowCount">The stored row count, or null without a roll-up row.</param>
/// <param name="RollupComputedAt">When the roll-up row was last computed, or null without one.</param>
/// <param name="Matches">True when both sides exist and agree on the total and the row count.</param>
/// <param name="Mismatch">One of <see cref="ReconciliationMismatch"/>, or null when they agree.</param>
internal sealed record ReconciliationUnit(
    Guid? ProjectId,
    string Metric,
    decimal LedgerTotalQuantity,
    long LedgerRowCount,
    decimal? RollupTotalQuantity,
    long? RollupRowCount,
    DateTimeOffset? RollupComputedAt,
    bool Matches,
    string? Mismatch);

/// <summary>
/// A period metric the roll-up itself flagged as incomplete: the source stream showed usage the
/// ledger holds no row for, so its stored total is known to understate the period.
/// </summary>
/// <param name="ProjectId">The project, or null for an org-level metric.</param>
/// <param name="Metric">The wire metric name.</param>
/// <param name="TotalQuantity">The stored total the flag qualifies.</param>
/// <param name="ComputedAt">When the flagged roll-up row was computed.</param>
internal sealed record ReconciliationGap(
    Guid? ProjectId,
    string Metric,
    decimal TotalQuantity,
    DateTimeOffset ComputedAt);

/// <summary>
/// One ledger key of the period that a producer sent again: a replay is deduped by the unique
/// <c>(org_id, billing_key)</c> and recorded per attempt, so a retrying producer stays visible.
/// </summary>
/// <param name="BillingKey">The source metering key that was repeated.</param>
/// <param name="Attempts">How many times the key was seen, the first write included.</param>
/// <param name="FirstSeenAt">The first attempt.</param>
/// <param name="LastSeenAt">The most recent attempt.</param>
internal sealed record DuplicateUsageAttempt(
    string BillingKey,
    int Attempts,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

/// <summary>
/// The reconciliation of one period: the ledger and the roll-up side by side per
/// <c>(project, metric)</c>, the roll-up's own gap flags, and the duplicate attempts of the
/// period's keys.
/// </summary>
/// <param name="Period">The reconciled period, <c>YYYY-MM</c>.</param>
/// <param name="Units">Per <c>(project, metric)</c> comparison, ordered by metric then project.</param>
/// <param name="Gaps">Roll-up rows the recompute flagged as incomplete.</param>
/// <param name="DuplicateAttempts">Repeated billing keys, most recent attempt first.</param>
/// <param name="DuplicateAttemptsTruncated">True when more duplicate keys exist than the list reports.</param>
internal sealed record PeriodReconciliation(
    string Period,
    IReadOnlyList<ReconciliationUnit> Units,
    IReadOnlyList<ReconciliationGap> Gaps,
    IReadOnlyList<DuplicateUsageAttempt> DuplicateAttempts,
    bool DuplicateAttemptsTruncated);

/// <summary>
/// <c>GET /internal/operator/orgs/{o}/usage-reconciliation?period=</c>: the documented invoice query
/// (12.4.1's statement), answered from the live ledger and the stored roll-up in the same read.
/// <para>
/// The comparison is aggregated in the database and joined once, so a period of any size costs one
/// aggregate statement plus one bounded duplicate-attempt read — never a query per row or per metric.
/// The read is audited like the ledger page it complements: one row per read, in the read's own
/// transaction, naming the operator, the org and the period.
/// </para>
/// </summary>
/// <remarks>
/// Where the two sides can disagree is reported, never repaired here: a missing roll-up means the
/// recompute has not covered that metric yet, a missing ledger row means the roll-up holds a total
/// the facts no longer support, and the duplicate list explains a period that took more attempts to
/// arrive than it should have. Repairing either is a producer or recompute decision.
/// </remarks>
internal sealed class PeriodReconciliationReader(
    ITenantScopeFactory scopes,
    ITenantDb db,
    IAuditTrail audit,
    ITenantLimits limits,
    UsageReadStore store)
{
    /// <summary>Most duplicate keys one read reports.</summary>
    internal const int MaxDuplicateAttempts = 200;

    /// <summary>The audited action of one reconciliation read.</summary>
    internal const string Action = "usage.reconciliation_read";

    /// <summary>
    /// Validates the period, opens the org's scope, reads the comparison and the duplicate attempts,
    /// and writes the audit row of that read inside the same transaction.
    /// </summary>
    /// <param name="orgId">The org the route names.</param>
    /// <param name="operatorId">The authenticated operator, the audit row's actor.</param>
    /// <param name="period">The period to reconcile, <c>YYYY-MM</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<Result<PeriodReconciliation>> ExecuteAsync(
        Guid orgId,
        Guid operatorId,
        string? period,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(
            ModuleName.Usage,
            nameof(PeriodReconciliationReader));

        if (period is null)
        {
            return Result.Failure<PeriodReconciliation>(Error.Invalid(
                "the period is required",
                [new ErrorDetail("period", "required")]));
        }

        if (!UsagePeriod.IsWellFormed(period))
        {
            return Result.Failure<PeriodReconciliation>(Error.Invalid(
                "the period is not a YYYY-MM billing month",
                [new ErrorDetail("period", "format")]));
        }

        using var unit = audit.BeginUnit();
        await using var scope = await scopes.BeginAsync(orgId, null, false, cancellationToken).ConfigureAwait(false);

        if (await limits.ForOrganizationAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<PeriodReconciliation>(Error.NotFound("no such organization"));
        }

        var rows = await store.ReadReconciliationAsync(orgId, period, cancellationToken).ConfigureAwait(false);
        var units = rows.Select(Reconcile).ToArray();
        var gaps = rows
            .Where(row => row.GapFlag && row.RollupComputedAt is { } computedAt)
            .Select(row => new ReconciliationGap(
                row.ProjectId,
                row.Metric,
                row.RollupTotalQuantity ?? 0m,
                row.RollupComputedAt!.Value))
            .ToArray();

        // One row more than the report holds answers "are there more?" the same way a page does.
        var attempts = await store
            .ReadDuplicateAttemptsAsync(orgId, period, MaxDuplicateAttempts + 1, cancellationToken)
            .ConfigureAwait(false);
        var truncated = attempts.Count > MaxDuplicateAttempts;
        var duplicates = attempts.Take(MaxDuplicateAttempts).Select(ToDuplicate).ToArray();

        DescribeAudit(orgId, operatorId, period, units.Length, truncated);
        await audit.FlushAsync(orgId, db, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(new PeriodReconciliation(period, units, gaps, duplicates, truncated));
    }

    /// <summary>
    /// Classifies one joined row. The absent-side cases come first: a side with no row has no totals
    /// to compare, so reporting "quantity" for it would name the wrong cause.
    /// </summary>
    private static ReconciliationUnit Reconcile(ReconciliationRow row)
    {
        var ledgerTotal = row.LedgerTotalQuantity ?? 0m;
        var ledgerRows = row.LedgerRowCount ?? 0L;

        var mismatch = row.RollupComputedAt is null
            ? ReconciliationMismatch.MissingRollup
            : row.LedgerTotalQuantity is null
                ? ReconciliationMismatch.MissingLedger
                : row.RollupTotalQuantity != ledgerTotal
                    ? ReconciliationMismatch.Quantity
                    : row.RollupRowCount != ledgerRows
                        ? ReconciliationMismatch.RowCount
                        : null;

        return new ReconciliationUnit(
            row.ProjectId,
            row.Metric,
            ledgerTotal,
            ledgerRows,
            row.RollupTotalQuantity,
            row.RollupRowCount,
            row.RollupComputedAt,
            Matches: mismatch is null,
            Mismatch: mismatch);
    }

    private static DuplicateUsageAttempt ToDuplicate(DuplicateAttemptRow row) =>
        new(row.BillingKey, row.Attempts, row.FirstSeenAt, row.LastSeenAt);

    /// <summary>
    /// Records the period, how many scopes were compared and whether the duplicate list was cut. No
    /// total, amount or key reaches the audit chain: this row says which period was read, not what it
    /// billed.
    /// </summary>
    private void DescribeAudit(Guid orgId, Guid operatorId, string period, int units, bool truncated)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["period"] = period,
            ["count"] = units.ToString(CultureInfo.InvariantCulture),
            ["outcome"] = truncated ? "duplicates_truncated" : "complete",
        };

        audit.Describe(new AuditObject("usage_rollup", orgId.ToString("D"), Metadata: metadata));
        audit.Record(Action, new AuditActor(AuditActorTypes.PlatformOperator, operatorId.ToString("D")));
    }
}
