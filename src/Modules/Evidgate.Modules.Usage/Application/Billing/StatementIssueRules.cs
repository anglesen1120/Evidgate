using System.Globalization;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Usage.Domain;

namespace Evidgate.Modules.Usage.Application.Billing;

/// <summary>
/// The body of <c>POST /internal/operator/billing/orgs/{o}/statements</c>: which closed period to invoice, and
/// the total and currency the operator believes it holds.
/// </summary>
/// <param name="Period">The period to issue, <c>YYYY-MM</c>.</param>
/// <param name="TotalAmount">The total the operator submits; it must equal the server's recompute exactly.</param>
/// <param name="Currency">The currency the operator submits; it must be the period's single currency.</param>
internal sealed record IssueStatementRequest(string? Period, decimal? TotalAmount, string? Currency);

/// <summary>
/// A worksheet that passed every rule: the frozen document a statement stores and the values it indexes.
/// </summary>
/// <param name="Period">The period, <c>YYYY-MM</c>.</param>
/// <param name="TotalAmount">The recomputed total the statement carries.</param>
/// <param name="Currency">The period's single currency.</param>
/// <param name="LineCount">How many usage rows the worksheet holds.</param>
/// <param name="ContentHash">The worksheet's canonical hash, the statement's identity for a re-issue.</param>
/// <param name="WorksheetJson">The canonical worksheet text, stored verbatim.</param>
internal sealed record StatementDraft(
    string Period,
    decimal TotalAmount,
    string Currency,
    int LineCount,
    string ContentHash,
    string WorksheetJson);

/// <summary>
/// The rules of issuing a statement, kept apart from the transaction that writes one so each refusal is a value
/// a test can name. The issuer applies them in one fixed order — the period's shape, whether it is closed, whether
/// it holds rows, whether every row is priced, whether they reconcile with the ledger's own totals, whether the
/// roll-up flagged a source gap, whether they share a currency, whether the ledger states a total, whether the
/// document fits what a statement stores, and only then whether the submitted total agrees — because a caller
/// should be told the first thing that is wrong with the period, not the last.
/// </summary>
internal static class StatementIssueRules
{
    /// <summary>The period has not ended, so its usage can still arrive.</summary>
    internal const string ReasonPeriodOpen = "period_open";

    /// <summary>The same refusal as a cost read names it: the month is still running, so its cost is not final.</summary>
    internal const string ReasonOpenPeriod = "open_period";

    /// <summary>The period holds no ledger row at all.</summary>
    internal const string ReasonNoRows = "no_rows";

    /// <summary>A row carries no price or no currency, so the total is not final.</summary>
    internal const string ReasonUnpricedRows = "unpriced_rows";

    /// <summary>The period's rows carry more than one currency.</summary>
    internal const string ReasonMixedCurrency = "mixed_currency";

    /// <summary>The rows are cut by the worksheet bound, do not agree with the ledger's aggregate, or omit usage.</summary>
    internal const string ReasonNotReconciled = "not_reconciled";

    /// <summary>The frozen document is larger than a statement row may store.</summary>
    internal const string ReasonWorksheetTooLarge = "worksheet_too_large";

    /// <summary>The submitted total or currency differs from the server's recompute.</summary>
    internal const string ReasonTotalMismatch = "total_mismatch";

    /// <summary>
    /// The most bytes of canonical worksheet text a statement may store. The column accepts 8 MiB of re-rendered
    /// jsonb text, which is longer than the document itself, so the app keeps a margin under it.
    /// </summary>
    internal const int MaxWorksheetBytes = 6 * 1024 * 1024;

    /// <summary>Validates the period's shape: required, then <c>YYYY-MM</c>.</summary>
    /// <param name="period">The submitted period, or null when the body omitted it.</param>
    internal static Result<string> ValidatePeriod(string? period) =>
        period is null
            ? Result.Failure<string>(Error.Invalid(
                "the period is required",
                [new ErrorDetail("period", "required")]))
            : !UsagePeriod.IsWellFormed(period)
                ? Result.Failure<string>(Error.Invalid(
                    "the period is not a YYYY-MM billing month",
                    [new ErrorDetail("period", "format")]))
                : Result.Success(period);

    /// <summary>
    /// True when <paramref name="period"/> ended before the month of <paramref name="now"/>. A period is billed
    /// once its month is over: usage that still arrives for the current month would otherwise be frozen out of
    /// the statement that claims to cover it. <c>YYYY-MM</c> compares ordinally, so the month is the whole rule.
    /// </summary>
    /// <param name="period">A period <see cref="ValidatePeriod"/> accepted.</param>
    /// <param name="now">The instant the issue was requested.</param>
    internal static bool IsClosed(string period, DateTimeOffset now) =>
        string.CompareOrdinal(period, Month(now)) < 0;

    /// <summary>The month of one instant in the period format, in UTC.</summary>
    /// <param name="now">The instant.</param>
    internal static string Month(DateTimeOffset now) =>
        now.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>The refusal of a period that has not ended.</summary>
    internal static Error PeriodOpen() => Refuse("period", ReasonPeriodOpen,
        "the period has not ended, so usage that still arrives would fall outside its statement");

    /// <summary>The refusal of a period that has not ended, as a read answers it: the month is still running.</summary>
    internal static Error OpenPeriod() => Refuse("period", ReasonOpenPeriod,
        "the period has not ended, so the cost of usage that still arrives would be reported as final");

    /// <summary>
    /// Applies the worksheet's rules to one request and returns the draft a statement would store. A period of
    /// corrections only is legal — its total may be negative or zero — because a correction is usage too.
    /// </summary>
    /// <param name="worksheet">The period's worksheet, read in the caller's scope.</param>
    /// <param name="request">The submitted total and currency.</param>
    internal static Result<StatementDraft> Evaluate(InvoiceWorksheet worksheet, IssueStatementRequest request)
    {
        ArgumentNullException.ThrowIfNull(worksheet);
        ArgumentNullException.ThrowIfNull(request);

        if (RefuseWorksheet(worksheet) is { } refused)
        {
            return Result.Failure<StatementDraft>(refused);
        }

        if (worksheet.Currency is not { } currency)
        {
            return Result.Failure<StatementDraft>(Refuse("period", ReasonMixedCurrency,
                "the period's rows do not carry one currency, so they cannot be summed"));
        }

        if (worksheet.Total is not { } total)
        {
            return Result.Failure<StatementDraft>(Refuse("period", ReasonNotReconciled,
                "the ledger states no total for the period, so it cannot be billed"));
        }

        // The document is rendered once here: it is both what the size rule measures and what the statement stores.
        var json = InvoiceWorksheetCanonical.ToJson(worksheet);
        if (Encoding.UTF8.GetByteCount(json) > MaxWorksheetBytes)
        {
            return Result.Failure<StatementDraft>(Refuse("period", ReasonWorksheetTooLarge,
                "the period's worksheet is larger than a statement can store, so it cannot be frozen as issued"));
        }

        // decimal equality compares the values, not their scales, so 10.50 and 10.5 are the same total.
        if (request.TotalAmount != total)
        {
            return Result.Failure<StatementDraft>(Refuse("total_amount", ReasonTotalMismatch,
                "the submitted total is not the number this period recomputes to"));
        }

        if (!string.Equals(request.Currency, currency, StringComparison.Ordinal))
        {
            return Result.Failure<StatementDraft>(Refuse("currency", ReasonTotalMismatch,
                "the submitted currency is not the currency this period bills in"));
        }

        return Result.Success(new StatementDraft(
            worksheet.Period,
            total,
            currency,
            worksheet.LineCount,
            worksheet.ContentHash,
            json));
    }

    /// <summary>
    /// The refusals a worksheet earns before its currency and its total are looked at, in the order a caller should
    /// hear them: nothing to invoice, a row no price marked, a period that is cut or does not reconcile, and a
    /// roll-up that flagged usage the ledger never received.
    /// </summary>
    /// <param name="worksheet">The period's worksheet.</param>
    private static Error? RefuseWorksheet(InvoiceWorksheet worksheet)
    {
        if (worksheet.Lines.Count == 0 && worksheet.Adjustments.Count == 0)
        {
            return Refuse("period", ReasonNoRows, "the period holds no ledger rows, so there is nothing to invoice");
        }

        if (worksheet.UnpricedLineCount > 0)
        {
            return Refuse("period", ReasonUnpricedRows,
                "the period holds usage that no price marked, so its total is not final");
        }

        if (worksheet.Truncated || !worksheet.Reconciled)
        {
            return Refuse("period", ReasonNotReconciled,
                "the period's rows do not agree with the ledger's own totals for it");
        }

        // A gap means the roll-up saw source usage the ledger holds no row for: the rows agree with the aggregate
        // and are still not the whole period, so this refusal must not be mistaken for a mixed-currency one.
        if (worksheet.SourceGapCount > 0)
        {
            return Refuse("period", ReasonNotReconciled,
                "the period's roll-up flagged source usage the ledger holds no row for, so its rows are incomplete");
        }

        return null;
    }

    /// <summary>
    /// One 422 refusal: the envelope's parsed-but-refused code, with the member to correct and the machine reason
    /// a client branches on.
    /// </summary>
    private static Error Refuse(string path, string reason, string message) =>
        Error.Unprocessable(ApiErrorCode.RequestInvalid, message, [new ErrorDetail(path, reason)]);
}
