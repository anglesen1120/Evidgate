namespace Evidgate.Modules.Usage.Domain;

/// <summary>
/// One ledger row as its insert returned it: the period the row ended up in and, when the issued-period guard moved
/// it, the period it was metered for. The insert returns the row's identity so an audit entry can name it.
/// </summary>
/// <param name="Id">The row identity the module minted.</param>
/// <param name="BillingKey">The per-org dedupe key the row was recorded under.</param>
/// <param name="Period">The period the row was stored in: the requested one unless the guard re-dated it.</param>
/// <param name="OriginalPeriod">The period the row was metered for when the guard moved it, otherwise null.</param>
internal sealed record RecordedUsageRow(Guid Id, string BillingKey, string Period, string? OriginalPeriod);

/// <summary>
/// What one batch wrote: the billing keys the insert statement inserted, the rows its issued-period guard re-dated,
/// and the billing keys whose stored or in-request payload disagreed so the batch was rolled back. A write with
/// conflicts has nothing inserted and nothing re-dated.
/// </summary>
/// <param name="Inserted">The keys the statement inserted, in the order the statement returned them.</param>
/// <param name="Redated">The inserted rows the guard moved into a later period; empty in the ordinary case.</param>
/// <param name="Conflicts">The keys that hold a different payload; empty in the ordinary case.</param>
internal sealed record UsageLedgerWrite(
    IReadOnlyList<string> Inserted,
    IReadOnlyList<RecordedUsageRow> Redated,
    IReadOnlyList<string> Conflicts);
