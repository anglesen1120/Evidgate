using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;

namespace Evidgate.Modules.Usage.Application.Quota;

/// <summary>
/// The bound a quota question must sit in before any statement runs: a strictly positive quantity of one billing
/// month from 2000-01 to 2999-12, or of the month the caller's own transaction is in. The gate refuses a question
/// outside it loudly, and the block stamp ignores one, so both read the bound here rather than each carrying a copy
/// of it.
/// </summary>
internal static class QuotaCheckRequestRules
{
    /// <summary>
    /// True when <paramref name="request"/> names an amount and a concrete billing month a counter can hold. A stamp
    /// writes a counter row, so it needs the month spelled out: it never resolves one from its own transaction.
    /// </summary>
    /// <param name="request">The question.</param>
    /// <returns>True when the question is inside the bound and names its month.</returns>
    internal static bool IsValid(QuotaCheckRequest request) =>
        request.Quantity > 0 && UsagePeriod.IsQuotaPeriod(request.Period);

    /// <summary>Refuses a question outside the bound, naming the member that is out of range.</summary>
    /// <param name="request">The question.</param>
    /// <exception cref="ArgumentOutOfRangeException">The quantity is not strictly positive, or the period is outside the bound.</exception>
    internal static void EnsureValid(QuotaCheckRequest request)
    {
        if (request.Quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Quantity,
                "a quota check reserves a strictly positive quantity");
        }

        if (request.Period is { } period && !UsagePeriod.IsQuotaPeriod(period))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Period,
                "a quota check reserves usage of one YYYY-MM billing month from 2000-01 to 2999-12, "
                + "or null for the month of the caller's transaction");
        }
    }
}
