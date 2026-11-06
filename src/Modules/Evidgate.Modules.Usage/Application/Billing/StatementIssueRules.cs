using System.Globalization;
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
/// it holds rows, whether every row is priced, whether they share a currency, whether they reconcile, and only
/// then whether the submitted total agrees — because a caller should be told the first thing that is wrong with
/// the period, not the last.
/// </summary>
internal static class StatementIssueRules
{
    /// <summary>The period has not ended, so its usage can still arrive.</summary>
    internal const string ReasonPeriodOpen = "period_open";

    /// <summary>The period holds no ledger row at all.</summary>
    internal const string ReasonNoRows = "no_rows";

    /// <summary>A row carries no price or no currency, so the total is not final.</summary>
    internal const string ReasonUnpricedRows = "unpriced_rows";

    /// <summary>The period's rows carry more than one currency.</summary>
    internal const string ReasonMixedCurrency = "mixed_currency";

    /// <summary>The rows are cut by the worksheet bound or do not agree with the ledger's aggregate.</summary>
    internal const string ReasonNotReconciled = "not_reconciled";

    /// <summary>The submitted total or currency differs from the server's recompute.</summary>
    internal const string ReasonTotalMismatch = "total_mismatch";

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

        if (worksheet.Lines.Count == 0 && worksheet.Adjustments.Count == 0)
        {
            return Result.Failure<StatementDraft>(Refuse("period", ReasonNoRows,
                "the period holds no ledger rows, so there is nothing to invoice"));
        }

        if (worksheet.UnpricedLineCount > 0)
        {
            return Result.Failure<StatementDraft>(Refuse("period", ReasonUnpricedRows,
                "the period holds usage that no price marked, so its total is not final"));
        }

        if (worksheet.Currency is null)
        {
            return Result.Failure<StatementDraft>(Refuse("period", ReasonMixedCurrency,
                "the period's rows do not carry one currency, so they cannot be summed"));
        }

        if (worksheet.Truncated || !worksheet.Reconciled || worksheet.Total is null)
        {
            return Result.Failure<StatementDraft>(Refuse("period", ReasonNotReconciled,
                "the period's rows do not agree with the ledger's own totals for it"));
        }

        // decimal equality compares the values, not their scales, so 10.50 and 10.5 are the same total.
        if (request.TotalAmount != worksheet.Total)
        {
            return Result.Failure<StatementDraft>(Refuse("total_amount", ReasonTotalMismatch,
                "the submitted total is not the number this period recomputes to"));
        }

        if (!string.Equals(request.Currency, worksheet.Currency, StringComparison.Ordinal))
        {
            return Result.Failure<StatementDraft>(Refuse("currency", ReasonTotalMismatch,
                "the submitted currency is not the currency this period bills in"));
        }

        return Result.Success(new StatementDraft(
            worksheet.Period,
            worksheet.Total.Value,
            worksheet.Currency,
            worksheet.LineCount,
            worksheet.ContentHash,
            InvoiceWorksheetCanonical.ToJson(worksheet)));
    }

    /// <summary>
    /// One 422 refusal: the envelope's parsed-but-refused code, with the member to correct and the machine reason
    /// a client branches on.
    /// </summary>
    private static Error Refuse(string path, string reason, string message) =>
        Error.Unprocessable(ApiErrorCode.RequestInvalid, message, [new ErrorDetail(path, reason)]);
}
