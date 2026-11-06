using System.Globalization;

namespace Evidgate.Modules.Usage.Domain;

/// <summary>
/// The <c>period</c> rule of the ledger: the column is a calendar month written <c>YYYY-MM</c>, and the
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

    /// <summary>
    /// True when <paramref name="period"/> is a well-formed month from 2000-01 to 2999-12, the range a
    /// <c>quota_counter</c> row accepts. The counter's retry hint is built from a <see cref="DateTimeOffset"/>,
    /// so a year outside that range would turn a refusal into an exception; four digits starting with
    /// <c>2</c> are exactly 2000..2999.
    /// </summary>
    /// <param name="period">The candidate period.</param>
    internal static bool IsQuotaPeriod(string? period) =>
        period is { } text && IsWellFormed(text) && text[0] == '2';

    /// <summary>
    /// The wait a blocked caller is told: the next month starts the fresh counter that makes the same call fit again.
    /// At least one second, so a caller never reads <c>Retry-After: 0</c> as "retry now".
    /// </summary>
    /// <param name="period">A period <see cref="IsQuotaPeriod"/> accepts; both the parse and the next month stay
    /// inside <see cref="DateTimeOffset"/> for it, so this never throws.</param>
    /// <param name="now">The caller's clock instant, so the answer is the same everywhere the same instant is read.</param>
    internal static TimeSpan RetryUntilNextPeriod(string period, DateTimeOffset now)
    {
        var year = int.Parse(period.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture);
        var month = int.Parse(period.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        var next = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        var until = next - now;

        return until < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : until;
    }
}
