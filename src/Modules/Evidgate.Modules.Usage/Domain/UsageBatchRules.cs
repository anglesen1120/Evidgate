using System.Text;
using Evidgate.Modules.Usage.Contracts;

namespace Evidgate.Modules.Usage.Domain;

/// <summary>
/// The entry rules of one ledger batch (12.3.1-T1.4, plan §5): the batch is validated whole before any statement runs,
/// so a refused call writes nothing and answers one 00 §9.2 detail per offending entry, indexed by position. The rules
/// are the column contract of <c>usage_ledger_entry</c> — an undeclared metric, a period that is not <c>YYYY-MM</c>, a
/// negative quantity, a billing key outside 1..200 or a unit outside 1..32 would each be a server error instead of a
/// 422 — plus the batch cap that keeps one call one bounded insert statement (ruling T50, plan §8 P2; the write's other
/// statement is the single project read of the entries it names).
/// </summary>
internal static class UsageBatchRules
{
    /// <summary>
    /// Validates <paramref name="entries"/> and answers every violation in request order; an empty list means the batch may
    /// be prepared. The batch cap is the only document-level rule (a null list is <c>required</c>), because it cannot name
    /// an entry.
    /// <para>
    /// Quantities must be nonnegative: negative ledger rows belong to the separate adjustment path. Text must be
    /// PostgreSQL-compatible Unicode (no NUL or lone UTF-16 surrogate), and lengths count Unicode code points.
    /// </para>
    /// </summary>
    /// <param name="entries">The entries of the batch, or null when the caller sent none at all.</param>
    internal static IReadOnlyList<UsageViolation> Validate(IReadOnlyList<UsageEntryInput>? entries)
    {
        if (entries is null)
        {
            return [new UsageViolation(null, "entries", "required")];
        }

        if (entries.Count > IUsageLedger.MaxBatchSize)
        {
            return [new UsageViolation(null, "entries", "too_many")];
        }

        List<UsageViolation>? violations = null;

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry is null)
            {
                (violations ??= []).Add(new UsageViolation(index, string.Empty, "required"));
                continue;
            }

            if (!Enum.IsDefined(entry.Metric))
            {
                (violations ??= []).Add(new UsageViolation(index, "metric", "invalid_value"));
            }

            if (entry.Quantity < 0)
            {
                (violations ??= []).Add(new UsageViolation(index, "quantity", "invalid_value"));
            }

            if (!UsagePeriod.IsWellFormed(entry.Period))
            {
                (violations ??= []).Add(new UsageViolation(index, "period", "invalid_value"));
            }

            if (string.IsNullOrWhiteSpace(entry.BillingKey))
            {
                (violations ??= []).Add(new UsageViolation(index, "billing_key", "required"));
            }
            else if (!PostgresText.IsStorable(entry.BillingKey))
            {
                (violations ??= []).Add(new UsageViolation(index, "billing_key", "invalid_value"));
            }
            else if (entry.BillingKey.EnumerateRunes().Count() > IUsageLedger.MaxBillingKeyLength)
            {
                // Characters, not UTF-16 units: the column contract is 1..200 characters and PostgreSQL's char_length
                // counts code points, so a 200-emoji key is legal while `string.Length` would call it 400 (see
                // AuthoringText for the same rule elsewhere in the solution).
                (violations ??= []).Add(new UsageViolation(index, "billing_key", "too_long"));
            }

            // `unit` is a NOT NULL column: a missing unit would surface as a 23502 rather than a refusal, so it is a
            // required member of the entry here. The column's check keeps it at 1..32 code points.
            if (string.IsNullOrWhiteSpace(entry.Unit))
            {
                (violations ??= []).Add(new UsageViolation(index, "unit", "required"));
            }
            else if (!PostgresText.IsStorable(entry.Unit))
            {
                (violations ??= []).Add(new UsageViolation(index, "unit", "invalid_value"));
            }
            else if (entry.Unit.EnumerateRunes().Count() > IUsageLedger.MaxUnitLength)
            {
                (violations ??= []).Add(new UsageViolation(index, "unit", "too_long"));
            }
        }

        return violations ?? [];
    }

    /// <summary>
    /// Turns a validated batch into the rows to insert. The first occurrence of a billing key wins — a repeat inside the
    /// same request is reported as a duplicate and never reaches the statement — so the insert never carries the same
    /// <c>(org_id, billing_key)</c> twice and the duplicates of a response are decided by request order alone. Identities
    /// are minted here (UUIDv7), once per row, so a retried call keeps the ordering properties of the ledger without reading
    /// anything back.
    /// </summary>
    /// <param name="entries">The entries of a batch that passed <see cref="Validate"/>.</param>
    internal static UsageBatch Prepare(IReadOnlyList<UsageEntryInput> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var rows = new List<PendingUsageEntry>(entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var repeated = new HashSet<string>(StringComparer.Ordinal);
        List<string>? repeatedKeys = null;

        foreach (var entry in entries)
        {
            if (!seen.Add(entry.BillingKey))
            {
                if (repeated.Add(entry.BillingKey))
                {
                    (repeatedKeys ??= []).Add(entry.BillingKey);
                }

                continue;
            }

            rows.Add(new PendingUsageEntry(
                Guid.CreateVersion7(),
                entry.ProjectId,
                UsageMetricWire.ToWireValue(entry.Metric),
                entry.Quantity,
                entry.Unit,
                entry.Period,
                entry.BillingKey,
                entry.OccurredAt));
        }

        return new UsageBatch(rows, repeatedKeys ?? []);
    }
}

/// <summary>
/// A validated batch ready for the one insert: the rows to write (one per distinct billing key, in request order) and the
/// billing keys the request repeated, also in request order.
/// </summary>
/// <param name="Rows">The rows this batch will insert; empty when every entry repeated an earlier key.</param>
/// <param name="RepeatedBillingKeys">The distinct billing keys the request repeated.</param>
internal sealed record UsageBatch(
    IReadOnlyList<PendingUsageEntry> Rows,
    IReadOnlyList<string> RepeatedBillingKeys);
