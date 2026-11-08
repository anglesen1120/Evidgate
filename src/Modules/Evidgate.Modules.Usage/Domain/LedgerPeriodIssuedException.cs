namespace Evidgate.Modules.Usage.Domain;

/// <summary>
/// A ledger write named a period an issued billing statement already froze. The refusal is typed rather than a
/// bare database error because two paths reach it: the read taken before the insert, which leaves the caller's
/// transaction usable, and the trigger's check violation when a concurrent issue wins the race.
/// </summary>
/// <param name="period">The <c>YYYY-MM</c> period the statement froze; the only value the message carries.</param>
internal sealed class LedgerPeriodIssuedException(string period)
    : InvalidOperationException($"the billing period {period} is already issued")
{
    /// <summary>The issued <c>YYYY-MM</c> period the refused batch named.</summary>
    internal string Period { get; } = period;
}
