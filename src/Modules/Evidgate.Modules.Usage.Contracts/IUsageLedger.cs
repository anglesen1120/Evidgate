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
    /// Records <paramref name="request"/>'s entries as ledger rows. Each row is one fact: the same
    /// <c>(org_id, billing_key)</c> is inserted once, so a resent metering event reports its key under
    /// <see cref="UsageBatchResult.Duplicates"/> instead of writing a second row (ruling T43). The whole batch is one
    /// <c>INSERT … SELECT FROM unnest(…)</c> statement with <c>ON CONFLICT (org_id, billing_key) DO NOTHING</c>
    /// (ruling T50, plan §8 P2), so a batch costs one round trip whatever its size.
    /// <para>
    /// An entry that repeats an earlier entry's billing key inside the same request is reported under
    /// <see cref="UsageBatchResult.Duplicates"/> as well: the first occurrence is the one recorded. Price and amount
    /// stay null until 12.3.1-T3 resolves prices (DECISIONS O5, ruling T60).
    /// </para>
    /// </summary>
    /// <param name="request">The entries to record; an empty batch writes nothing and answers two empty lists.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The inserted and the duplicate billing keys, in request order.</returns>
    /// <exception cref="ArgumentException">
    /// The batch breaks the entry rules — a null or longer-than-1000 entry list, a null entry, an undeclared metric,
    /// a <c>period</c> that is not <c>YYYY-MM</c>, or a billing key that is empty or longer than
    /// <see cref="MaxBillingKeyLength"/>. The caller built these entries, so this is the caller's bug: the message
    /// names the first offending entry, and nothing was written.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// No tenant transaction is open: the tenant-scope guard throws before any statement runs.
    /// </exception>
    Task<UsageBatchResult> RecordAsync(RecordUsageBatchRequest request, CancellationToken cancellationToken);
}
