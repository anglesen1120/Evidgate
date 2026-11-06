namespace Evidgate.Modules.Usage.Infrastructure.Persistence;

/// <summary>
/// The <c>quota_counter</c> statements, in one place so the gate and the raise path share the served text
/// instead of a copy.
/// <para>
/// Every statement names the scope through the sentinel <c>coalesce(project_id, …)</c> triple the unique
/// index uses, so one index probe answers it and an org-level counter (null project) is a plain equality
/// rather than an <c>is not distinct from</c> the planner cannot index.
/// </para>
/// </summary>
internal static class UsageQuotaGateSql
{
    /// <summary>
    /// The gate's first statement: one conditional increment that is the check, so concurrent batches
    /// serialise on the row lock and the last unit is granted once. Its second half answers any scope that
    /// exists when the increment matched nothing, so a call with no counter row (no configured limit) is
    /// answered — one round trip — by the same statement, and only a scope that exists with a counter row
    /// moves on to the refusal statement. <c>reserved</c> says which of the two the caller read.
    /// </summary>
    internal const string Reserve = """
        with reserved as (
            update public.quota_counter as c
            set used = c.used + @quantity,
                warned_at = case
                    when c.warned_at is null and c.used + @quantity >= c.quota_limit * c.warn_ratio
                        then now()
                    else c.warned_at
                end
            where c.org_id = @org_id
              and coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid)
                  = coalesce(@project_id::uuid, '00000000-0000-0000-0000-000000000000'::uuid)
              and c.period = @period
              and c.metric = @metric
              and c.used + @quantity <= c.quota_limit
            returning c.used, c.quota_limit, c.warn_ratio
        )
        select answer.used, answer.quota_limit, answer.warn_ratio, answer.reserved
        from (
            select used, quota_limit, warn_ratio, true as reserved from reserved
            union all
            select c.used, c.quota_limit, c.warn_ratio, false as reserved
            from public.quota_counter as c
            where c.org_id = @org_id
              and coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid)
                  = coalesce(@project_id::uuid, '00000000-0000-0000-0000-000000000000'::uuid)
              and c.period = @period
              and c.metric = @metric
              and not exists (select 1 from reserved)
        ) as answer
        """;

    /// <summary>
    /// The gate's second statement, for a call the increment did not grant. One conditional write settles
    /// the scope: it reserves when the row has room by then (an operator raise committed first, so the call
    /// is served and its usage is counted rather than lost) and stamps the first refusal when it has none.
    /// The stamp carries the capacity predicate, so a counter with room is never left blocked, and the
    /// verdict comes from the pre-update tuple (<c>old.*</c> in <c>returning</c>, the pinned PostgreSQL 18
    /// server), so a reservation the same statement made is reported as a reservation, never as a refusal.
    /// <para>
    /// A scope that already carries the stamp writes nothing: the second half of the statement answers its
    /// snapshot, so a repeated refusal never moves the row and cannot bump <c>version</c>. No row at all
    /// means the counter holds room after all, or is gone.
    /// </para>
    /// </summary>
    internal const string StampRefusal = """
        with settled as (
            update public.quota_counter as c
            set used = case
                    when c.used + @quantity <= c.quota_limit then c.used + @quantity
                    else c.used
                end,
                warned_at = case
                    when c.used + @quantity <= c.quota_limit
                         and c.warned_at is null
                         and c.used + @quantity >= c.quota_limit * c.warn_ratio
                        then now()
                    else c.warned_at
                end,
                blocked_at = case
                    when c.used + @quantity > c.quota_limit then coalesce(c.blocked_at, now())
                    else c.blocked_at
                end
            where c.org_id = @org_id
              and coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid)
                  = coalesce(@project_id::uuid, '00000000-0000-0000-0000-000000000000'::uuid)
              and c.period = @period
              and c.metric = @metric
              and (c.used + @quantity <= c.quota_limit or c.blocked_at is null)
            returning c.used, c.quota_limit, c.warn_ratio,
                old.used + @quantity > old.quota_limit as blocked
        )
        select answer.used, answer.quota_limit, answer.warn_ratio, answer.blocked
        from settled as answer
        union all
        select c.used, c.quota_limit, c.warn_ratio, true as blocked
        from public.quota_counter as c
        where c.org_id = @org_id
          and coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid)
              = coalesce(@project_id::uuid, '00000000-0000-0000-0000-000000000000'::uuid)
          and c.period = @period
          and c.metric = @metric
          and not exists (select 1 from settled)
        """;

    /// <summary>
    /// The raise: one upsert on the scope's unique key. A scope with no counter gains one holding no
    /// usage; an existing one moves only upwards, so a raise can never put the limit below what the period
    /// already used. Clearing <c>blocked_at</c> is the counter's state transition; <c>warned_at</c> stays,
    /// so a scope that once warned still says so.
    /// </summary>
    internal const string Raise = """
        insert into public.quota_counter as c (
            id, org_id, project_id, period, metric, used, quota_limit)
        values (uuidv7(), @org_id, @project_id, @period, @metric, 0, @quota_limit)
        on conflict (org_id, coalesce(project_id, '00000000-0000-0000-0000-000000000000'::uuid), period, metric)
        do update set
            quota_limit = excluded.quota_limit,
            blocked_at = case when excluded.quota_limit > c.used then null else c.blocked_at end
        where excluded.quota_limit > c.quota_limit
        returning c.id, c.used, c.quota_limit
        """;
}
