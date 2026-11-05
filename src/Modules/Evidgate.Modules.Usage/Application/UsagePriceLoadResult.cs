using Evidgate.Modules.Usage.Domain;

namespace Evidgate.Modules.Usage.Application;

/// <summary>
/// What one rate-card load did: the ids it wrote, or the refusals that stopped it. A refused load wrote
/// nothing — the rows of one card are one unit of work — so the two lists are never both populated.
/// </summary>
/// <param name="PriceIds">The ids of the inserted rows, in card order; empty when the load was refused.</param>
/// <param name="Violations">The refusals; empty when the load succeeded.</param>
internal sealed record UsagePriceLoadResult(
    IReadOnlyList<Guid> PriceIds,
    IReadOnlyList<UsagePriceViolation> Violations)
{
    /// <summary>True when the card was written.</summary>
    internal bool Succeeded => Violations.Count == 0;

    /// <summary>A successful load.</summary>
    /// <param name="priceIds">The inserted ids, in card order.</param>
    internal static UsagePriceLoadResult Loaded(IReadOnlyList<Guid> priceIds) => new(priceIds, []);

    /// <summary>A refused load, which wrote nothing.</summary>
    /// <param name="violations">The refusals; never empty.</param>
    internal static UsagePriceLoadResult Refused(IReadOnlyList<UsagePriceViolation> violations) => new([], violations);
}
