namespace Evidgate.Modules.Usage.Contracts;

/// <summary>
/// The usage surface other modules call in process (00 §5.5, 12.3.1 §5). Every member runs inside the caller's
/// tenant transaction — the caller opened it through the C2 pipeline, so this interface never opens a connection or
/// a transaction, never commits and never rolls back — and the organization is always the scope's tenant, never a
/// value the caller passes (00 §5.4).
/// </summary>
public interface IUsageLedger
{
    /// <summary>
    /// The largest batch one call accepts (12.3.1-T1.4): a caller that wants to record more splits the call, so one
    /// metering source cannot turn into an unbounded statement.
    /// </summary>
    const int MaxBatchSize = 1000;

    /// <summary>Longest <see cref="UsageEntryInput.BillingKey"/> the column accepts (<c>billing_key</c> 1..200).</summary>
    const int MaxBillingKeyLength = 200;

    /// <summary>
    /// Longest <see cref="UsageEntryInput.Unit"/> the column accepts (<c>unit</c> 1..32 characters, counted in code
    /// points as the column's <c>char_length</c> check does).
    /// </summary>
    const int MaxUnitLength = 32;

    /// <summary>
    /// Records <paramref name="request"/>'s entries as ledger rows. Each row is one fact: the same
    /// <c>(org_id, billing_key)</c> is inserted once, so a resent metering event reports its key under
    /// <see cref="UsageBatchResult.Duplicates"/> instead of writing a second row (ruling T43). The rows themselves are one
    /// <c>INSERT … SELECT FROM unnest(…)</c> statement with <c>ON CONFLICT (org_id, billing_key) DO NOTHING</c>
    /// (ruling T50, plan §8 P2) whatever the batch size, and the projects it names are read once before it, so a batch is
    /// three statements — never one per entry. The rows are written in billing-key order, so two concurrent batches with
    /// overlapping keys lock in one order and cannot deadlock.
    /// <para>
    /// An entry that repeats an earlier entry's billing key inside the same request is reported under
    /// <see cref="UsageBatchResult.Duplicates"/> as well: the first occurrence is the one recorded. A key already on
    /// file is a duplicate whatever its payload — the first write wins and a later payload is ignored, not compared.
    /// Price and amount stay null until 12.3.1-T3 resolves prices (DECISIONS O5, ruling T60). Before the insert the
    /// distinct <see cref="UsageEntryInput.ProjectId"/> values are checked in one read inside the caller's transaction,
    /// so an unknown project — or one of another org, which row security hides — is a refused entry instead of a
    /// foreign-key error that would abort the caller's transaction. A period an issued statement froze is not refused:
    /// the row is billed in the next open period and keeps the period it was metered for in
    /// <see cref="UsageEntryRecord.OriginalPeriod"/>, and the move is audited in the caller's transaction.
    /// </para>
    /// </summary>
    /// <param name="request">The entries to record; an empty batch writes nothing and answers two empty lists.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The inserted and the duplicate billing keys, in request order.</returns>
    /// <exception cref="ArgumentException">
    /// The batch breaks the entry rules: a null or longer-than-1000 entry list, a null entry, an undeclared metric, a
    /// negative <c>quantity</c> (a correction is an adjustment row, never a negative plain row), a <c>period</c> that is
    /// not <c>YYYY-MM</c>, a billing key that is empty, longer than <see cref="MaxBillingKeyLength"/> or not storable
    /// text, a <c>unit</c> that is empty, longer than <see cref="MaxUnitLength"/> or not storable text, or a project that
    /// is not one of the caller's org. Storable text carries no U+0000 and no lone UTF-16 surrogate, which PostgreSQL
    /// <c>text</c> cannot hold. The caller built these entries, so this is the caller's bug: the message names the first
    /// offending entry, and nothing was written.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// No tenant transaction is open: the tenant-scope guard throws before any statement runs. The same type is what the
    /// implementation throws, carrying the period, when an entry's month and every month after it are issued — there is
    /// no open period to bill, so an in-process emitter fails loudly and its queue retries and dead-letters the row
    /// rather than the ledger writing into a billed month.
    /// </exception>
    Task<UsageBatchResult> RecordAsync(RecordUsageBatchRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the row <paramref name="billingKey"/> recorded for the caller's org, or null when this org has none: another
    /// org's key is invisible under row security, so a cross-org lookup is an absence, never a row. The same rules as
    /// <see cref="RecordAsync"/> apply — one statement inside the caller's tenant transaction, no connection or
    /// transaction of its own, no commit — because a caller that is about to treat a duplicate key as its own replay
    /// reads what the first write stored.
    /// </summary>
    /// <param name="billingKey">The key whose stored row to read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The stored row, or null when this tenant has no row under the key.</returns>
    /// <exception cref="InvalidOperationException">No tenant transaction is open: the scope guard throws before any statement runs.</exception>
    Task<UsageEntryRecord?> FindAsync(string billingKey, CancellationToken cancellationToken);
}
