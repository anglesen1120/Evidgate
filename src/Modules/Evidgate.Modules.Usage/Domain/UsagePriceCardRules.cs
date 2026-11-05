using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Evidgate.Modules.Usage.Contracts;

namespace Evidgate.Modules.Usage.Domain;

/// <summary>
/// The rules one rate card must satisfy before any row is written: the caps that keep one load bounded, the
/// money and currency shapes, and the two cross-row rules — no overlap and no repeated metric — that the database also
/// enforces. The database stays the arbiter; these rules exist so a refused card names the offending path instead of
/// surfacing a constraint violation, and so an in-card mistake is found before a statement runs.
/// </summary>
internal static partial class UsagePriceCardRules
{
    /// <summary>Largest card body one load accepts, checked before any parse: a limit is not a post-parse length check.</summary>
    internal const int MaxBodyBytes = 256 * 1024;

    /// <summary>Largest number of price rows one card may carry.</summary>
    internal const int MaxRows = 500;

    /// <summary>Longest <c>rate_card_version</c> the column accepts.</summary>
    internal const int MaxVersionLength = 64;

    /// <summary>Largest number of refusals one response carries before the walk stops.</summary>
    internal const int MaxViolations = 100;

    /// <summary>
    /// The amount shape: plain decimal digits with an optional fraction, never a sign, an exponent, a group separator or
    /// surrounding whitespace. At most 16 integer and 12 fraction digits — 28 significant digits, which decimal holds
    /// exactly — so a value that cannot be stored without rounding is refused rather than rounded.
    /// </summary>
    [GeneratedRegex(@"\A[0-9]{1,16}(\.[0-9]{1,12})?\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex AmountShape();

    /// <summary>The ISO-4217 shape the column's check mirrors.</summary>
    [GeneratedRegex(@"\A[A-Z]{3}\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex CurrencyShape();

    /// <summary>
    /// An ISO-8601 instant that names its offset, so the value is the same instant wherever the card is loaded. A
    /// timestamp without one would be read in the loading server's zone, which would shift a rate card by hours.
    /// </summary>
    [GeneratedRegex(
        @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})\z",
        RegexOptions.CultureInvariant,
        100)]
    private static partial Regex InstantShape();

    /// <summary>
    /// Parses one amount token — a JSON string or a JSON number's raw text — into the <see cref="decimal"/> the column
    /// stores. The shape is checked first so an exponent, a sign or a separator is a refusal rather than something
    /// <see cref="decimal.Parse(string, NumberStyles, IFormatProvider)"/> might accept; the parse itself never involves a
    /// binary floating-point type, so the value that arrives is the value that is stored.
    /// </summary>
    /// <param name="token">The raw token, without JSON quotes.</param>
    /// <param name="value">The parsed amount when the token is valid.</param>
    internal static bool TryParseAmount(string token, out decimal value)
    {
        value = 0m;

        if (!AmountShape().IsMatch(token)
            || !decimal.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        // The digit caps should already keep the token exact; comparing the digits decimal kept against the ones the
        // token spelled is the cross-check that proves it, so a rounded amount can never pass as a stored one.
        if (!DigitsMatch(token, parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>Whether two decimal texts spell the same significant digits, ignoring leading and trailing zeros.</summary>
    private static bool DigitsMatch(string token, decimal value) =>
        string.Equals(
            SignificantDigits(token),
            SignificantDigits(value.ToString(CultureInfo.InvariantCulture)),
            StringComparison.Ordinal);

    private static string SignificantDigits(string text)
    {
        var digits = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            if (character is >= '0' and <= '9')
            {
                digits.Append(character);
            }
        }

        var start = 0;
        var end = digits.Length;

        while (start < end && digits[start] == '0')
        {
            start++;
        }

        while (end > start && digits[end - 1] == '0')
        {
            end--;
        }

        return start == end ? "0" : digits.ToString(start, end - start);
    }

    /// <summary>
    /// Whether <paramref name="instant"/> is a whole number of microseconds, the finest precision <c>timestamptz</c>
    /// stores. A finer instant would be rounded on write, moving a window boundary away from the one the card named.
    /// </summary>
    /// <param name="instant">The parsed instant.</param>
    internal static bool IsMicrosecondAligned(DateTimeOffset instant) =>
        instant.UtcTicks % TimeSpan.TicksPerMicrosecond == 0;

    /// <summary>Whether <paramref name="value"/> is a currency code the column accepts.</summary>
    /// <param name="value">The candidate code.</param>
    internal static bool IsCurrency(string value) => CurrencyShape().IsMatch(value);

    /// <summary>Maps a wire metric name to its member, using the one list of names the ledger stores.</summary>
    /// <param name="value">The candidate wire name.</param>
    /// <param name="metric">The member when the name is one of the seven.</param>
    internal static bool TryParseMetric(string value, out UsageMetric metric)
    {
        for (var index = 0; index < UsageLedgerMetricNames.All.Count; index++)
        {
            if (string.Equals(UsageLedgerMetricNames.All[index], value, StringComparison.Ordinal))
            {
                metric = (UsageMetric)index;
                return true;
            }
        }

        metric = default;
        return false;
    }

    /// <summary>Parses an ISO-8601 instant that carries its offset, keeping that instant rather than a local guess.</summary>
    /// <param name="value">The raw string.</param>
    /// <param name="instant">The parsed instant when the string is one.</param>
    internal static bool TryParseInstant(string value, out DateTimeOffset instant)
    {
        if (!InstantShape().IsMatch(value))
        {
            instant = default;
            return false;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out instant);
    }

    /// <summary>
    /// Whether <paramref name="row"/>'s range ends before <paramref name="other"/>'s begins, or the other way round. The
    /// ranges are half-open, so two rows that only touch at one instant do not overlap.
    /// </summary>
    /// <param name="row">One row of the card.</param>
    /// <param name="other">Another row of the same metric.</param>
    internal static bool Overlaps(UsagePriceCardRow row, UsagePriceCardRow other) =>
        (other.EffectiveTo is null || row.EffectiveFrom < other.EffectiveTo)
        && (row.EffectiveTo is null || other.EffectiveFrom < row.EffectiveTo);

    /// <summary>
    /// The cross-row rules of one card, in document order: two ranges of one metric that overlap are
    /// <see cref="UsagePriceRefusalReasons.OverlappingRange"/>, and two rows of one metric that do not overlap are
    /// <see cref="UsagePriceRefusalReasons.DuplicateVersion"/> — the card version holds a metric once, which is the
    /// unique index's rule. Overlap is checked first so the more specific refusal wins for a pair that breaks both.
    /// </summary>
    /// <param name="rows">The rows the card declared.</param>
    internal static IReadOnlyList<UsagePriceViolation> ValidateRows(IReadOnlyList<UsagePriceCardRow> rows)
    {
        List<UsagePriceViolation>? violations = null;

        for (var index = 0; index < rows.Count; index++)
        {
            for (var other = 0; other < index; other++)
            {
                if (rows[other].Metric != rows[index].Metric)
                {
                    continue;
                }

                var reason = Overlaps(rows[other], rows[index])
                    ? UsagePriceRefusalReasons.OverlappingRange
                    : UsagePriceRefusalReasons.DuplicateVersion;

                (violations ??= []).Add(new UsagePriceViolation($"/prices/{index}/metric", reason));
                break;
            }
        }

        return violations ?? [];
    }
}
