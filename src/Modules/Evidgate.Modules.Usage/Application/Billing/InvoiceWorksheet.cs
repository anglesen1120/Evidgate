using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Tenancy.Contracts;
using Evidgate.Modules.Usage.Application.Queries;
using Evidgate.Modules.Usage.Domain;
using Evidgate.Modules.Usage.Infrastructure.Persistence;

namespace Evidgate.Modules.Usage.Application.Billing;

/// <summary>
/// One ledger row as the worksheet renders it: the priced facts of one unit of usage, or of the correction
/// that adjusts another row. <see cref="AdjustmentOf"/> and <see cref="Reason"/> are set on a correction only.
/// </summary>
/// <param name="Id">The ledger row's id.</param>
/// <param name="ProjectId">The project, or null for an org-level metric.</param>
/// <param name="Metric">The wire metric name.</param>
/// <param name="Quantity">The quantity the row records; negative on a correction.</param>
/// <param name="Unit">The unit the quantity is expressed in.</param>
/// <param name="BillingKey">The producer's deduplication key.</param>
/// <param name="OccurredAt">When the usage happened.</param>
/// <param name="PriceRef">The rate-card row the amount came from, or null while unpriced.</param>
/// <param name="Amount">The priced amount, or null while the row holds no price.</param>
/// <param name="Currency">The amount's currency, or null while unpriced.</param>
/// <param name="AdjustmentOf">The row this one corrects, or null for usage of the period itself.</param>
/// <param name="Reason">Why the correction was recorded, or null on a usage row.</param>
internal sealed record WorksheetLine(
    Guid Id,
    Guid? ProjectId,
    string Metric,
    decimal Quantity,
    string Unit,
    string BillingKey,
    DateTimeOffset OccurredAt,
    Guid? PriceRef,
    decimal? Amount,
    string? Currency,
    Guid? AdjustmentOf,
    string? Reason);

/// <summary>
/// The statement one period was issued under, as the worksheet read reports it beside the live figures: what was
/// frozen, never what the period sums to now.
/// </summary>
/// <param name="StatementId">The statement row's id.</param>
/// <param name="ContentHash">The worksheet's canonical hash as issued.</param>
/// <param name="TotalAmount">The total the statement froze.</param>
/// <param name="Currency">The currency that total is expressed in.</param>
/// <param name="IssuedAt">When the statement was issued.</param>
internal sealed record IssuedWorksheetStatement(
    Guid StatementId,
    string ContentHash,
    decimal TotalAmount,
    string Currency,
    DateTimeOffset IssuedAt);

/// <summary>
/// The invoice worksheet of one closed period: every ledger row that bills, the corrections beside it, and the
/// period's own totals as the ledger aggregates them.
/// </summary>
/// <remarks>
/// A total is null where it cannot be stated: <see cref="LineTotal"/> and <see cref="AdjustmentTotal"/> stay null
/// while any row of their list is unpriced, and <see cref="Total"/> stays null while any reconciled scope is
/// unpriced or the period's rows carry more than one currency. <see cref="Reconciled"/> says whether the rows
/// agree with the ledger side the reconciliation reports; <see cref="SourceGapCount"/> says how many roll-up rows
/// flagged usage the ledger holds no row for, so the period is known to understate what it bills;
/// <see cref="Truncated"/> says the row bound cut the lists, so a truncated worksheet is never issuable.
/// <para>
/// A source gap is not part of <see cref="InvoiceWorksheetCanonical"/>'s document: no statement is issued while
/// one exists, so no frozen invoice can ever carry one.
/// </para>
/// </remarks>
/// <param name="Period">The period, <c>YYYY-MM</c>.</param>
/// <param name="Currency">The period's single currency, or null when it holds no rows or more than one.</param>
/// <param name="Lines">The period's usage rows, ordered by metric, project, occurrence and id.</param>
/// <param name="Adjustments">The period's corrections, in the same order, each naming the row it corrects.</param>
/// <param name="LineTotal">The corrections-excluded priced sum, or null while a line is unpriced.</param>
/// <param name="AdjustmentTotal">The corrections' priced sum, or null while a correction is unpriced.</param>
/// <param name="Total">The authoritative total from the ledger's per-scope sums, or null while one is unknown.</param>
/// <param name="LineCount">How many usage rows <see cref="Lines"/> holds.</param>
/// <param name="UnpricedLineCount">How many rows of both lists carry no price or no currency.</param>
/// <param name="SourceGapCount">How many roll-up rows flagged source usage the ledger holds no row for.</param>
/// <param name="Truncated">True when the period holds more rows than the worksheet can carry.</param>
/// <param name="Reconciled">True when the rows agree on count and, where known, on amount and currency.</param>
/// <param name="ContentHash">SHA-256 (lowercase hex) of <see cref="InvoiceWorksheetCanonical"/>'s text.</param>
/// <param name="Issued">The period's issued statement, or null while the period was never issued.</param>
/// <param name="DriftedSinceIssue">
/// True when the period was issued and this read no longer hashes to what was issued, so a row or a correction
/// reached the period after the statement froze it. Always false without an issued statement.
/// </param>
internal sealed record InvoiceWorksheet(
    string Period,
    string? Currency,
    IReadOnlyList<WorksheetLine> Lines,
    IReadOnlyList<WorksheetLine> Adjustments,
    decimal? LineTotal,
    decimal? AdjustmentTotal,
    decimal? Total,
    int LineCount,
    int UnpricedLineCount,
    int SourceGapCount,
    bool Truncated,
    bool Reconciled,
    string ContentHash,
    IssuedWorksheetStatement? Issued = null,
    bool DriftedSinceIssue = false);

/// <summary>
/// What a period would invoice, read on demand: <c>GET /internal/operator/orgs/{o}/invoice-worksheet?period=</c>.
/// <para>
/// The rows and their totals come from one read inside the org's scope, and the total is never re-derived from
/// the rows: it is the ledger side of <see cref="PeriodReconciliationReader"/>'s units, so the worksheet and the
/// reconciliation can only ever disagree by reporting it. The read is audited like the reconciliation beside it —
/// one row per read, in the read's own transaction, naming the period and how many rows it described, never an
/// amount.
/// </para>
/// </summary>
/// <remarks>
/// A worksheet is a snapshot of what the period holds at the instant it is read; issuing a statement freezes it,
/// which is why this read never claims a period is issuable. It reports what the issuer's rules then judge.
/// The bound is <see cref="MaxLines"/> rows plus one, so a period past it is reported as truncated instead of
/// being passed off as complete.
/// <para>
/// A period that was already issued is answered with the statement it froze beside the live figures, so a row or
/// a correction that reached the period after the issue is reported as a drift instead of silently moving the
/// total the org was billed. The comparison is made against the canonical hash the statement stored; neither
/// the issued values nor the flag are part of that document.
/// </para>
/// </remarks>
internal sealed class InvoiceWorksheetReader(
    ITenantScopeFactory scopes,
    ITenantDb db,
    IAuditTrail audit,
    ITenantLimits limits,
    UsageReadStore store,
    PeriodReconciliationReader reconciliation,
    BillingStatementStore statements)
{
    /// <summary>Most ledger rows one worksheet carries; a period past it is truncated and unissuable.</summary>
    internal const int MaxLines = 10_000;

    /// <summary>The audited action of one worksheet read.</summary>
    internal const string Action = "usage.invoice_worksheet_read";

    /// <summary>The object type the read's audit row names.</summary>
    internal const string ObjectType = "usage_ledger_entry";

    /// <summary>
    /// Validates the period, opens the org's scope, assembles the worksheet and writes the audit row of that read
    /// inside the same transaction.
    /// </summary>
    /// <param name="orgId">The org the route names.</param>
    /// <param name="operatorId">The authenticated operator, the audit row's actor.</param>
    /// <param name="period">The worksheet's period, <c>YYYY-MM</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<Result<InvoiceWorksheet>> ExecuteAsync(
        Guid orgId,
        Guid operatorId,
        string? period,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(
            ModuleName.Usage,
            nameof(InvoiceWorksheetReader));

        var validated = Validate(period);
        if (validated.Error is { } invalid)
        {
            return Result.Failure<InvoiceWorksheet>(invalid);
        }

        using var unit = audit.BeginUnit();
        await using var scope = await scopes.BeginAsync(orgId, null, false, cancellationToken).ConfigureAwait(false);

        if (await limits.ForOrganizationAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<InvoiceWorksheet>(Error.NotFound("no such organization"));
        }

        var worksheet = await BuildAsync(orgId, validated.Value, cancellationToken).ConfigureAwait(false);
        worksheet = await DescribeIssueAsync(orgId, worksheet, cancellationToken).ConfigureAwait(false);

        DescribeAudit(orgId, operatorId, worksheet);
        await audit.FlushAsync(orgId, db, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(worksheet);
    }

    /// <summary>
    /// Assembles the worksheet of one period inside the caller's already-open tenant scope. It writes nothing and
    /// audits nothing, so the read route and the issuer that freezes a worksheet build it through one code path.
    /// The issued statement is attached by the read and never here: the issuer's own build pays no second read, and
    /// the document it freezes carries the live figures alone.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="period">The period, already validated as <c>YYYY-MM</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<InvoiceWorksheet> BuildAsync(Guid orgId, string period, CancellationToken cancellationToken)
    {
        // One row more than the worksheet holds answers "was the list cut?" without a count.
        var read = await store
            .ReadWorksheetLinesAsync(orgId, period, MaxLines + 1, cancellationToken)
            .ConfigureAwait(false);
        var truncated = read.Count > MaxLines;
        IReadOnlyList<WorksheetLineRow> rows = truncated ? [.. read.Take(MaxLines)] : read;

        var lines = rows.Where(row => row.AdjustmentOf is null).Select(ToLine).ToArray();
        var adjustments = rows.Where(row => row.AdjustmentOf is not null).Select(ToLine).ToArray();

        var compared = await reconciliation
            .ReadUnitsAsync(orgId, period, cancellationToken)
            .ConfigureAwait(false);
        var ledgerSide = WorksheetTotals.FromUnits(compared);
        var rowSide = WorksheetTotals.FromRows(lines, adjustments);

        var draft = new InvoiceWorksheet(
            period,
            ledgerSide.Currency,
            lines,
            adjustments,
            rowSide.LineTotal,
            rowSide.AdjustmentTotal,
            ledgerSide.Total,
            lines.Length,
            rowSide.UnpricedCount,
            compared.Gaps.Count,
            truncated,
            WorksheetTotals.IsReconciled(ledgerSide, rowSide, compared.Truncated),
            ContentHash: string.Empty);

        // The hash covers the canonical text and nothing else, so it is computed once the text is final.
        return draft with { ContentHash = InvoiceWorksheetCanonical.Hash(draft) };
    }

    /// <summary>
    /// Answers what the period was issued as, when it was: one indexed read of the statement the period already
    /// froze, taken inside the caller's scope and only after the live worksheet was built, so an unissued period
    /// pays nothing for it. The issued values are reported beside the live ones and never replace them — a row
    /// that reached the period late cannot move what the org was billed, it only says the two disagree.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="worksheet">The worksheet as the period reads now.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async Task<InvoiceWorksheet> DescribeIssueAsync(
        Guid orgId,
        InvoiceWorksheet worksheet,
        CancellationToken cancellationToken)
    {
        var issued = await statements
            .FindAsync(orgId, worksheet.Period, BillingStatementStore.InvoiceKind, cancellationToken)
            .ConfigureAwait(false);
        if (issued is null)
        {
            return worksheet;
        }

        return worksheet with
        {
            Issued = new IssuedWorksheetStatement(
                issued.Id,
                issued.ContentHash,
                issued.TotalAmount,
                issued.Currency,
                issued.IssuedAt),
            DriftedSinceIssue = !string.Equals(issued.ContentHash, worksheet.ContentHash, StringComparison.Ordinal),
        };
    }

    /// <summary>Validates the period, or names the member that fails the request.</summary>
    private static Result<string> Validate(string? period) =>
        period is null
            ? Result.Failure<string>(Error.Invalid(
                "the period is required",
                [new ErrorDetail("period", "required")]))
            : !UsagePeriod.IsWellFormed(period)
                ? Result.Failure<string>(Error.Invalid(
                    "the period is not a YYYY-MM billing month",
                    [new ErrorDetail("period", "format")]))
                : Result.Success(period);

    private static WorksheetLine ToLine(WorksheetLineRow row) => new(
        row.Id,
        row.ProjectId,
        row.Metric,
        row.Quantity,
        row.Unit,
        row.BillingKey,
        row.OccurredAt,
        row.PriceRef,
        row.Amount,
        row.Currency,
        row.AdjustmentOf,
        row.Reason);

    /// <summary>
    /// Records which period was read, how many rows it described, which bound shaped the answer and whether the
    /// period has moved since it was issued. No amount reaches the audit chain: this row says what was read, not
    /// what it billed.
    /// </summary>
    private void DescribeAudit(Guid orgId, Guid operatorId, InvoiceWorksheet worksheet)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["period"] = worksheet.Period,
            ["count"] = (worksheet.LineCount + worksheet.Adjustments.Count)
                .ToString(CultureInfo.InvariantCulture),
            ["outcome"] = Outcome(worksheet),
        };

        audit.Describe(new AuditObject(ObjectType, orgId.ToString("D"), Metadata: metadata));
        audit.Record(Action, new AuditActor(AuditActorTypes.PlatformOperator, operatorId.ToString("D")));
    }

    /// <summary>
    /// The read's own verdict, in one word, still amount-free: a period that no longer matches the statement it
    /// was issued under is reported as drifted ahead of how complete the worksheet is, because it is the stronger
    /// statement about the period — the org was billed something this read no longer describes.
    /// </summary>
    private static string Outcome(InvoiceWorksheet worksheet) =>
        worksheet.DriftedSinceIssue
            ? "drifted"
            : worksheet.Truncated
                ? "truncated"
                : worksheet.SourceGapCount > 0
                    ? "source_gaps"
                    : !worksheet.Reconciled
                        ? "unreconciled"
                        : worksheet.UnpricedLineCount > 0
                            ? "unpriced"
                            : "complete";
}
