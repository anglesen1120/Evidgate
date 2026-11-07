namespace Evidgate.Modules.Usage.Infrastructure.Persistence;

/// <summary>
/// The <c>quota_counter</c> statements and the transaction's billing month, in one place so the gate and the raise path
/// share the served text instead of a copy.
/// <para>
/// Every counter statement names a scope through the sentinel <c>coalesce(project_id, …)</c> triple the unique index
/// uses, so one index probe answers it and an org-level counter (null project) is a plain equality rather than an
/// <c>is not distinct from</c> the planner cannot index. A call that charges two scopes passes both keys in
/// <c>@scope_keys</c>, and every statement that reads more than one scope reads them in <c>array_position</c> order — the
/// caller's tenant row first — so the locks are taken in one order by every caller and two of them cannot deadlock.
/// </para>
/// </summary>
internal static class UsageQuotaGateSql
{
    /// <summary>
    /// One definition of "the month this call is gated in": the caller's own month when it named one, and the month of
    /// the open transaction otherwise, resolved inside the statement that reads the counters so a caller that leaves
    /// the month open needs no second round trip. Every row a statement returns carries it, so the caller names the
    /// same month in its next statement.
    /// </summary>
    private const string ResolvedPeriod =
        "coalesce(@period::text, to_char(transaction_timestamp() at time zone 'UTC','YYYY-MM'))";

    /// <summary>
    /// The UTC month stamped on rows created in the caller's transaction, beside the instant that month is measured
    /// from, so a reader of both names the month the transaction is in.
    /// </summary>
    internal const string CurrentPeriod = """
        select to_char(transaction_timestamp() at time zone 'UTC','YYYY-MM') as period,
               transaction_timestamp() as database_now
        """;

    /// <summary>
    /// Reads only existing refusing counters; the first scope is the tenant's. The instant is the transaction's own
    /// clock, the one that named the period, so a refusal's wait never contradicts the month it refuses in. A null
    /// <c>@period</c> is the transaction's own month, and every row answers the month it was read in.
    /// </summary>
    internal const string Peek = $"""
        select coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid) as scope_key,
               c.used, c.quota_limit, transaction_timestamp() as database_now, c.period
        from public.quota_counter as c
        where c.org_id = @org_id
          and c.period = {ResolvedPeriod}
          and c.metric = @metric
          and coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid) = any(@scope_keys)
          and c.used + @quantity > c.quota_limit
        order by array_position(@scope_keys, coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid))
        limit 1
        """;

    /// <summary>
    /// The gate's first statement: one conditional increment of every scope the call charges, which is the check, so
    /// concurrent batches serialise on the row locks and the last unit is granted once. The scopes are locked before
    /// any of them is written (<c>targets</c>), and the increment runs only when <em>every</em> locked scope still has
    /// room, so a refusal in one scope leaves the other exactly as it was.
    /// <para>
    /// A scope with no counter row is unlimited and reports no row of its own; a scope whose month has no counter but
    /// an earlier month did reports <c>needs_carry</c>, so the limit is carried into the period before the call is
    /// answered. A call that needs any carry locks and charges nothing at all (<c>targets</c> is empty): a caller that
    /// held the project's lock while it still needed the organization's row would deadlock against the caller that
    /// locked the organization's row first and waited for the project's. The carry therefore runs before any of these
    /// locks, and the one rerun after it is the only statement that can charge, so no scope is ever paid twice for one
    /// call. <c>warned_now</c> is the pre-update tuple against the post-update one, so it is true only for the
    /// statement that actually stamped <c>warned_at</c>: the call that owns the transition, and only it, emits the
    /// warning. A null <c>@period</c> is the transaction's own month, and every row of every branch answers it in
    /// <c>period</c>.
    /// </para>
    /// </summary>
    internal const string Reserve = $"""
        with missing as materialized (
            select p.id, k.scope_key, p.quota_limit, p.warn_ratio
            from unnest(@scope_keys) as k(scope_key)
            cross join lateral (
                select c.id, c.quota_limit, c.warn_ratio
                from public.quota_counter as c
                where c.org_id = @org_id
                  and coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid) = k.scope_key
                  and c.metric = @metric
                  and c.period < {ResolvedPeriod}
                order by c.period desc
                limit 1
            ) as p
            where not exists (
                select 1
                from public.quota_counter as c
                where c.org_id = @org_id
                  and c.period = {ResolvedPeriod}
                  and c.metric = @metric
                  and coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid) = k.scope_key
            )
            order by array_position(@scope_keys, k.scope_key)
        ),
        targets as materialized (
            select c.id, c.used, c.quota_limit, c.warn_ratio,
                coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid) as scope_key
            from public.quota_counter as c
            where not exists (select 1 from missing as m)
              and c.org_id = @org_id
              and c.period = {ResolvedPeriod}
              and c.metric = @metric
              and coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid) = any(@scope_keys)
            order by array_position(@scope_keys, coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid))
            for update
        ),
        decided as (
            select coalesce(bool_and(t.used + @quantity <= t.quota_limit), true) as room
            from targets as t
        ),
        incremented as (
            update public.quota_counter as c
            set used = c.used + @quantity,
                warned_at = case
                    when c.warned_at is null and c.used + @quantity >= c.quota_limit * c.warn_ratio
                        then now()
                    else c.warned_at
                end
            from decided as d
            where d.room
              and c.id in (select t.id from targets as t)
            returning c.id, c.used, c.quota_limit, c.warn_ratio,
                coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid) as scope_key,
                true as reserved,
                old.warned_at is null and c.warned_at is not null as warned_now
        )
        select i.id, i.scope_key, i.used, i.quota_limit, i.warn_ratio, i.reserved,
            false as needs_carry, i.warned_now, {ResolvedPeriod} as period
        from incremented as i
        union all
        select t.id, t.scope_key, t.used, t.quota_limit, t.warn_ratio, false as reserved,
            false as needs_carry, false as warned_now, {ResolvedPeriod} as period
        from targets as t
        where not exists (select 1 from incremented as i where i.id = t.id)
        union all
        select m.id, m.scope_key, 0::numeric, m.quota_limit, m.warn_ratio, false as reserved,
            true as needs_carry, false as warned_now, {ResolvedPeriod} as period
        from missing as m
        """;

    /// <summary>
    /// The carry-forward of one call's scopes: every scope whose month has no counter yet takes its latest earlier
    /// counter's limit and warning line, with no usage and no threshold stamp. One statement, its rows inserted in
    /// lock order (the organization's, then the project's) and before any of them is locked, so a rollover never holds
    /// one scope's lock while it needs another's. The scope key's <c>on conflict</c> is what makes concurrent first
    /// callers of the month converge on one row: the loser writes nothing and the caller's next statement reads the
    /// winner's row.
    /// </summary>
    internal const string CarryForward = """
        insert into public.quota_counter (id, org_id, project_id, period, metric, used, quota_limit, warn_ratio)
        select uuidv7(), @org_id,
            nullif(k.scope_key, '00000000-0000-0000-0000-000000000000'::uuid),
            @period, @metric, 0, p.quota_limit, p.warn_ratio
        from unnest(@scope_keys) as k(scope_key)
        cross join lateral (
            select c.quota_limit, c.warn_ratio
            from public.quota_counter as c
            where c.org_id = @org_id
              and coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid) = k.scope_key
              and c.metric = @metric
              and c.period < @period
            order by c.period desc
            limit 1
        ) as p
        where not exists (
            select 1
            from public.quota_counter as c
            where c.org_id = @org_id
              and c.period = @period
              and c.metric = @metric
              and coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid) = k.scope_key
        )
        order by array_position(@scope_keys, k.scope_key)
        on conflict (org_id, coalesce(project_id, '00000000-0000-0000-0000-000000000000'::uuid), period, metric)
        do nothing
        """;

    /// <summary>
    /// The gate's second statement, for a call the increment did not grant. One conditional write settles every scope
    /// the call charges: when a raise committed in between and they all have room by now, all of them are charged and
    /// the call is served; otherwise none is charged and the scopes without room get the first refusal stamp, while
    /// the scope that does have room is left untouched. The stamp carries the capacity predicate, so a counter with
    /// room is never left blocked, and the verdict comes from the pre-update tuple (<c>old.*</c> in
    /// <c>returning</c>, the pinned PostgreSQL 18 server), so a reservation the same statement made is reported as a
    /// reservation, never as a refusal.
    /// <para>
    /// A scope that already carries the stamp is not written: it is answered from the locked snapshot, so a repeated
    /// refusal cannot bump <c>version</c>. The stamp itself commits with the caller's transaction, so a caller that
    /// answers the refusal by rolling back (the ingest route's refused batch) discards it and
    /// <see cref="StampBlocked"/> writes it again in a scope of its own.
    /// </para>
    /// <para>
    /// Every row carries the transaction's own clock, the one that named the period, so the caller measures the wait
    /// until the next period from the same clock as the month it refuses in rather than from a host's clock.
    /// </para>
    /// </summary>
    internal const string Settle = """
        with targets as materialized (
            select c.id, c.used, c.quota_limit, c.warn_ratio,
                coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid) as scope_key
            from public.quota_counter as c
            where c.org_id = @org_id
              and c.period = @period
              and c.metric = @metric
              and coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid) = any(@scope_keys)
            order by array_position(@scope_keys, coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid))
            for update
        ),
        decided as (
            select coalesce(bool_and(t.used + @quantity <= t.quota_limit), true) as fits
            from targets as t
        ),
        settled as (
            update public.quota_counter as c
            set used = case when d.fits then c.used + @quantity else c.used end,
                warned_at = case
                    when d.fits and c.warned_at is null and c.used + @quantity >= c.quota_limit * c.warn_ratio
                        then now()
                    else c.warned_at
                end,
                blocked_at = case
                    when c.used + @quantity > c.quota_limit then coalesce(c.blocked_at, now())
                    else c.blocked_at
                end
            from decided as d
            where c.id in (select t.id from targets as t)
              and (d.fits or (c.used + @quantity > c.quota_limit and c.blocked_at is null))
            returning c.id, c.used, c.quota_limit, c.warn_ratio,
                coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid) as scope_key,
                old.used + @quantity > old.quota_limit as blocked,
                old.warned_at is null and c.warned_at is not null as warned_now
        )
        select s.id, s.scope_key, s.used, s.quota_limit, s.warn_ratio, s.blocked, s.warned_now,
            transaction_timestamp() as database_now
        from settled as s
        union all
        select t.id, t.scope_key, t.used, t.quota_limit, t.warn_ratio,
            t.used + @quantity > t.quota_limit as blocked, false as warned_now,
            transaction_timestamp() as database_now
        from targets as t
        where not exists (select 1 from settled as s where s.id = t.id)
        """;

    /// <summary>
    /// The durable stamp of a refusal whose caller rolled its transaction back: the scopes are locked in the same order
    /// as above and only a counter still without room and still unstamped is written, so a repeated refusal, a counter
    /// a raise has since freed and a scope with no counter row all cost the one statement and no version bump. The
    /// <c>blocked_at is null</c> predicate sits inside the locking sub-statement, so a repeated refusal locks no row
    /// at all rather than locking one it then leaves untouched.
    /// </summary>
    internal const string StampBlocked = """
        with blocked as materialized (
            select c.id
            from public.quota_counter as c
            where c.org_id = @org_id
              and c.period = @period
              and c.metric = @metric
              and coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid) = any(@scope_keys)
              and c.used + @quantity > c.quota_limit
              and c.blocked_at is null
            order by array_position(@scope_keys, coalesce(c.project_id, '00000000-0000-0000-0000-000000000000'::uuid))
            for update
        )
        update public.quota_counter as c
        set blocked_at = now()
        from blocked as b
        where c.id = b.id
          and c.blocked_at is null
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
