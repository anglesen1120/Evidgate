using System.Data;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Evidgate.Modules.Usage.Infrastructure.Persistence;

/// <summary>
/// The ledger's write path (12.3.1-T1.4, plan §5/§8 P2): one <c>INSERT … SELECT FROM unnest(…)</c> statement over the
/// caller's tenant transaction, with <c>ON CONFLICT (org_id, billing_key) DO NOTHING RETURNING billing_key</c>. One round
/// trip inserts the whole batch; a separate project validation read prevents FK errors from aborting the caller's
/// transaction, and a second read refuses a period an issued statement froze before the insert runs. Column uniqueness is
/// the dedupe arbiter, and the returned set distinguishes inserts from duplicates.
/// <para>
/// The statement runs on the module's EF write model (00 §5.5 C4/C5 — the Dapper path is read-only by construction), so it
/// uses the connection and the transaction <see cref="ITenantDbContextFactory{TContext}"/> bound to the caller's scope and
/// RLS applies. It never takes <c>org_id</c> from the batch — the parameter is the scope's tenant, so a caller cannot write
/// into another org. EF's batching cannot express the conflict clause or the returned set, hence the explicit command;
/// the parameters are Npgsql parameters all the same, as <c>ProducerRegistry</c>'s one-statement insert does.
/// </para>
/// </summary>
internal sealed class UsageLedgerStore(
    ITenantDbContextFactory<UsageDbContext> contexts,
    ITenantScopeAccessor scopes)
{
    /// <summary>
    /// The stored row of one key, on the unique index <c>(org_id, billing_key)</c>: the columns a caller compares before
    /// treating a duplicate key as its own replay. <c>occurred_at</c> is not read — it is not part of that identity.
    /// </summary>
    private const string FindSql = """
        select billing_key, metric, project_id, quantity, unit, period
        from public.usage_ledger_entry
        where org_id = @org_id and billing_key = @billing_key
        """;

    /// <summary>
    /// The batch as a single statement. <c>price_ref</c>, <c>amount</c> and <c>currency</c> are written as null: 12.3.1-T1
    /// stores unpriced rows (DECISIONS O5, ruling T60), and 12.3.1-T3 resolves the price inside the same transaction.
    /// <c>occurred_at</c> travels as UTC <c>timestamptz[]</c>, preserving PostgreSQL microsecond precision without
    /// floating-point arithmetic or session-time-zone conversion.
    /// </summary>
    private const string InsertSql = """
        insert into public.usage_ledger_entry (
            id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at,
            price_ref, amount, currency)
        select
            x.id, @org_id, x.project_id, x.metric, x.quantity, x.unit, x.period, x.billing_key,
            x.occurred_at,
            null, null, null
        from unnest(
            @ids::uuid[],
            @project_ids::uuid[],
            @metrics::text[],
            @quantities::numeric[],
            @units::text[],
            @periods::text[],
            @billing_keys::text[],
            @occurred_at::timestamptz[])
            with ordinality as x (id, project_id, metric, quantity, unit, period, billing_key, occurred_at, position)
        order by x.position
        on conflict (org_id, billing_key) do nothing
        returning billing_key
        """;

    /// <summary>
    /// Reads the periods of one batch that an issued statement already froze, in one statement inside the caller's
    /// transaction. Row security answers the scope's org only, and the argument is the distinct set the batch names.
    /// </summary>
    private const string IssuedPeriodsSql = """
        select distinct period from public.billing_statement
        where org_id = @org_id and period = any(@periods)
        """;

    /// <summary>
    /// The constraint the ledger's issued-period trigger raises: a <c>23514</c> under this name is the trigger's
    /// refusal and nothing else, so no other check violation is read as one.
    /// </summary>
    private const string IssuedPeriodConstraint = "usage_ledger_entry_period_issued";

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
    /// read takes no lock, so a caller comparing a duplicate key never blocks the writer that created it.
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

        return new UsageEntryRecord(
            reader.GetString(0),
            reader.GetString(1),
            projectIsNull ? null : reader.GetGuid(2),
            reader.GetDecimal(3),
            reader.GetString(4),
            reader.GetString(5));
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
    /// A period an issued statement froze takes no row, so the batch is refused whole before the insert runs: the
    /// trigger refuses the same row, but its refusal aborts the caller's transaction, which a caller mid-work cannot
    /// afford. A statement issued while this batch is in flight is the trigger's decision, and it reaches the caller as
    /// this same refusal.
    /// </para>
    /// </summary>
    /// <param name="orgId">The tenant of the open scope (never a value from the batch).</param>
    /// <param name="batch">The prepared batch to record.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The billing keys the insert statement inserted, for <c>UsageBatchOutcome.Merge</c>.</returns>
    /// <exception cref="LedgerPeriodIssuedException">A period of the batch is already issued.</exception>
    internal async Task<IReadOnlyList<string>> RecordBatchAsync(
        Guid orgId,
        UsageBatch batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (await FirstIssuedPeriodAsync(orgId, batch.Rows, cancellationToken).ConfigureAwait(false) is { } issued)
        {
            throw new LedgerPeriodIssuedException(issued);
        }

        var inserted = await InsertAsync(orgId, batch.Rows, cancellationToken).ConfigureAwait(false);
        var duplicates = batch.DuplicateBillingKeys(inserted.ToHashSet(StringComparer.Ordinal));

        if (duplicates.Count > 0)
        {
            await RecordDuplicateAttemptsAsync(orgId, duplicates, cancellationToken).ConfigureAwait(false);
        }

        return inserted;
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
    /// Inserts <paramref name="rows"/> for <paramref name="orgId"/> and answers the billing keys the statement inserted;
    /// a key an existing row already holds is skipped by the conflict clause and is therefore absent from the answer.
    /// The rows are written in billing-key order whatever the request order: every batch takes the unique-index locks in
    /// one global order, so two concurrent batches with overlapping keys cannot deadlock (40P01), and the answer is
    /// mapped back by key rather than by position.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope (never a value from the batch).</param>
    /// <param name="rows">The rows to insert; empty writes nothing.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="LedgerPeriodIssuedException">
    /// A period of <paramref name="rows"/> was issued after the caller's read and before this insert.
    /// </exception>
    internal async Task<IReadOnlyList<string>> InsertAsync(
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

        return await ExecuteInsertAsync(command, rows, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the batch insert and answers the keys the statement inserted. Only the trigger's issued-period refusal is
    /// read here: it means a statement was issued after the caller's read and before this insert, which is the same
    /// refusal, decided by the database instead of by the read.
    /// </summary>
    /// <param name="command">The batch insert, on the caller's tenant transaction.</param>
    /// <param name="rows">The rows it carries, in request order.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="LedgerPeriodIssuedException">A period of <paramref name="rows"/> was issued in the meantime.</exception>
    private static async Task<List<string>> ExecuteInsertAsync(
        NpgsqlCommand command,
        IReadOnlyList<PendingUsageEntry> rows,
        CancellationToken cancellationToken)
    {
        var inserted = new List<string>(rows.Count);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                inserted.Add(reader.GetString(0));
            }
        }
        catch (PostgresException exception) when (IsIssuedPeriodRefusal(exception))
        {
            throw new LedgerPeriodIssuedException(IssuedPeriodOf(exception, rows));
        }

        return inserted;
    }

    /// <summary>
    /// The first period of <paramref name="rows"/>, in request order, that an issued statement already froze; null when
    /// every period is still open. One read inside the caller's transaction, taken before the insert so its refusal
    /// leaves that transaction usable. It cannot settle a race — a statement issued after this read is still unseen —
    /// which is what the table's trigger exists for.
    /// </summary>
    /// <param name="orgId">The tenant of the open scope (never a value from the batch).</param>
    /// <param name="rows">The rows of the batch, in request order.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async Task<string?> FirstIssuedPeriodAsync(
        Guid orgId,
        IReadOnlyList<PendingUsageEntry> rows,
        CancellationToken cancellationToken)
    {
        var periods = rows.Select(row => row.Period).Distinct(StringComparer.Ordinal).ToArray();
        if (periods.Length == 0)
        {
            return null;
        }

        await using var context = contexts.Create();
        await using var command = (NpgsqlCommand)context.Database.GetDbConnection().CreateCommand();
        command.CommandText = IssuedPeriodsSql;
        command.Transaction = (NpgsqlTransaction)scopes.Require().Transaction;
        command.Parameters.Add(new NpgsqlParameter("org_id", NpgsqlDbType.Uuid) { Value = orgId });
        command.Parameters.Add(new NpgsqlParameter("periods", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = periods });

        var issued = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                issued.Add(reader.GetString(0));
            }
        }

        return rows.Select(row => row.Period).FirstOrDefault(issued.Contains);
    }

    /// <summary>True for the ledger trigger's issued-period refusal and for no other check violation.</summary>
    /// <param name="exception">The database error of the insert.</param>
    private static bool IsIssuedPeriodRefusal(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.CheckViolation
        && string.Equals(exception.ConstraintName, IssuedPeriodConstraint, StringComparison.Ordinal);

    /// <summary>
    /// The period the refused row named: the trigger's message carries it because the refusal aborts the caller's
    /// transaction, so nothing can be re-read to ask which period was refused. A message naming no period falls back
    /// to the batch's first, the only period this side still knows.
    /// </summary>
    /// <param name="exception">The trigger's refusal.</param>
    /// <param name="rows">The rows of the refused batch, in request order.</param>
    private static string IssuedPeriodOf(PostgresException exception, IReadOnlyList<PendingUsageEntry> rows)
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
