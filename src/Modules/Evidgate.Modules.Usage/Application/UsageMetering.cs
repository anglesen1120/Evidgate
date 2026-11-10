using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Domain;

namespace Evidgate.Modules.Usage.Application;

/// <summary>
/// Feeds the usage-units counter from a ledger batch that has just been written: the two write paths call
/// this with the rows they prepared and the billing keys the insert returned, so a duplicate key counts
/// nothing and a rolled-back transaction counts nothing either.
/// </summary>
internal static class UsageMetering
{
    /// <summary>
    /// Sums the inserted rows' quantities per metric and records them once the enclosing tenant transaction
    /// commits. A refused or rolled-back batch records nothing, which is what keeps the counter a count of
    /// billed work rather than of attempts.
    /// </summary>
    /// <param name="scope">The caller's tenant scope, whose commit the measurement waits for.</param>
    /// <param name="rows">The batch's prepared rows, deduplicated in request order.</param>
    /// <param name="insertedKeys">The billing keys the statement inserted; a key already on file is absent.</param>
    internal static void Schedule(
        ITenantScope scope,
        IReadOnlyList<PendingUsageEntry> rows,
        IReadOnlyList<string> insertedKeys)
    {
        if (insertedKeys.Count == 0)
        {
            return;
        }

        var recorded = insertedKeys.ToHashSet(StringComparer.Ordinal);
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (recorded.Contains(row.BillingKey))
            {
                totals[row.Metric] = totals.GetValueOrDefault(row.Metric) + row.Quantity;
            }
        }

        scope.AfterCommit(() =>
        {
            foreach (var total in totals)
            {
                EvidgateTelemetry.RecordUsageUnits(total.Key, (double)total.Value);
            }
        });
    }
}
