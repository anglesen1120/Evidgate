using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Npgsql;

namespace Evidgate.Modules.Usage.Infrastructure.Persistence;

/// <summary>
/// The roll-up recompute (plan §7): the set-based aggregate of one tenant's ledger into <c>usage_rollup</c>, in the
/// caller's open tenant transaction.
/// <para>
/// One <c>insert … select … group by</c> per org computes every scope of that org in one statement — a project's period
/// and metric, or the org's own row when the ledger entry carries no project — and the coalesce-project unique index of
/// 0047 is the <c>on conflict</c> arbiter, so a recompute updates an aggregate instead of appending a second one.
/// </para>
/// <para>
/// The update is conditional on the numbers actually differing, which is what keeps a closed period byte-identical
/// across fires: an unchanged aggregate keeps its <c>computed_at</c> and its <c>version</c>, so re-running a period can
/// never look like a rewrite. The gap flag is the exception that has to move on its own: a scope that had usage and now
/// has no ledger rows is flagged, and the flag clears when rows return even at equal totals.
/// </para>
/// </summary>
internal static class UsageRollupStore
{
    /// <summary>
    /// Advisory-lock namespace of the recompute (agent-code-rules D8). Two workers that reach one org serialise on
    /// <c>(namespace, org_id)</c>, so the <c>on conflict</c> update of the same rows never races itself; different orgs
    /// keep their own lock and proceed in parallel.
    /// </summary>
    private const int LockNamespace = 7412;

    private const string LockSql = "select pg_advisory_xact_lock(@namespace, hashtext(@org_id::text))";

    /// <summary>
    /// The aggregate of every period the ledger holds for the scope's tenant. Grouping happens on the stored
    /// <c>period</c>, so a late row for a closed month is recomputed by the same statement as the open month, and no
    /// period needs a clock-derived window. <c>currency</c> is taken only when every row of the scope agrees on one:
    /// mixed currencies are unknown, and an aggregate that guessed one would misstate a statement.
    /// </summary>
    private const string RecomputeSql = """
        insert into public.usage_rollup (
            id, org_id, project_id, period, metric, total_quantity, total_amount, currency, row_count,
            gap_flag, computed_at)
        select
            uuidv7(), @org_id, totals.project_id, totals.period, totals.metric, totals.total_quantity,
            totals.total_amount, totals.currency, totals.row_count, false, now()
        from (
            select
                l.project_id,
                l.period,
                l.metric,
                sum(l.quantity) as total_quantity,
                sum(l.amount) as total_amount,
                case when count(distinct l.currency) = 1 then min(l.currency) end as currency,
                count(*)::integer as row_count
            from public.usage_ledger_entry l
            where l.org_id = @org_id
            group by l.project_id, l.period, l.metric
        ) as totals
        where true
        on conflict (org_id, coalesce(project_id, '00000000-0000-0000-0000-000000000000'::uuid), period, metric)
        do update set
            total_quantity = excluded.total_quantity,
            total_amount = excluded.total_amount,
            currency = excluded.currency,
            row_count = excluded.row_count,
            gap_flag = false,
            computed_at = excluded.computed_at
        where usage_rollup.total_quantity is distinct from excluded.total_quantity
           or usage_rollup.total_amount is distinct from excluded.total_amount
           or usage_rollup.currency is distinct from excluded.currency
           or usage_rollup.row_count is distinct from excluded.row_count
           or usage_rollup.gap_flag
        """;

    /// <summary>
    /// Flags a stored aggregate whose scope no longer has a ledger row. The recompute cannot insert such a row (the
    /// aggregate exists only because rows once did), so the flag is the one place a divergence stays visible; it is set
    /// only while it is not already set, so a repeated fire changes nothing.
    /// </summary>
    private const string FlagGapsSql = """
        update public.usage_rollup r
        set gap_flag = true
        where r.org_id = @org_id
          and not r.gap_flag
          and not exists (
            select 1
            from public.usage_ledger_entry l
            where l.org_id = r.org_id
              and l.project_id is not distinct from r.project_id
              and l.period = r.period
              and l.metric = r.metric)
        """;

    /// <summary>
    /// Recomputes every roll-up scope of <paramref name="scope"/>'s tenant and flags the scopes whose ledger rows have
    /// disappeared. The statements run on the scope's own connection and transaction, so a caller's rollback removes
    /// them and no other tenant's rows are reachable.
    /// </summary>
    /// <param name="scope">The open tenant scope of the org to recompute.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async Task RecomputeAsync(ITenantScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (!scope.IsActive)
        {
            throw new InvalidOperationException("the roll-up recompute needs an open tenant scope");
        }

        await LockAsync(scope, cancellationToken).ConfigureAwait(false);
        await RecomputeAggregatesAsync(scope, cancellationToken).ConfigureAwait(false);
        await FlagGapsAsync(scope, cancellationToken).ConfigureAwait(false);
    }

    private static async Task LockAsync(ITenantScope scope, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(LockSql, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("namespace", LockNamespace);
        command.Parameters.AddWithValue("org_id", scope.OrgId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RecomputeAggregatesAsync(ITenantScope scope, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RecomputeSql, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("org_id", scope.OrgId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task FlagGapsAsync(ITenantScope scope, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(FlagGapsSql, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("org_id", scope.OrgId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
