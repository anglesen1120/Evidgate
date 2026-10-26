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
        var repeated = batch.RepeatedBillingKeys.ToHashSet(StringComparer.Ordinal);

        List<string>? kept = null;
        List<string>? duplicates = null;

        foreach (var row in batch.Rows)
        {
            var wasInserted = insertedKeys.Contains(row.BillingKey);

            if (wasInserted)
            {
                (kept ??= []).Add(row.BillingKey);
            }

            // A repeated key is a duplicate even when its first occurrence was inserted; a key the statement skipped is
            // a duplicate because it was already on file.
            if (!wasInserted || repeated.Contains(row.BillingKey))
            {
                (duplicates ??= []).Add(row.BillingKey);
            }
        }

        return new UsageBatchResult(kept ?? [], duplicates ?? []);
    }
}
