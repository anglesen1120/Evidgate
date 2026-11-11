using Evidgate.BuildingBlocks.Observability;
using Evidgate.Modules.Usage.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Evidgate.Modules.Usage.Infrastructure.Persistence;

/// <summary>
/// The ledger write path's payload comparison: a billing key that already holds a different payload, or a request that
/// repeats a key with a different payload, is a conflict the batch answers instead of billing.
/// </summary>
internal sealed partial class UsageLedgerStore
{
    /// <summary>
    /// The savepoint the batch insert and its tally sit behind, so a conflict can remove every row the batch wrote,
    /// including the keys that did not conflict.
    /// </summary>
    private const string BatchSavepoint = "usage_ledger_batch";

    /// <summary>
    /// The stored payload of one key, on the unique index <c>(org_id, billing_key)</c>. The follow-up read after
    /// <c>ON CONFLICT DO NOTHING</c> cannot race: the conflict clause waited for any concurrent writer of the key, a
    /// new statement sees its committed row, and the app role holds no UPDATE on the ledger, so the read row is final.
    /// </summary>
    private const string StoredPayloadSql = """
        select billing_key, metric, project_id, quantity, unit, coalesce(original_period, period), payload_hash
        from public.usage_ledger_entry
        where org_id = @org_id and billing_key = any(@billing_keys::text[])
        """;

    /// <summary>
    /// Compares every key the insert skipped and every key the request repeated with a different payload against the
    /// stored row. A legacy row (written before <c>payload_hash</c> existed) is hashed from its stored billable columns,
    /// with its requested period, so it compares like any other row.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope (never a value from the batch).</param>
    /// <param name="batch">The prepared batch the insert just ran for.</param>
    /// <param name="inserted">The keys the insert statement returned.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async Task<List<LedgerKeyConflict>> FindConflictsAsync(
        Guid orgId,
        UsageBatch batch,
        IReadOnlyList<string> inserted,
        CancellationToken cancellationToken)
    {
        var insertedKeys = inserted.ToHashSet(StringComparer.Ordinal);
        var keysToRead = batch.Rows
            .Where(row => !insertedKeys.Contains(row.BillingKey)
                || batch.ConflictingPayloadHashes.ContainsKey(row.BillingKey))
            .Select(row => row.BillingKey)
            .ToArray();

        if (keysToRead.Length == 0)
        {
            return [];
        }

        var stored = await ReadStoredPayloadsAsync(orgId, keysToRead, cancellationToken).ConfigureAwait(false);
        var conflicts = new List<LedgerKeyConflict>();

        foreach (var row in batch.Rows)
        {
            var inRequestConflict = batch.ConflictingPayloadHashes.TryGetValue(row.BillingKey, out var repeatedHash);

            if (!inRequestConflict && insertedKeys.Contains(row.BillingKey))
            {
                continue;
            }

            if (!stored.TryGetValue(row.BillingKey, out var storedRow))
            {
                throw new InvalidOperationException(
                    $"the usage ledger holds no row under a billing key the batch just wrote or skipped: {row.BillingKey}");
            }

            if (ConflictOf(row, storedRow, repeatedHash) is { } conflict)
            {
                conflicts.Add(conflict);
            }
        }

        return conflicts;
    }

    /// <summary>
    /// The conflict one key reports, or null when the payloads agree. The two digests it carries must really disagree: a
    /// repeat can carry the payload already on file, so when the request's first occurrence already differs from the
    /// stored row, that occurrence is the claim reported.
    /// </summary>
    /// <param name="row">The row the batch claims for the key.</param>
    /// <param name="storedRow">The row on file under that key.</param>
    /// <param name="repeatedHash">The differing hash of the request's repeat of the key, or null when it did not repeat.</param>
    private static LedgerKeyConflict? ConflictOf(
        PendingUsageEntry row,
        StoredLedgerPayload storedRow,
        string? repeatedHash)
    {
        var storedHash = storedRow.PayloadHash ?? UsagePayloadHash.Compute(
            storedRow.Metric,
            storedRow.ProjectId,
            storedRow.Quantity,
            storedRow.Unit,
            storedRow.RequestedPeriod);

        var firstOccurrenceDiffers = !string.Equals(storedHash, row.PayloadHash, StringComparison.Ordinal);

        if (repeatedHash is null)
        {
            return firstOccurrenceDiffers
                ? new LedgerKeyConflict(row.BillingKey, row.ProjectId, storedHash, row.PayloadHash)
                : null;
        }

        return new LedgerKeyConflict(
            row.BillingKey,
            row.ProjectId,
            storedHash,
            firstOccurrenceDiffers ? row.PayloadHash : repeatedHash);
    }

    /// <summary>Reads the stored billable payload of <paramref name="billingKeys"/> in one statement.</summary>
    private async Task<Dictionary<string, StoredLedgerPayload>> ReadStoredPayloadsAsync(
        Guid orgId,
        IReadOnlyList<string> billingKeys,
        CancellationToken cancellationToken)
    {
        await using var context = contexts.Create();
        await using var command = (NpgsqlCommand)context.Database.GetDbConnection().CreateCommand();
        command.CommandText = StoredPayloadSql;
        command.Transaction = (NpgsqlTransaction)scopes.Require().Transaction;
        command.Parameters.Add(new NpgsqlParameter("org_id", NpgsqlDbType.Uuid) { Value = orgId });
        command.Parameters.Add(
            new NpgsqlParameter("billing_keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = billingKeys.ToArray() });

        var stored = new Dictionary<string, StoredLedgerPayload>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var projectIsNull = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false);
            var hashIsNull = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false);

            stored[reader.GetString(0)] = new StoredLedgerPayload(
                reader.GetString(1),
                projectIsNull ? null : reader.GetGuid(2),
                reader.GetDecimal(3),
                reader.GetString(4),
                reader.GetString(5),
                hashIsNull ? null : reader.GetString(6));
        }

        return stored;
    }

    /// <summary>
    /// Records each conflict once at <see cref="LogLevel.Error"/> and counts them. Only the identity of the key and the
    /// two digests are logged: the billable payload stays in the ledger, never in a log line.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="conflicts">The conflicting keys and the digests that disagree.</param>
    private void ReportConflicts(Guid orgId, IReadOnlyList<LedgerKeyConflict> conflicts)
    {
        foreach (var conflict in conflicts)
        {
            LogBillingKeyConflict(
                orgId,
                conflict.ProjectId,
                conflict.BillingKey,
                conflict.StoredHash,
                conflict.IncomingHash);
            EvidgateTelemetry.RecordUsageBillingKeyConflict();
        }
    }

    [LoggerMessage(
        EventId = 2479,
        Level = LogLevel.Error,
        Message = "Usage ledger billing key conflict in org {Org}: key {BillingKey} (project {Project}) holds {StoredHash}, the batch claims {IncomingHash}; the batch was rolled back and nothing was billed")]
    private partial void LogBillingKeyConflict(
        Guid org,
        Guid? project,
        string billingKey,
        string storedHash,
        string incomingHash);

    /// <summary>One key whose stored payload differs from the payload the batch claims.</summary>
    private sealed record LedgerKeyConflict(
        string BillingKey,
        Guid? ProjectId,
        string StoredHash,
        string IncomingHash);

    /// <summary>The stored billable columns of one ledger row, with its requested period.</summary>
    private sealed record StoredLedgerPayload(
        string Metric,
        Guid? ProjectId,
        decimal Quantity,
        string Unit,
        string RequestedPeriod,
        string? PayloadHash);
}
