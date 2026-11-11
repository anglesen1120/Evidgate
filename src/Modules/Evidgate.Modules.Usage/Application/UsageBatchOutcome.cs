using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;

namespace Evidgate.Modules.Usage.Application;

/// <summary>
/// Builds the <see cref="UsageBatchResult"/> of a recorded batch (12.3.1 §5): the rows the statement inserted, and the
/// requested keys it did not — either already on file (a resent metering event, ruling T43) or repeated inside the
/// request, where the first occurrence is the one recorded. Both lists follow request order and carry no key twice, so a
/// caller can map them back onto its own entries.
/// </summary>
internal static class UsageBatchOutcome
{
    /// <summary>Merges the prepared batch with the billing keys the one statement returned.</summary>
    /// <param name="batch">The prepared batch; its rows are the distinct request keys, in request order.</param>
    /// <param name="inserted">The billing keys the insert returned.</param>
    internal static UsageBatchResult Merge(UsageBatch batch, IReadOnlyList<string> inserted)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(inserted);

        var insertedKeys = inserted.ToHashSet(StringComparer.Ordinal);

        List<string>? kept = null;

        foreach (var row in batch.Rows)
        {
            if (insertedKeys.Contains(row.BillingKey))
            {
                (kept ??= []).Add(row.BillingKey);
            }
        }

        // The duplicate list comes from the batch's own classification so the recorded attempts and this answer can
        // never name different keys. A batch with conflicts never reaches this merge: it wrote nothing.
        return new UsageBatchResult(kept ?? [], batch.DuplicateBillingKeys(insertedKeys), []);
    }
}
