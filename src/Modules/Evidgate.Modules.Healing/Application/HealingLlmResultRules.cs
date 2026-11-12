using Evidgate.Modules.Healing.Contracts;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// 08.5.1-T1: the bounds the ledger puts on a provider's own numbers, applied before the first row is written, so a
/// provider that reports nonsense cannot fail the INSERT (<c>tokens</c> CHECK, <c>numeric(12,6)</c> overflow) or write a
/// <c>status</c> the CHECK refuses.
/// </summary>
internal static class HealingLlmResultRules
{
    /// <summary>The largest cost <c>healing_llm_call.cost numeric(12,6)</c> can hold.</summary>
    internal const decimal MaxRecordableCost = 999_999.999999m;

    /// <summary>
    /// The result as it may be recorded: unchanged when its status is one of <see cref="HealingLlmStatuses"/> and its
    /// reported usage fits the column types, otherwise an <c>error</c> with no content and nothing spent.
    /// </summary>
    /// <param name="result">What the adapter reported for one attempt.</param>
    /// <returns>The result the gateway records and counts.</returns>
    internal static HealingLlmResult Recordable(HealingLlmResult result) =>
        HealingLlmStatuses.IsKnown(result.Status) && result.Tokens >= 0 && IsRecordableCost(result.Cost)
            ? result
            : new HealingLlmResult(HealingLlmStatuses.Error, null, 0, 0m);

    // Postgres rounds to the column's scale (6) before range-checking, so a cost just under one million can still
    // overflow numeric(12,6); the rounding is repeated here with the same half-away-from-zero rule.
    private static bool IsRecordableCost(decimal cost) =>
        cost >= 0m && decimal.Round(cost, 6, MidpointRounding.AwayFromZero) <= MaxRecordableCost;
}
