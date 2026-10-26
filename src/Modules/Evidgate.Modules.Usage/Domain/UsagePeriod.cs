namespace Evidgate.Modules.Usage.Domain;

/// <summary>
/// The <c>period</c> rule of the ledger (12.3.1 §4): the column is a calendar month written <c>YYYY-MM</c>, and the
/// roll-up and every summary filter group by it, so nothing else (a free-text month, a full date, a time zone) is
/// accepted. The check is ordinal and allocation-free: four ASCII digits, a hyphen, a month in <c>01</c>..<c>12</c>.
/// </summary>
internal static class UsagePeriod
{
    /// <summary>True when <paramref name="period"/> is exactly <c>YYYY-MM</c> with a month in <c>01</c>..<c>12</c>.</summary>
    /// <param name="period">The candidate period.</param>
    internal static bool IsWellFormed(string? period)
    {
        if (period is not { Length: 7 })
        {
            return false;
        }

        var month = (period[5] - '0') * 10 + (period[6] - '0');

        return char.IsAsciiDigit(period[0])
            && char.IsAsciiDigit(period[1])
            && char.IsAsciiDigit(period[2])
            && char.IsAsciiDigit(period[3])
            && period[4] == '-'
            && char.IsAsciiDigit(period[5])
            && char.IsAsciiDigit(period[6])
            && month is >= 1 and <= 12;
    }
}
