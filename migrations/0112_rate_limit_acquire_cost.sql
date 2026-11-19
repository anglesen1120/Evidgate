-- WHY: three defects of the 0110 function. (1) A route that charges 2 units called it twice (two round trips).
-- (2) A refused flood still rewrote the already-refused row on every call (heap churn, WAL, a row lock). (3) The
-- 1-in-16 capacity check was chosen by the first byte of the key digest, which a caller steers by picking keys.
--
-- evidgate_resolve.rate_limit_acquire(surface, key_hash, limit, window_seconds, max_keys, cost) -> (allowed, retry_after)
--   * NEW 6-argument overload; the 5-argument function from 0110 is KEPT as a cost-1 wrapper over it. A running
--     application (old pod during the rollout, or after an application rollback) calls the 5-argument signature
--     and must keep working, and the wrapper means there is one copy of the decision logic, not two;
--   * cost is 1..16. The FIRST unit decides, exactly as two sequential calls did: the call is admitted when the window
--     is live with fewer than `limit` hits (or expired / absent), and an admitted call then spends all `cost` units
--     (clamped at limit+1, the stored ceiling of 0110). A refused call spends nothing more;
--   * a window that is live and already holds at least `limit` hits is refused with a plain index read: no row is
--     written or locked, so a refused flood costs no WAL. The refusal and retry-after are the ones the clamped upsert
--     gave (`limit` and `limit+1` both refuse); the stored count may now rest at `limit` instead of `limit+1`;
--   * the admit path is one upsert whose update is guarded by `expired or hit_count < limit`, so two replicas racing
--     on one key still cannot both be admitted past the limit; a racer the guard rejects is a refusal;
--   * capacity sampling is `random() < 1/16` on the insert path of a new key, independent of the key.
--
-- lint: audit-deferred pre-tenant-budget evidgate_resolve.rate_limit_acquire
--
-- Deploy ORDER: migration first, then the application. The application calls the 6-argument function; until it is
-- replaced the old build calls the 5-argument wrapper and gets the same decisions.
-- ROLLBACK: roll the APPLICATION back first (the 5-argument wrapper keeps serving it), then, if the database must
-- go back, drop the 6-argument function and recreate the 5-argument one from 0110 (create or replace in 0110 is not
-- re-run by the migrator, so paste its body). Live budgets survive: the table is not touched.

set local lock_timeout = '5s';

create or replace function evidgate_resolve.rate_limit_acquire(
  p_surface text,
  p_key_hash bytea,
  p_limit integer,
  p_window_seconds integer,
  p_max_keys integer,
  p_cost integer)
returns table (allowed boolean, retry_after_seconds integer)
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
  v_now timestamptz := clock_timestamp();
  v_window interval := p_window_seconds * interval '1 second';
  v_expired_before timestamptz;
  v_start timestamptz;
  v_hits integer;
  v_inserted boolean;
  v_excess bigint;
begin
  if p_surface is null or char_length(p_surface) not between 1 and 64
     or p_key_hash is null or octet_length(p_key_hash) <> 32
     or p_limit is null or p_limit not between 1 and 1000000
     or p_window_seconds is null or p_window_seconds not between 1 and 86400
     or p_max_keys is null or p_max_keys not between 1 and 1000000
     or p_cost is null or p_cost not between 1 and 16 then
    raise exception 'rate_limit_acquire: invalid arguments' using errcode = '22023';
  end if;

  v_expired_before := v_now - v_window;

  -- A live window already at its limit refuses with a read only: the budget is monotone inside a window, so a stale
  -- read can only be refused too, and nothing needs the row lock a conflicting upsert would take.
  select w.window_start, w.hit_count into v_start, v_hits
    from public.rate_limit_window w
   where w.surface = p_surface and w.key_hash = p_key_hash;

  if found and v_start > v_expired_before and v_hits >= p_limit then
    return query select false, greatest(1, ceil(extract(epoch from v_start + v_window - v_now))::integer);
    return;
  end if;

  insert into public.rate_limit_window as w (surface, key_hash, window_start, hit_count)
  values (p_surface, p_key_hash, v_now, least(p_cost, p_limit + 1))
  on conflict (surface, key_hash) do update
    set window_start = case when w.window_start <= v_expired_before then v_now else w.window_start end,
        hit_count = least(case when w.window_start <= v_expired_before then p_cost else w.hit_count + p_cost end,
                          p_limit + 1)
  where w.window_start <= v_expired_before or w.hit_count < p_limit
  -- `xmax = 0` is the documented way to tell the inserted row from the updated one in RETURNING.
  returning w.window_start, (xmax = 0) into v_start, v_inserted;

  if not found then
    -- A concurrent call filled the window between the read and the upsert: refuse, charging nothing.
    select w.window_start into v_start
      from public.rate_limit_window w
     where w.surface = p_surface and w.key_hash = p_key_hash;

    return query select false,
      case when v_start is null then 1
           else greatest(1, ceil(extract(epoch from v_start + v_window - v_now))::integer) end;
    return;
  end if;

  if v_inserted then
    with expired_windows as materialized (
      select e.key_hash
        from public.rate_limit_window e
       where e.surface = p_surface
         and e.key_hash <> p_key_hash
         and e.window_start <= v_expired_before
       limit 100
         for update skip locked
    )
    delete from public.rate_limit_window w
     using expired_windows e
     where w.surface = p_surface
       and w.key_hash = e.key_hash
       and w.key_hash <> p_key_hash
       and w.window_start <= v_expired_before;

    -- One new key in 16, chosen by the database's own random(), runs the bounded capacity check.
    if random() < 0.0625 then
      select count(*) - p_max_keys into v_excess
        from (select 1 from public.rate_limit_window w where w.surface = p_surface limit p_max_keys::bigint + 100) as bounded;

      if v_excess > 0 then
        with excess_windows as materialized (
          select e.key_hash
            from public.rate_limit_window e
           where e.surface = p_surface
             and e.key_hash <> p_key_hash
           order by e.window_start
           limit least(100::bigint, v_excess)
             for update skip locked
        )
        delete from public.rate_limit_window w
         using excess_windows e
         where w.surface = p_surface
           and w.key_hash = e.key_hash
           and w.key_hash <> p_key_hash;
      end if;
    end if;
  end if;

  return query select true, 0;
end
$$;

revoke all on function evidgate_resolve.rate_limit_acquire(text, bytea, integer, integer, integer, integer) from public;
grant execute on function evidgate_resolve.rate_limit_acquire(text, bytea, integer, integer, integer, integer) to evidgate_app;

-- The 0110 signature stays callable and delegates, so there is one copy of the logic and every running build gets the
-- refused-row and sampling fixes at once.
create or replace function evidgate_resolve.rate_limit_acquire(
  p_surface text,
  p_key_hash bytea,
  p_limit integer,
  p_window_seconds integer,
  p_max_keys integer)
returns table (allowed boolean, retry_after_seconds integer)
language plpgsql
volatile
security definer
set search_path = ''
as $$
begin
  return query
    select r.allowed, r.retry_after_seconds
      from evidgate_resolve.rate_limit_acquire(p_surface, p_key_hash, p_limit, p_window_seconds, p_max_keys, 1) as r;
end
$$;

revoke all on function evidgate_resolve.rate_limit_acquire(text, bytea, integer, integer, integer) from public;
grant execute on function evidgate_resolve.rate_limit_acquire(text, bytea, integer, integer, integer) to evidgate_app;
