using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Usage.Domain;

namespace Evidgate.Modules.Usage.Application;

/// <summary>
/// Audits the one decision a ledger write cannot see for itself: a row metered for an issued period was re-dated into
/// the next open month. The audit row shares the caller's transaction, so a write that rolls back leaves no entry
/// claiming a move that never committed, and an entry that cannot be written fails the write instead of passing
/// unaudited.
/// </summary>
/// <remarks>
/// One entry per distinct pair of months, because a batch may carry several late periods and one row cannot truthfully
/// name two. The actor is the caller of the flow when a host resolved one; a task attempt resolves none, and its rows
/// name the system. When the flow already holds an audit unit — a command's pipeline, which flushes it at commit — this
/// helper only queues its entries and leaves the flush and the chain lock to that unit; otherwise it opens a unit and
/// flushes it before returning, so the row is durable with the ledger write rather than after it.
/// </remarks>
/// <param name="audit">The audit buffer of the current flow.</param>
/// <param name="db">The scoped data access of the open tenant transaction.</param>
/// <param name="principals">The ambient caller, if any.</param>
internal sealed class LedgerRedateAudit(
    IAuditTrail audit,
    ITenantDb db,
    IRequestPrincipalAccessor principals)
{
    /// <summary>The audited action of one batch whose rows the insert guard moved.</summary>
    internal const string Action = "usage.entry_redated";

    /// <summary>The object type the audit row names: the ledger the rows belong to.</summary>
    internal const string ObjectType = "usage_ledger";

    /// <summary>
    /// Records one entry per original and assigned month this batch moved rows between; writes nothing when the guard
    /// moved nothing.
    /// </summary>
    /// <param name="orgId">The tenant of the open transaction, which is also the chain the rows join.</param>
    /// <param name="redated">The inserted rows the guard re-dated.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task RecordAsync(
        Guid orgId,
        IReadOnlyList<RecordedUsageRow> redated,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(redated);

        if (redated.Count == 0)
        {
            return;
        }

        var ownsUnit = !audit.HasUnit;
        using var unit = ownsUnit ? audit.BeginUnit() : null;
        var actor = AuditActorResolver.From(principals.Current);

        foreach (var moved in redated.GroupBy(row => (row.OriginalPeriod, row.Period)))
        {
            audit.Describe(new AuditObject(
                ObjectType,
                moved.First().Id.ToString("D"),
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["original_period"] = moved.Key.OriginalPeriod!,
                    ["period"] = moved.Key.Period,
                    ["count"] = moved.Count().ToString(CultureInfo.InvariantCulture),
                }));
            audit.Record(Action, actor);
        }

        if (ownsUnit)
        {
            await audit.FlushAsync(orgId, db, cancellationToken).ConfigureAwait(false);
        }
    }
}
