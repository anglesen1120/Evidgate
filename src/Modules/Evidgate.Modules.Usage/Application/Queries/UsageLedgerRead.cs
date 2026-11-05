using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Tenancy.Contracts;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Evidgate.Modules.Usage.Infrastructure.Persistence;

namespace Evidgate.Modules.Usage.Application.Queries;

/// <summary>One ledger row as the billing operator reads it: the stored billing fact, verbatim.</summary>
/// <param name="Id">The row's id (UUIDv7, app-generated at write time).</param>
/// <param name="ProjectId">The project the usage belongs to, or null for an org-level metric.</param>
/// <param name="Metric">The wire metric name the ledger stores.</param>
/// <param name="Quantity">The stored amount; an adjustment carries its signed quantity.</param>
/// <param name="Unit">The unit <paramref name="Quantity"/> is expressed in.</param>
/// <param name="Period">The billing period, <c>YYYY-MM</c>.</param>
/// <param name="BillingKey">The source metering key the row was deduped under.</param>
/// <param name="OccurredAt">When the metered work happened.</param>
/// <param name="PriceRef">The price row the amount was computed from, or null while unpriced.</param>
/// <param name="Amount">The priced amount, or null while unpriced.</param>
/// <param name="Currency">The amount's currency, or null while unpriced.</param>
/// <param name="AdjustmentOf">The row this one corrects, or null for an original row.</param>
/// <param name="Reason">The correction's reason, or null for an original row.</param>
/// <param name="CreatedAt">When the row was recorded; the keyset's first member.</param>
internal sealed record UsageLedgerRow(
    Guid Id,
    Guid? ProjectId,
    string Metric,
    decimal Quantity,
    string Unit,
    string Period,
    string BillingKey,
    DateTimeOffset OccurredAt,
    Guid? PriceRef,
    decimal? Amount,
    string? Currency,
    Guid? AdjustmentOf,
    string? Reason,
    DateTimeOffset CreatedAt);

/// <summary>One keyset page of the ledger, oldest first, with the next page's cursor when one exists.</summary>
/// <remarks>
/// A page is a walk of an append-only table, not a snapshot: rows written between two requests may
/// appear on a later page, and the page set is only as consistent as the instants it was read at.
/// A statement that must agree with itself — an invoice total — is the period reconciliation read,
/// which aggregates one period inside one transaction and is the authoritative figure.
/// </remarks>
/// <param name="Items">The page's rows, in <c>(created_at, id)</c> order.</param>
/// <param name="NextCursor">The continuation to hand back, or null on the last page.</param>
internal sealed record UsageLedgerPage(IReadOnlyList<UsageLedgerRow> Items, string? NextCursor);

/// <summary>
/// <c>GET /internal/operator/orgs/{o}/usage-ledger</c>: the audited, keyset-paged read of one org's
/// ledger rows — the statement behind an invoice, so the page is the recorded facts and nothing else.
/// <para>
/// The route sits on the operator ingress, which is where the credential gate lives instead of the
/// tenant pipeline: the caller is admitted by <c>IOperatorGate</c> before this service runs, the
/// org is the route's own parameter because an operator reads many tenants, and the audit row names
/// that operator — no tenant principal exists for that caller.
/// </para>
/// </summary>
/// <remarks>
/// Every read is audited: the row carries the actor, the org and the query parameters (period,
/// metric, page bound, whether a continuation was used). The audit row is written in the same
/// transaction as the read, so a read whose audit row cannot be written fails instead of returning
/// billing data nobody recorded, and a refused read (422, 400, 404) writes nothing at all.
/// <para>
/// The pages are not a snapshot: each request opens its own transaction, so a row a producer writes
/// between two calls is visible to the later page and a row an adjustment corrects is not rewritten
/// on the earlier one. The authoritative answer to "what does this period total" is the
/// reconciliation read, which aggregates the whole period in one transaction; a caller that needs a
/// consistent statement of an invoice takes that read, not a walk of this page.
/// </para>
/// </remarks>
internal sealed class UsageLedgerReader(
    ITenantScopeFactory scopes,
    ITenantDb db,
    IAuditTrail audit,
    ITenantLimits limits,
    UsageReadStore store)
{
    /// <summary>The page size of a request that names none.</summary>
    internal const int DefaultLimit = 50;

    /// <summary>The largest page a request may name.</summary>
    internal const int MaxLimit = 200;

    /// <summary>The audited action of one ledger read.</summary>
    internal const string Action = "usage.ledger_read";

    /// <summary>
    /// Validates the request, opens the org's scope, reads one page and writes the audit row of that
    /// read inside the same transaction.
    /// </summary>
    /// <param name="orgId">The org the route names.</param>
    /// <param name="operatorId">The authenticated operator, the audit row's actor.</param>
    /// <param name="period">The exact period filter, or null for every period.</param>
    /// <param name="metric">The exact wire metric filter, or null for every metric.</param>
    /// <param name="cursor">The continuation an earlier page answered, or null for the first page.</param>
    /// <param name="requestedLimit">The page size the caller asked for; clamped to 1..<see cref="MaxLimit"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<Result<UsageLedgerPage>> ExecuteAsync(
        Guid orgId,
        Guid operatorId,
        string? period,
        string? metric,
        string? cursor,
        int requestedLimit,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Usage, nameof(UsageLedgerReader));

        if (period is not null && !UsagePeriod.IsWellFormed(period))
        {
            return Result.Failure<UsageLedgerPage>(Error.Invalid(
                "the period is not a YYYY-MM billing month",
                [new ErrorDetail("period", "format")]));
        }

        if (metric is not null && !IsDeclaredMetric(metric))
        {
            return Result.Failure<UsageLedgerPage>(Error.Invalid(
                "the metric is not one the ledger stores",
                [new ErrorDetail("metric", "unknown")]));
        }

        DateTimeOffset? cursorCreatedAt = null;
        Guid? cursorId = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!KeysetCursor.TryDecode(cursor, out var decodedCreatedAt, out var decodedId))
            {
                return Result.Failure<UsageLedgerPage>(Error.BadRequest(
                    "the cursor is not a page continuation this endpoint issued",
                    [new ErrorDetail("cursor", "invalid")]));
            }

            cursorCreatedAt = decodedCreatedAt;
            cursorId = decodedId;
        }

        var bounded = Math.Clamp(requestedLimit, 1, MaxLimit);

        // The unit opens before the scope: a read that finds no organization returns without writing, and the
        // scope is disposed (rolled back) on that path, so neither half can outlive the other.
        using var unit = audit.BeginUnit();
        await using var scope = await scopes.BeginAsync(orgId, null, false, cancellationToken).ConfigureAwait(false);

        if (await limits.ForOrganizationAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<UsageLedgerPage>(Error.NotFound("no such organization"));
        }

        // One row more than the page answers "is there more?" without a count; the cursor names the last returned row.
        var rows = await store
            .ReadLedgerPageAsync(orgId, period, metric, cursorCreatedAt, cursorId, bounded + 1, cancellationToken)
            .ConfigureAwait(false);
        var hasMore = rows.Count > bounded;
        var items = rows.Take(bounded).Select(ToRow).ToArray();

        DescribeAudit(orgId, operatorId, period, metric, bounded, cursor, items.Length);
        await audit.FlushAsync(orgId, db, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(new UsageLedgerPage(
            items,
            hasMore ? KeysetCursor.Encode(items[^1].CreatedAt, items[^1].Id) : null));
    }

    /// <summary>True when <paramref name="metric"/> is one of the wire names the column's check admits.</summary>
    private static bool IsDeclaredMetric(string metric) =>
        UsageLedgerMetricNames.All.Contains(metric, StringComparer.Ordinal);

    /// <summary>Maps one stored row to the wire shape, without reinterpretation.</summary>
    private static UsageLedgerRow ToRow(UsageLedgerPageRow row) => new(
        row.Id,
        row.ProjectId,
        row.Metric,
        row.Quantity,
        row.Unit,
        row.Period,
        row.BillingKey,
        row.OccurredAt,
        row.PriceRef,
        row.Amount,
        row.Currency,
        row.AdjustmentOf,
        row.Reason,
        row.CreatedAt);

    /// <summary>
    /// Records what this read asked for and how much it returned. Amounts and quantities stay out:
    /// the audit chain names the query, never the billing figures it produced.
    /// </summary>
    private void DescribeAudit(
        Guid orgId,
        Guid operatorId,
        string? period,
        string? metric,
        int limit,
        string? cursor,
        int count)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            ["count"] = count.ToString(CultureInfo.InvariantCulture),
        };

        if (period is not null)
        {
            metadata["period"] = period;
        }

        if (metric is not null)
        {
            metadata["metric"] = metric;
        }

        // Whether a continuation was used, never the continuation itself: the cursor is an opaque
        // token-shaped value, and the audit metadata rule refuses anything shaped like a credential.
        if (!string.IsNullOrEmpty(cursor))
        {
            metadata["has_cursor"] = "true";
        }

        audit.Describe(new AuditObject("usage_ledger", orgId.ToString("D"), Metadata: metadata));
        audit.Record(Action, new AuditActor(AuditActorTypes.PlatformOperator, operatorId.ToString("D")));
    }
}
