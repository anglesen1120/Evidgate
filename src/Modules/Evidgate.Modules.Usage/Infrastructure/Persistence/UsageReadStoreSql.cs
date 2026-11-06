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
    /// <para>
    /// The join key is the all-zero project sentinel both sides normalise to, not <c>is not
    /// distinct from</c>: an org-level aggregate (<c>project_id is null</c> — support hours) has no
    /// equality under <c>=</c>, and the sentinel is the same expression the roll-up's unique key
    /// uses, so the comparison is one plain equality the planner can index. No real project can own
    /// the sentinel id, so it cannot fold two scopes together.
    /// </para>
    /// <para>
    /// Amount and currency use the roll-up's own rule: both are known only when every row of the
    /// scope is priced and the scope carries one currency. A partial sum is null on both sides, so
    /// the comparison never reports an agreement it did not observe.
    /// </para>
    /// <para>
    /// <c>limit @limit</c> bounds the row set; the caller asks for one row more than it reports, so
    /// "are there more scopes?" needs no count.
    /// </para>
    /// </remarks>
    internal const string Reconciliation =
        """
        with ledger as (
            select project_id,
                   coalesce(project_id, '00000000-0000-0000-0000-000000000000'::uuid) as project_key,
                   metric,
                   sum(quantity) as total_quantity,
                   count(*) as row_count,
                   case when count(*) = count(amount) and count(distinct currency) = 1
                        then sum(amount) end as total_amount,
                   case when count(*) = count(amount) and count(distinct currency) = 1
                        then min(currency) end as currency
              from public.usage_ledger_entry
             where org_id = @org_id
               and period = @period
             group by project_id, metric
        ), rollup as (
            select project_id,
                   coalesce(project_id, '00000000-0000-0000-0000-000000000000'::uuid) as project_key,
                   metric,
                   total_quantity,
                   row_count::bigint as row_count,
                   total_amount,
                   currency,
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
               ledger.total_amount as ledger_total_amount,
               ledger.currency as ledger_currency,
               rollup.total_quantity as rollup_total_quantity,
               rollup.row_count as rollup_row_count,
               rollup.total_amount as rollup_total_amount,
               rollup.currency as rollup_currency,
               rollup.computed_at as rollup_computed_at,
               coalesce(rollup.gap_flag, false) as gap_flag
          from ledger
          full join rollup
            on ledger.project_key = rollup.project_key
           and ledger.metric = rollup.metric
         order by metric, project_id nulls first
         limit @limit
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

    /// <summary>
    /// The current period's counters and roll-ups for one org: the organization-wide counter row per metric
    /// (<c>quota_counter.project_id</c> null), one row per project that has a counter or a roll-up in the period,
    /// and the roll-up total as the fallback for a scope the month has no counter for. A project filter adds that
    /// project as a scope even when it has no data, so the caller always reads its zero; a project the caller
    /// cannot see answers one marker row instead (<c>project_missing</c>), which the caller maps to the same 404
    /// the other Usage reads answer.
    /// </summary>
    /// <remarks>
    /// One statement, so a summary is one read inside the request's transaction. Every counter probe names the
    /// <c>(org_id, coalesce(project_id, zero), period, metric)</c> unique index expression and every roll-up probe
    /// names its own scope key, so both are index probes rather than table scans. The tenant total sums the period's
    /// roll-ups across every project (the org-wide cap covers all of them); a project total is that project's rows.
    /// </remarks>
    internal const string Summary =
        """
        with project_visible as (
            select @project_id is null
                or exists (
                    select 1
                    from public.project as p
                    where p.org_id = @org_id
                      and p.id = @project_id) as visible
        ),
        scopes as (
            select 'tenant'::text as scope,
                   null::uuid as project_id,
                   '00000000-0000-0000-0000-000000000000'::uuid as scope_key
            where (select visible from project_visible)
            union all
            select 'project'::text, named.project_id, named.project_id
            from (
                select c.project_id
                from public.quota_counter as c
                where c.org_id = @org_id
                  and c.period = @period
                  and c.project_id is not null
                  and (@project_id is null or c.project_id = @project_id)
                union
                select r.project_id
                from public.usage_rollup as r
                where r.org_id = @org_id
                  and r.period = @period
                  and r.project_id is not null
                  and (@project_id is null or r.project_id = @project_id)
                union
                select @project_id::uuid
                where @project_id is not null
                  and (select visible from project_visible)
            ) as named
        ),
        counters as (
            select s.scope, s.project_id, s.scope_key,
                   c.metric, c.used, c.quota_limit, c.warned_at, c.blocked_at
            from scopes as s
            join public.quota_counter as c
              on c.org_id = @org_id
             and c.period = @period
             and coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid) = s.scope_key
        ),
        project_rollups as (
            select s.project_id, r.metric, sum(r.total_quantity) as used
            from scopes as s
            join public.usage_rollup as r
              on r.org_id = @org_id
             and r.period = @period
             and coalesce(r.project_id, '00000000-0000-0000-0000-000000000000'::uuid) = s.scope_key
            where s.scope = 'project'
            group by s.project_id, r.metric
        ),
        tenant_rollups as (
            select r.metric, sum(r.total_quantity) as used
            from public.usage_rollup as r
            where r.org_id = @org_id
              and r.period = @period
              and (select visible from project_visible)
            group by r.metric
        ),
        rows as (
            select scope, project_id, metric, used, quota_limit, warned_at, blocked_at
            from counters
            union all
            select 'tenant'::text, null::uuid, t.metric, t.used,
                   null::numeric, null::timestamptz, null::timestamptz
            from tenant_rollups as t
            where not exists (
                select 1
                from counters as c
                where c.scope = 'tenant'
                  and c.metric = t.metric)
            union all
            select 'project'::text, p.project_id, p.metric, p.used,
                   null::numeric, null::timestamptz, null::timestamptz
            from project_rollups as p
            where not exists (
                select 1
                from counters as c
                where c.scope = 'project'
                  and c.scope_key = p.project_id
                  and c.metric = p.metric)
        )
        select scope, project_id, metric, used, quota_limit, warned_at, blocked_at,
               false as project_missing
        from rows
        union all
        select 'tenant'::text, null::uuid, null::text, null::numeric, null::numeric,
               null::timestamptz, null::timestamptz, true as project_missing
        where not (select visible from project_visible)
        """;
}
