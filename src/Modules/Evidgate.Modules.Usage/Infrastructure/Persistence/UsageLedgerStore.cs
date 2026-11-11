using System.Data;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Evidgate.Modules.Usage.Infrastructure.Persistence;

/// <summary>
/// The ledger's write path (12.3.1-T1.4, plan §5/§8 P2): one <c>INSERT … SELECT FROM unnest(…)</c> statement over the
/// caller's tenant transaction, with <c>ON CONFLICT (org_id, billing_key) DO NOTHING RETURNING …</c>. One round trip
/// inserts the whole batch and answers what each row ended up as; a separate project validation read prevents FK errors
/// from aborting the caller's transaction. Column uniqueness is the dedupe arbiter, and the returned set distinguishes
/// inserts from duplicates. A row whose period is already issued is re-dated into the next open period by the table's
/// insert guard (migration 0094), and the returned <c>original_period</c> is how this side learns it moved.
/// <para>
/// The statement runs on the module's EF write model (00 §5.5 C4/C5 — the Dapper path is read-only by construction), so it
/// uses the connection and the transaction <see cref="ITenantDbContextFactory{TContext}"/> bound to the caller's scope and
/// RLS applies. It never takes <c>org_id</c> from the batch — the parameter is the scope's tenant, so a caller cannot write
/// into another org. EF's batching cannot express the conflict clause or the returned set, hence the explicit command;
/// the parameters are Npgsql parameters all the same, as <c>ProducerRegistry</c>'s one-statement insert does.
/// </para>
/// </summary>
internal sealed partial class UsageLedgerStore(
    ITenantDbContextFactory<UsageDbContext> contexts,
    ITenantScopeAccessor scopes,
    ILogger<UsageLedgerStore> logger)
{
    /// <summary>
    /// The stored row of one key, on the unique index <c>(org_id, billing_key)</c>: the columns a caller compares before
    /// treating a duplicate key as its own replay. <c>occurred_at</c> is not read — it is not part of that identity.
    /// </summary>
    private const string FindSql = """
        select billing_key, metric, project_id, quantity, unit, period, original_period
        from public.usage_ledger_entry
        where org_id = @org_id and billing_key = @billing_key
        """;

    /// <summary>
    /// The batch as a single statement. <c>payload_hash</c> is the canonical digest of what the row bills, so a later
    /// request under the same key can be compared with the stored row rather than assumed to replay it. <c>price_ref</c>,
    /// <c>amount</c> and <c>currency</c> are written as null: this path stores unpriced rows, and the price is resolved
    /// inside the same transaction. <c>occurred_at</c> travels as UTC
    /// <c>timestamptz[]</c>, preserving PostgreSQL microsecond precision without floating-point arithmetic or
    /// session-time-zone conversion. The returning set carries what the row became, not what was asked for: the insert
    /// guard may have moved <c>period</c> and set <c>original_period</c>.
    /// </summary>
    private const string InsertSql = """
        insert into public.usage_ledger_entry (
            id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at, payload_hash,
            price_ref, amount, currency)
        select
            x.id, @org_id, x.project_id, x.metric, x.quantity, x.unit, x.period, x.billing_key,
            x.occurred_at, x.payload_hash,
            null, null, null
        from unnest(
            @ids::uuid[],
            @project_ids::uuid[],
            @metrics::text[],
            @quantities::numeric[],
            @units::text[],
            @periods::text[],
            @billing_keys::text[],
            @occurred_at::timestamptz[],
            @payload_hashes::text[])
            with ordinality as x (id, project_id, metric, quantity, unit, period, billing_key, occurred_at, payload_hash, position)
        order by x.position
        on conflict (org_id, billing_key) do nothing
        returning id, billing_key, period, original_period
        """;

    /// <summary>
    /// The constraint the ledger's insert guard raises when a row's month and every month after it are issued: a
    /// <c>23514</c> under this name is that refusal and nothing else, so no other check violation is read as one.
    /// </summary>
    private const string NoOpenPeriodConstraint = "usage_ledger_entry_no_open_period";

    /// <summary>
    /// Counts every duplicate billing-key attempt of one batch, in the same transaction as the ledger insert.
    /// <c>attempts = attempts + 1</c> on conflict is the whole tally — a replayed key increments the counter it already
    /// has — and only the counter and the last sighting are written: the key of a duplicate row never moves, so a
    /// reconciliation join on <c>(org_id, billing_key)</c> stays valid. Rows go in billing-key order for the same reason
    /// the ledger's do: two concurrent batches take the unique-key locks in one global order.
    /// </summary>
    private const string RecordDuplicatesSql = """
        insert into public.usage_duplicate_attempt (
            id, org_id, billing_key, attempts, first_seen_at, last_seen_at)
        select
            x.id, @org_id, x.billing_key, 1, now(), now()
        from unnest(@ids::uuid[], @billing_keys::text[])
            with ordinality as x (id, billing_key, position)
        order by x.billing_key
        on conflict (org_id, billing_key) do update
        set attempts = public.usage_duplicate_attempt.attempts + 1,
            last_seen_at = now()
        """;

    /// <summary>
    /// Reads the row <paramref name="orgId"/>'s tenant holds under <paramref name="billingKey"/>, or null when it holds
    /// none. One indexed read on the caller's tenant transaction: row security makes another org's key an absence, and the
    /// read takes no lock, so reading a key never blocks the writer that created it.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope (never a value from the caller's key).</param>
    /// <param name="billingKey">The key to read; the unique index on <c>(org_id, billing_key)</c> answers it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<UsageEntryRecord?> FindAsync(
        Guid orgId,
        string billingKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(billingKey);

        await using var context = contexts.Create();
        await using var command = (NpgsqlCommand)context.Database.GetDbConnection().CreateCommand();
        command.Transaction = (NpgsqlTransaction)scopes.Require().Transaction;
        command.CommandText = FindSql;
        command.Parameters.Add(new NpgsqlParameter("org_id", NpgsqlDbType.Uuid) { Value = orgId });
        command.Parameters.Add(new NpgsqlParameter("billing_key", NpgsqlDbType.Text) { Value = billingKey });

        await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SingleRow, cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var projectIsNull = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false);
        var originalPeriodIsNull = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false);

        return new UsageEntryRecord(
            reader.GetString(0),
            reader.GetString(1),
            projectIsNull ? null : reader.GetGuid(2),
            reader.GetDecimal(3),
            reader.GetString(4),
            reader.GetString(5),
            originalPeriodIsNull ? null : reader.GetString(6));
    }

    /// <summary>
    /// Validates the distinct projects of <paramref name="entries"/> in one read inside the caller's transaction. A
    /// project of another org is invisible under RLS, so unknown and foreign ids get the same per-entry refusal instead
    /// of a 23503 that would abort the caller. The read takes no row lock: the insert's own foreign-key check takes the
    /// key-share lock it needs, and a project is soft-deleted (`status`), never removed, so nothing can vanish between
    /// this read and the statement.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope (never a value from the batch).</param>
    /// <param name="entries">The entries whose projects to check.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<IReadOnlyList<UsageViolation>> ValidateProjectsAsync(
        Guid orgId,
        IReadOnlyList<UsageEntryInput> entries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var projectIds = entries
            .Where(entry => entry.ProjectId.HasValue)
            .Select(entry => entry.ProjectId.GetValueOrDefault())
            .Distinct()
            .ToArray();

        if (projectIds.Length == 0)
        {
            return [];
        }

        await using var context = contexts.Create();
        var visible = await UsageProjectVisibility.VisibleAsync(
                context.Database.GetDbConnection(),
                scopes.Require().Transaction,
                orgId,
                projectIds,
                cancellationToken)
            .ConfigureAwait(false);

        List<UsageViolation>? violations = null;
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].ProjectId is { } projectId && !visible.Contains(projectId))
            {
                (violations ??= []).Add(new UsageViolation(index, "project_id", "invalid_value"));
            }
        }

        return violations ?? [];
    }

    /// <summary>
    /// Records <paramref name="batch"/> for <paramref name="orgId"/>: the ledger insert, then one statement counting the
    /// keys the batch reported as duplicates. The tally shares the caller's transaction, so a batch that rolls back
    /// leaves neither a ledger row nor an attempt — the reconciliation cannot report a duplicate that was never
    /// accepted. A batch with no duplicate writes nothing beyond its insert.
    /// <para>
    /// Every skipped key is compared with the row on file before the batch is accepted: a key that holds a different
    /// payload, or that the request repeated with a different payload, is a conflict. The insert sits behind a
    /// savepoint, so a conflict rolls the whole batch back — no row of any key survives — and answers the conflicting
    /// keys instead of writing a tally. A conflict is permanent: the stored payload is never rewritten.
    /// </para>
    /// <para>
    /// A period an issued statement froze is not refused any more: the insert guard re-dates that row into the next
    /// open period and names the original one in <c>original_period</c>, so late usage is billed instead of dropped.
    /// The returned redated rows are what the caller audits; a period with no open month after it refuses the whole
    /// insert with the trigger's named check violation, which reaches the caller as
    /// <see cref="LedgerNoOpenPeriodException"/>.
    /// </para>
    /// </summary>
    /// <param name="orgId">The tenant of the open scope (never a value from the batch).</param>
    /// <param name="batch">The prepared batch to record.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The inserted keys, the inserted rows the guard moved, and the keys that conflict.</returns>
    /// <exception cref="LedgerNoOpenPeriodException">A row's period and every period after it are issued.</exception>
    internal async Task<UsageLedgerWrite> RecordBatchAsync(
        Guid orgId,
        UsageBatch batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var scope = scopes.Require();
        var transaction = scope.Transaction;
        await transaction.SaveAsync(BatchSavepoint, cancellationToken).ConfigureAwait(false);

        var recorded = await InsertAsync(orgId, batch.Rows, cancellationToken).ConfigureAwait(false);
        var inserted = recorded.Select(row => row.BillingKey).ToArray();
        var conflicts = await FindConflictsAsync(orgId, batch, inserted, cancellationToken).ConfigureAwait(false);

        if (conflicts.Count > 0)
        {
            await transaction.RollbackAsync(BatchSavepoint, cancellationToken).ConfigureAwait(false);
            await ReleaseBatchSavepointAsync(scope.Connection, transaction, cancellationToken).ConfigureAwait(false);
            ReportConflicts(orgId, conflicts);
            return new UsageLedgerWrite([], [], [.. conflicts.Select(conflict => conflict.BillingKey)]);
        }

        var duplicates = batch.DuplicateBillingKeys(inserted.ToHashSet(StringComparer.Ordinal));

        if (duplicates.Count > 0)
        {
            await RecordDuplicateAttemptsAsync(orgId, duplicates, cancellationToken).ConfigureAwait(false);
        }

        await ReleaseBatchSavepointAsync(scope.Connection, transaction, cancellationToken).ConfigureAwait(false);

        return new UsageLedgerWrite(
            inserted,
            [.. recorded.Where(row => row.OriginalPeriod is not null)],
            []);
    }

    /// <summary>
    /// Releases <see cref="BatchSavepoint"/> with an asynchronous command. The provider's savepoint helper performs
    /// synchronous I/O, which would block a request thread, so the fixed statement runs on the scope's own connection and
    /// transaction instead.
    /// </summary>
    /// <param name="connection">The scope's connection, which owns the savepoint.</param>
    /// <param name="transaction">The scope's open transaction.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private static async Task ReleaseBatchSavepointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "release savepoint " + BatchSavepoint;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordDuplicateAttemptsAsync(
        Guid orgId,
        IReadOnlyList<string> billingKeys,
        CancellationToken cancellationToken)
    {
        var ordered = billingKeys.Order(StringComparer.Ordinal).ToArray();
        var ids = new Guid[ordered.Length];
        for (var index = 0; index < ordered.Length; index++)
        {
            ids[index] = Guid.CreateVersion7();
        }

        await using var context = contexts.Create();
        var scope = scopes.Require();

        await using var command = (NpgsqlCommand)context.Database.GetDbConnection().CreateCommand();
        command.CommandText = RecordDuplicatesSql;
        command.Transaction = (NpgsqlTransaction)scope.Transaction;
        command.Parameters.Add(new NpgsqlParameter("org_id", NpgsqlDbType.Uuid) { Value = orgId });
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
        command.Parameters.Add(new NpgsqlParameter("billing_keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = ordered });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts <paramref name="rows"/> for <paramref name="orgId"/> and answers each row the statement inserted as it
    /// was stored; a key an existing row already holds is skipped by the conflict clause and is therefore absent from
    /// the answer. The rows are written in billing-key order whatever the request order: every batch takes the
    /// unique-index locks in one global order, so two concurrent batches with overlapping keys cannot deadlock (40P01),
    /// and the answer is mapped back by key rather than by position.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope (never a value from the batch).</param>
    /// <param name="rows">The rows to insert; empty writes nothing.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="LedgerNoOpenPeriodException">
    /// A row's period and every period after it are issued, so the guard found no month to bill.
    /// </exception>
    internal async Task<IReadOnlyList<RecordedUsageRow>> InsertAsync(
        Guid orgId,
        IReadOnlyList<PendingUsageEntry> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return [];
        }

        var ids = new Guid[rows.Count];
        var projectIds = new Guid?[rows.Count];
        var metrics = new string[rows.Count];
        var quantities = new decimal[rows.Count];
        var units = new string[rows.Count];
        var periods = new string[rows.Count];
        var billingKeys = new string[rows.Count];
        var occurredAt = new DateTime[rows.Count];
        var payloadHashes = new string[rows.Count];
        // Every transaction acquires unique-key locks in the same ordinal order, regardless of request order.
        var orderedRows = rows.OrderBy(row => row.BillingKey, StringComparer.Ordinal).ToArray();

        for (var index = 0; index < rows.Count; index++)
        {
            var row = orderedRows[index];
            ids[index] = row.Id;
            projectIds[index] = row.ProjectId;
            metrics[index] = row.Metric;
            quantities[index] = row.Quantity;
            units[index] = row.Unit;
            periods[index] = row.Period;
            billingKeys[index] = row.BillingKey;
            occurredAt[index] = row.OccurredAt.UtcDateTime;
            payloadHashes[index] = row.PayloadHash;
        }

        // The connection comes from the module's write model (00 §5.5 C4) and the transaction from the tenant scope it is
        // bound to (00 §5.5 C3): the scope owns both, so this call commits nothing and a caller's rollback removes the rows.
        await using var context = contexts.Create();
        var scope = scopes.Require();

        await using var command = (NpgsqlCommand)context.Database.GetDbConnection().CreateCommand();
        command.CommandText = InsertSql;
        command.Transaction = (NpgsqlTransaction)scope.Transaction;
        command.Parameters.Add(new NpgsqlParameter("org_id", NpgsqlDbType.Uuid) { Value = orgId });
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
        command.Parameters.Add(new NpgsqlParameter("project_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = projectIds });
        command.Parameters.Add(new NpgsqlParameter("metrics", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = metrics });
        command.Parameters.Add(new NpgsqlParameter("quantities", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = quantities });
        command.Parameters.Add(new NpgsqlParameter("units", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = units });
        command.Parameters.Add(new NpgsqlParameter("periods", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = periods });
        command.Parameters.Add(new NpgsqlParameter("billing_keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = billingKeys });
        command.Parameters.Add(new NpgsqlParameter("occurred_at", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = occurredAt });
        command.Parameters.Add(new NpgsqlParameter("payload_hashes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = payloadHashes });

        return await ExecuteInsertAsync(command, rows, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the batch insert and answers the rows it stored. Only the guard's no-open-period refusal is read here: it
    /// means the row's month and every month after it were issued, so the caller learns which period it named.
    /// </summary>
    /// <param name="command">The batch insert, on the caller's tenant transaction.</param>
    /// <param name="rows">The rows it carries, in request order.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="LedgerNoOpenPeriodException">A row's period has no open month after it.</exception>
    private static async Task<List<RecordedUsageRow>> ExecuteInsertAsync(
        NpgsqlCommand command,
        IReadOnlyList<PendingUsageEntry> rows,
        CancellationToken cancellationToken)
    {
        var recorded = new List<RecordedUsageRow>(rows.Count);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
                .ConfigureAwait(false);

            // SequentialAccess only moves forward over the row, so the columns are read in ordinal order.
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetGuid(0);
                var billingKey = reader.GetString(1);
                var period = reader.GetString(2);
                var originalPeriodIsNull = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false);
                recorded.Add(new RecordedUsageRow(
                    id,
                    billingKey,
                    period,
                    originalPeriodIsNull ? null : reader.GetString(3)));
            }
        }
        catch (PostgresException exception) when (IsNoOpenPeriodRefusal(exception))
        {
            throw new LedgerNoOpenPeriodException(PeriodOf(exception, rows));
        }

        return recorded;
    }

    /// <summary>True for the ledger guard's no-open-period refusal and for no other check violation.</summary>
    /// <param name="exception">The database error of the insert.</param>
    private static bool IsNoOpenPeriodRefusal(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.CheckViolation
        && string.Equals(exception.ConstraintName, NoOpenPeriodConstraint, StringComparison.Ordinal);

    /// <summary>
    /// The period the refused row named: the guard's message carries it because the refusal aborts the caller's
    /// transaction, so nothing can be re-read to ask which period was refused. A message naming no period falls back
    /// to the batch's first, the only period this side still knows.
    /// </summary>
    /// <param name="exception">The guard's refusal.</param>
    /// <param name="rows">The rows of the refused batch, in request order.</param>
    private static string PeriodOf(PostgresException exception, IReadOnlyList<PendingUsageEntry> rows)
    {
        var message = exception.MessageText;
        for (var start = 0; start + 7 <= message.Length; start++)
        {
            if (UsagePeriod.IsWellFormed(message.AsSpan(start, 7)))
            {
                return message.Substring(start, 7);
            }
        }

        return rows[0].Period;
    }
}
