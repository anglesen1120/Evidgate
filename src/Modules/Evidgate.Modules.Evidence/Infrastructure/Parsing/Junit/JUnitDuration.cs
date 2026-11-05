using System.Globalization;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;

/// <summary>
/// Turns a report's <c>time</c> attribute (seconds, as JUnit producers write it) into the stored whole
/// milliseconds. A missing, negative, non-numeric or unrepresentable value is <see langword="null"/>: a duration
/// is evidence about the run, so a value we cannot trust is absent rather than a zero that would read as
/// "instant". Nothing here throws, because the attribute comes straight from the untrusted document.
/// </summary>
internal static class JUnitDuration
{
    private const double MillisecondsPerSecond = 1000d;

    /// <summary>Reads one <c>time</c> attribute.</summary>
    /// <param name="seconds">The attribute text, or <see langword="null"/> when absent.</param>
    /// <returns>Whole milliseconds, or <see langword="null"/> when the value cannot be represented honestly.</returns>
    public static long? ToMilliseconds(string? seconds)
    {
        if (string.IsNullOrWhiteSpace(seconds)
            || !double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value)
            || value < 0)
        {
            return null;
        }

        var milliseconds = value * MillisecondsPerSecond;
        if (!double.IsFinite(milliseconds) || milliseconds >= long.MaxValue)
        {
            return null;
        }

        // Half away from zero, so the same bytes always produce the same integer on every platform.
        return (long)Math.Round(milliseconds, MidpointRounding.AwayFromZero);
    }
}
