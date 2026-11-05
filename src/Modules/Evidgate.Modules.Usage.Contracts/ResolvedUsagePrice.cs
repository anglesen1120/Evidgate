namespace Evidgate.Modules.Usage.Contracts;

/// <summary>
/// The price a metered row is billed at, read from the rate card that was in force at the moment the row is priced.
/// Every member comes from the <c>usage_price</c> row itself, so a caller that stores the result keeps an
/// audit trail back to the exact card version and range that produced the amount.
/// </summary>
/// <param name="PriceId">The <c>usage_price.id</c> the caller stores as <c>usage_ledger_entry.price_ref</c>.</param>
/// <param name="OrgId">The row's org, or null when it is a global rate card (the fallback every org resolves).</param>
/// <param name="Metric">The metric the price applies to.</param>
/// <param name="UnitPrice">
/// The price of one unit of <see cref="Metric"/>, in <see cref="Currency"/>. A PostgreSQL <c>numeric</c> read into
/// <see cref="decimal"/>: the money path never touches a binary floating-point type.
/// </param>
/// <param name="Currency">The ISO-4217 code the row prices in (three uppercase letters).</param>
/// <param name="EffectiveFrom">The first instant the row applies, inclusive.</param>
/// <param name="EffectiveTo">The first instant the row no longer applies, or null when the range is open-ended.</param>
/// <param name="IncludedQuota">Units included before <see cref="UnitPrice"/> applies, or null when none are.</param>
/// <param name="RateCardVersion">The card version the row was loaded under, so a bill can name its rate card.</param>
public sealed record ResolvedUsagePrice(
    Guid PriceId,
    Guid? OrgId,
    UsageMetric Metric,
    decimal UnitPrice,
    string Currency,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo,
    decimal? IncludedQuota,
    string RateCardVersion);
