using Evidgate.Modules.Usage.Contracts;

namespace Evidgate.Modules.Usage.Domain;

/// <summary>
/// One validated rate card ready for insert: the version every row of the load is tagged with, and the rows
/// themselves. A card is all-or-nothing — a violation refuses the whole card — so the rows never need a per-row flag
/// saying whether they are safe to write.
/// </summary>
/// <param name="RateCardVersion">The card version, 1..64 characters, unique per scope and metric in the database.</param>
/// <param name="Rows">The price rows, one per metric; never empty (a card with no prices is refused).</param>
internal sealed record UsagePriceCard(
    string RateCardVersion,
    IReadOnlyList<UsagePriceCardRow> Rows);

/// <summary>
/// One price row of a card, already parsed into the types the column stores: a <see cref="decimal"/> amount (never a
/// binary floating-point value), the instant range, and the optional quota.
/// </summary>
/// <param name="Metric">The metric the row prices.</param>
/// <param name="UnitPrice">The price of one unit, nonnegative, at most 12 fractional digits.</param>
/// <param name="Currency">The ISO-4217 code, three uppercase letters.</param>
/// <param name="EffectiveFrom">The first instant the row applies, inclusive.</param>
/// <param name="EffectiveTo">The first instant it no longer applies, or null for an open-ended range.</param>
/// <param name="IncludedQuota">Units included before the unit price applies, or null when none are.</param>
internal sealed record UsagePriceCardRow(
    UsageMetric Metric,
    decimal UnitPrice,
    string Currency,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo,
    decimal? IncludedQuota);

/// <summary>
/// What one card parse produced: the card, or every violation found. Both are never set — a card with any violation is
/// not returned at all, which is what keeps a partially valid card from being written.
/// </summary>
/// <param name="Card">The parsed card, or null when the body was refused.</param>
/// <param name="Violations">The refusals, in document order; empty when <paramref name="Card"/> is set.</param>
internal sealed record UsagePriceCardParseResult(
    UsagePriceCard? Card,
    IReadOnlyList<UsagePriceViolation> Violations)
{
    /// <summary>A refused parse.</summary>
    /// <param name="violation">The refusal to report.</param>
    internal static UsagePriceCardParseResult Refused(UsagePriceViolation violation) =>
        new(null, [violation]);

    /// <summary>A refused parse with several refusals.</summary>
    /// <param name="violations">The refusals to report; never empty.</param>
    internal static UsagePriceCardParseResult Refused(IReadOnlyList<UsagePriceViolation> violations) =>
        new(null, violations);
}
