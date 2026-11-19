-- WHY: pre-tenant attempt budgets (login start/callback, explicit link, invite staging, CI exchange) lived in process
-- memory, so N replicas allowed N times the configured limit, and at the key bound a new client was refused while
-- everyone else's tracked keys kept their budget. One shared window per (surface, key) removes both defects.
--
-- SHAPE: one platform table (no org_id: it holds no customer business data), listed in contracts/rls-exceptions.txt
-- as `platform`. The key is stored only as an unsalted SHA-256 digest, which is pseudonymous rather than anonymous:
-- a low-entropy key (an address) can be re-derived by enumeration, so the digest is not a secret and expired rows
-- are swept instead of kept. It follows the 0100 posture: FORCE row level security with the owner-only
-- `*_owner_backfill` policy, every privilege revoked from the runtime roles, and one invoker-less definer in
-- `evidgate_resolve` that only `evidgate_app` may execute, so the app role can neither read nor forge a window.
--
-- evidgate_resolve.rate_limit_acquire(surface, key_hash, limit, window_seconds, max_keys) -> (allowed, retry_after)
--   * one upsert decides and increments in a single statement, so two replicas racing on one key cannot both observe
--     the same count; the window is reset by the database clock (`clock_timestamp()`), never a caller timestamp;
--   * the stored count is clamped at limit+1, which keeps the decision (`count <= limit`) overflow-free and lets a
--     refused caller be charged too, so a client at its limit cannot widen its budget by retrying;
--   * a full surface evicts instead of refusing: only the INSERT path of a new key does housekeeping, so a hit costs
--     one primary-key upsert. That path first deletes up to 100 EXPIRED windows of its own surface
--     (`window_start <= database now - window`, never the caller's row, `for update skip locked` in a
--     materialized batch, so the delete cannot rescan the locking subquery past the cap), then one new
--     key in 16 by digest (`get_byte(key_hash, 0) < 16`) runs the capacity check and deletes up to 100 of the
--     surface's oldest windows beyond max_keys. The bound is therefore approximate: a surface can sit above
--     max_keys by the new keys admitted between two sampled checks (a small multiple of the request concurrency
--     at that rate), and each sampled check removes the excess up to 100 rows, so the surface converges. Eviction
--     never refuses the arriving key, and surfaces never touch each other's rows.
--
-- lint: audit-deferred pre-tenant-budget evidgate_resolve.rate_limit_acquire
--
-- Rollback: roll the APPLICATION back first — an older build keeps its in-process windows and never calls the
-- function — then drop evidgate_resolve.rate_limit_acquire(text, bytea, integer, integer, integer) and the table.
-- Live budgets are lost: a client already at its limit gets a fresh window on the next call.
--
-- Deploy: migration first, then the application; an older application keeps its in-process windows until replaced.

set local lock_timeout = '5s';

create table if not exists public.rate_limit_window (
  surface      text not null,
  key_hash     bytea not null,
  window_start timestamptz not null,
  hit_count    integer not null,
  constraint rate_limit_window_pkey primary key (surface, key_hash),
  constraint rate_limit_window_surface_check check (char_length(surface) between 1 and 64),
  constraint rate_limit_window_key_hash_check check (octet_length(key_hash) = 32),
  constraint rate_limit_window_hit_count_check check (hit_count between 1 and 1000001)
);

-- Serves the bound check, the eviction order and the expired-window scan without touching the heap.
create index if not exists rate_limit_window_surface_window_start_idx
  on public.rate_limit_window (surface, window_start);

alter table public.rate_limit_window enable row level security;
alter table public.rate_limit_window force row level security;
drop policy if exists rate_limit_window_owner_backfill on public.rate_limit_window;
create policy rate_limit_window_owner_backfill on public.rate_limit_window
  for all
  to evidgate_owner
  using (true)
  with check (true);

revoke all on public.rate_limit_window from evidgate_app, evidgate_operator, evidgate_worker_sched;

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
declare
  v_now timestamptz := clock_timestamp();
  v_window interval := p_window_seconds * interval '1 second';
  v_expired_before timestamptz;
  v_start timestamptz;
  v_hits integer;
  v_inserted boolean;
  v_excess bigint;
begin
  -- limit has an upper bound because the clamped counter stores limit+1; max_keys has one because a sampled capacity
  -- check counts up to max_keys + 100 entries. A caller cannot ask for an endless budget or an unbounded scan.
  if p_surface is null or char_length(p_surface) not between 1 and 64
     or p_key_hash is null or octet_length(p_key_hash) <> 32
     or p_limit is null or p_limit not between 1 and 1000000
     or p_window_seconds is null or p_window_seconds not between 1 and 86400
     or p_max_keys is null or p_max_keys not between 1 and 1000000 then
    raise exception 'rate_limit_acquire: invalid arguments' using errcode = '22023';
  end if;

  -- `window_start <= v_expired_before` is the same expiry test the upsert resets on, written so the
  -- (surface, window_start) index answers it as a range: a surface with no expired row costs one probe.
  v_expired_before := v_now - v_window;

  insert into public.rate_limit_window as w (surface, key_hash, window_start, hit_count)
  values (p_surface, p_key_hash, v_now, 1)
  on conflict (surface, key_hash) do update
    set window_start = case when w.window_start + v_window <= v_now then v_now else w.window_start end,
        hit_count = least(case when w.window_start + v_window <= v_now then 1 else w.hit_count + 1 end, p_limit + 1)
  -- `xmax = 0` is the documented way to tell the inserted row from the updated one in RETURNING.
  returning w.window_start, w.hit_count, (xmax = 0) into v_start, v_hits, v_inserted;

  if v_inserted then
    -- Every new key sheds up to 100 of its surface's expired windows; skip locked avoids waiting on admissions.
    -- Materialize the locking batch: a rescanned SKIP LOCKED subquery can select fresh rows during the delete.
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

    -- The capacity count scans up to max_keys + 100 index entries, so it runs on one new key in 16 rather than on
    -- every insert. `limit` caps that work and v_excess caps one call's deletes at 100; a later sampled check removes
    -- whatever excess remains, so the surface converges without ever refusing the arriving key.
    if get_byte(p_key_hash, 0) < 16 then
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

  return query
    select v_hits <= p_limit,
           case when v_hits <= p_limit then 0
                else greatest(1, ceil(extract(epoch from v_start + v_window - v_now))::integer)
           end;
end
$$;

revoke all on function evidgate_resolve.rate_limit_acquire(text, bytea, integer, integer, integer) from public;
grant execute on function evidgate_resolve.rate_limit_acquire(text, bytea, integer, integer, integer) to evidgate_app;
