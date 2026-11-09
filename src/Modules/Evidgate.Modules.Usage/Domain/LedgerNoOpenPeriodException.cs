namespace Evidgate.Modules.Usage.Domain;

/// <summary>
/// A ledger write named a period whose month, and every month after it, is issued: the insert re-dates late usage
/// into the next open period, and there is none. The refusal is typed because the walk happens inside the insert
/// trigger — the caller's transaction is aborted, so the period it names is all this side can report.
/// </summary>
/// <param name="period">The <c>YYYY-MM</c> period the row was metered for, never a month it was moved to.</param>
internal sealed class LedgerNoOpenPeriodException(string period)
    : InvalidOperationException($"the billing period {period} and every period after it are already issued")
{
    /// <summary>The <c>YYYY-MM</c> period the refused row named.</summary>
    internal string Period { get; } = period;
}
