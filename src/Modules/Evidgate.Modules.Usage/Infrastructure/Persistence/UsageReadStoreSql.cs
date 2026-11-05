namespace Evidgate.Modules.Usage.Infrastructure.Persistence;

/// <summary>
/// The read SQL of the billing operator's two statements: one keyset page of
/// <c>usage_ledger_entry</c>, and the period reconciliation of the ledger against
/// <c>usage_rollup</c> plus the duplicate attempts recorded by the write path.
/// </summary>
/// <remarks>
/// Every statement carries the tenant twice — <c>org_id = @org_id</c> and the open scope's row
/// security — because a filter the caller names is never the tenant boundary. The columns stay
/// snake_case: <c>DefaultTypeMap.MatchNamesWithUnderscores</c> is on for the whole process, so the
/// row types map without per-column aliases.
/// </remarks>
internal static class UsageReadStoreSql
{
    /// <summary>
    /// One page of the org's ledger, oldest first, keyset on <c>(created_at, id)</c>: the row
    /// comparison is what makes a page boundary correct when a batch wrote several rows in the same
    /// instant, which a comparison on <c>created_at</c> alone would skip or repeat.
    /// </summary>
    internal const string LedgerPage =
        """
        select id, project_id, metric, quantity, unit, period, billing_key, occurred_at,
               price_ref, amount, currency, adjustment_of, reason, created_at
          from public.usage_ledger_entry
         where org_id = @org_id
           and (@period::text is null or period = @period)
           and (@metric::text is null or metric = @metric)
           and (@cursor_created_at::timestamptz is null
                or (created_at, id) > (@cursor_created_at, @cursor_id::uuid))
         order by created_at, id
         limit @limit
        """;

    /// <summary>
    /// The period's per-<c>(project, metric)</c> comparison. Both sides are aggregated in the
    /// database and joined here once: a full join is what makes a side that exists only in the
    /// ledger (<c>missing_rollup</c>) or only in the roll-up (<c>missing_ledger</c>) visible as a
    /// row instead of as an absence the caller would have to reconstruct.
    /// </summary>
    /// <remarks>
    /// <c>is not distinct from</c> is required, not stylistic: an org-level aggregate
    /// (<c>project_id is null</c> — support hours) has no equality under <c>=</c>, so the plain
    /// join would report every org-level metric as missing on both sides.
    /// </remarks>
    internal const string Reconciliation =
        """
        with ledger as (
            select project_id,
                   metric,
                   sum(quantity) as total_quantity,
                   count(*) as row_count
              from public.usage_ledger_entry
             where org_id = @org_id
               and period = @period
             group by project_id, metric
        ), rollup as (
            select project_id,
                   metric,
                   total_quantity,
                   row_count::bigint as row_count,
                   computed_at,
                   gap_flag
              from public.usage_rollup
             where org_id = @org_id
               and period = @period
        )
        select coalesce(ledger.project_id, rollup.project_id) as project_id,
               coalesce(ledger.metric, rollup.metric) as metric,
               ledger.total_quantity as ledger_total_quantity,
               ledger.row_count as ledger_row_count,
               rollup.total_quantity as rollup_total_quantity,
               rollup.row_count as rollup_row_count,
               rollup.computed_at as rollup_computed_at,
               coalesce(rollup.gap_flag, false) as gap_flag
          from ledger
          full join rollup
            on ledger.project_id is not distinct from rollup.project_id
           and ledger.metric = rollup.metric
         order by metric, project_id nulls first
        """;

    /// <summary>
    /// The duplicate-attempt rows of one period, newest attempt first. The attempt table has no
    /// period of its own (a key is deduped per org), so the period comes from the ledger row the
    /// key already names — the write path only reports a key as a duplicate once that row exists.
    /// </summary>
    internal const string DuplicateAttempts =
        """
        select d.billing_key, d.attempts, d.first_seen_at, d.last_seen_at
          from public.usage_duplicate_attempt d
          join public.usage_ledger_entry l
            on l.org_id = d.org_id
           and l.billing_key = d.billing_key
         where d.org_id = @org_id
           and l.period = @period
         order by d.last_seen_at desc, d.billing_key
         limit @limit
        """;
}
