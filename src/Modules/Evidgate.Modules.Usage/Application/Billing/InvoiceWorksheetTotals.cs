using Evidgate.Modules.Usage.Application.Queries;

namespace Evidgate.Modules.Usage.Application.Billing;

/// <summary>
/// The ledger side of a period as <see cref="PeriodReconciliationReader"/>'s units report it: the authoritative
/// total, the single currency it is stated in, and how many rows it covers.
/// </summary>
/// <param name="Total">The sum of the scopes' priced amounts, or null while one scope is unpriced or mixed.</param>
/// <param name="Currency">The one currency every scope carries, or null when there is none or more than one.</param>
/// <param name="RowCount">How many ledger rows of the period the scopes cover.</param>
/// <param name="Truncated">True when the scope bound cut the units, so the total is partial.</param>
internal sealed record WorksheetLedgerSide(decimal? Total, string? Currency, long RowCount, bool Truncated);

/// <summary>The worksheet's own rows, summed: what the lists say beside what the ledger aggregates.</summary>
/// <param name="LineTotal">The usage rows' priced sum, or null while one of them is unpriced.</param>
/// <param name="AdjustmentTotal">The corrections' priced sum, or null while one of them is unpriced.</param>
/// <param name="UnpricedCount">How many rows of both lists carry no price or no currency.</param>
/// <param name="Currencies">The distinct currencies the priced rows carry, ordinal order.</param>
/// <param name="LineCount">How many usage rows the worksheet holds.</param>
/// <param name="AdjustmentCount">How many corrections it holds.</param>
internal sealed record WorksheetRowSide(
    decimal? LineTotal,
    decimal? AdjustmentTotal,
    int UnpricedCount,
    IReadOnlyList<string> Currencies,
    int LineCount,
    int AdjustmentCount);

/// <summary>
/// The arithmetic of a worksheet: the ledger side read from the reconciliation's units, the row side summed from
/// the lists, and the test that the two describe the same period.
/// </summary>
/// <remarks>
/// The reconciliation units are the aggregate of record, so they are where a total comes from. The rows are
/// summed only to cross-check that aggregate — a second implementation would be a second truth the moment the
/// two disagreed, which is exactly what <see cref="IsReconciled"/> refuses to hide.
/// </remarks>
internal static class WorksheetTotals
{
    /// <summary>The ledger side of the units: one scope per <c>(project, metric)</c>, roll-up-only scopes skipped.</summary>
    /// <param name="units">The period's reconciled scopes.</param>
    internal static WorksheetLedgerSide FromUnits(ReconciliationUnits units)
    {
        ArgumentNullException.ThrowIfNull(units);

        var total = 0m;
        long rowCount = 0;
        var priced = true;
        var currencies = new HashSet<string>(StringComparer.Ordinal);

        foreach (var unit in units.Units)
        {
            // A scope with no ledger row exists only in the roll-up; it bills nothing of this period.
            if (unit.LedgerRowCount == 0)
            {
                continue;
            }

            rowCount += unit.LedgerRowCount;

            if (unit.LedgerTotalAmount is { } amount && unit.LedgerCurrency is { } currency)
            {
                total += amount;
                currencies.Add(currency);

                continue;
            }

            // The unit is unpriced or carries several currencies; either way its amount is not a number.
            priced = false;
        }

        var stated = priced && currencies.Count == 1;
        if (rowCount == 0)
        {
            return new WorksheetLedgerSide(0m, null, 0, units.Truncated);
        }

        return new WorksheetLedgerSide(
            stated ? total : null,
            stated ? currencies.Min(StringComparer.Ordinal) : null,
            rowCount,
            units.Truncated);
    }

    /// <summary>Sums the worksheet's rows, reporting every row that could not be part of a sum.</summary>
    /// <param name="lines">The period's usage rows.</param>
    /// <param name="adjustments">The period's corrections.</param>
    internal static WorksheetRowSide FromRows(
        IReadOnlyList<WorksheetLine> lines,
        IReadOnlyList<WorksheetLine> adjustments)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(adjustments);

        var lineTotal = Sum(lines, out var lineUnpriced);
        var adjustmentTotal = Sum(adjustments, out var adjustmentUnpriced);
        var currencies = lines
            .Concat(adjustments)
            .Select(row => row.Currency)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new WorksheetRowSide(
            lineTotal,
            adjustmentTotal,
            lineUnpriced + adjustmentUnpriced,
            currencies,
            lines.Count,
            adjustments.Count);
    }

    /// <summary>
    /// True when the rows and the ledger side describe the same period: the same row count, and — where both
    /// sides state one — the same amount and the same currency. Two unknown amounts cannot disagree, so a period
    /// whose rows are unpriced reconciles and is refused by the issuer for its pricing instead.
    /// </summary>
    /// <param name="ledger">The ledger side.</param>
    /// <param name="rows">The row side.</param>
    /// <param name="unitsTruncated">True when the scope bound cut the ledger side.</param>
    internal static bool IsReconciled(WorksheetLedgerSide ledger, WorksheetRowSide rows, bool unitsTruncated)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(rows);

        if (unitsTruncated || ledger.RowCount != rows.LineCount + rows.AdjustmentCount)
        {
            return false;
        }

        if (ledger.Total is { } total
            && (rows.LineTotal is not { } lines
                || rows.AdjustmentTotal is not { } adjustments
                || lines + adjustments != total))
        {
            return false;
        }

        return ledger.Currency is not { } currency
            || (rows.Currencies.Count == 1 && string.Equals(rows.Currencies[0], currency, StringComparison.Ordinal));
    }

    /// <summary>
    /// True when the ledger side still describes exactly the worksheet's rows: the same row count, and — where
    /// both state one — the same total and currency. A row that arrived after the worksheet was assembled moves
    /// one of the three, and a cut aggregate can hide it, so a truncated re-read never counts as unchanged.
    /// </summary>
    /// <param name="ledger">The ledger side, re-read.</param>
    /// <param name="worksheet">The worksheet whose rows are being frozen.</param>
    internal static bool CoversSameRows(WorksheetLedgerSide ledger, InvoiceWorksheet worksheet)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(worksheet);

        return !ledger.Truncated
            && ledger.RowCount == worksheet.LineCount + worksheet.Adjustments.Count
            && ledger.Total == worksheet.Total
            && string.Equals(ledger.Currency, worksheet.Currency, StringComparison.Ordinal);
    }

    /// <summary>The priced sum of one list, or null when any row of it carries no price or no currency.</summary>
    private static decimal? Sum(IReadOnlyList<WorksheetLine> rows, out int unpriced)
    {
        var total = 0m;
        unpriced = 0;

        foreach (var row in rows)
        {
            if (row.Amount is { } amount && row.Currency is not null)
            {
                total += amount;

                continue;
            }

            unpriced++;
        }

        return unpriced == 0 ? total : null;
    }
}
