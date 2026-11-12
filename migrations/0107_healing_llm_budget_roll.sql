-- 0107_healing_llm_budget_roll.sql
--
-- The daily roll of the healing LLM budget. `healing_llm_budget` has one row per (project, period,
-- period_start) and the two-phase ledger keys every reserve and settle on the database's own UTC date, so a
-- new day finds no row and a call is refused as unprovisioned until something opens it. Until this file nothing
-- did: `evidgate_app` may not write the caps (0054) and `evidgate_worker_sched` holds no privilege on the table.
--
-- WHAT THE ROLL IS. `evidgate.roll_healing_llm_budget(p_day date)` inserts, for every project whose latest
-- earlier row exists and that has no row for the day, a new row with that row's caps and zero usage. It never
-- updates a row, so there is no in-place zeroing: a call that reserved on the previous day settles against the
-- previous day's row (`period_start` is part of the unique key and of the settle statement), so a roll under a
-- running call can neither erase its hold nor release it twice. A rerun is a no-op (`on conflict do nothing`).
-- The day is clamped to the server's UTC date, so a caller cannot open a future day. The function answers the
-- number of rows it opened.
--
-- WHO MAY RUN IT. It is `SECURITY DEFINER`, owner-owned, with an empty `search_path`; the owner reaches the table
-- through the `_owner_backfill` policy of 0054. EXECUTE is `evidgate_worker_sched`'s alone (the sweep role of
-- 0088 and 0104); PUBLIC and every other role hold nothing, so the Api's request path cannot call it. Every call
-- appends one `platform_sweep_audit` row (0088), because the roll spans every org and `audit_log` is per-org.
--
-- Growth: one row per project per day accrues from now on; a retention sweep for old budget rows is not part of
-- this file.
--
-- Idempotent: `create or replace function` and re-stated `revoke`/`grant`. One file runs in one transaction.
--
-- Rollback: `drop function if exists evidgate.roll_healing_llm_budget(date);` Rows it opened stay and are
-- harmless history.

set local lock_timeout = '5s';

create or replace function evidgate.roll_healing_llm_budget(p_day date)
returns integer
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
  v_day date := least(p_day, (now() at time zone 'utc')::date);
  v_opened integer;
begin
  insert into public.healing_llm_budget
    (id, org_id, project_id, period, period_start, period_cap_cost, period_cap_tokens)
  select gen_random_uuid(), prev.org_id, prev.project_id, 'daily', v_day,
         prev.period_cap_cost, prev.period_cap_tokens
    from (select distinct on (b.org_id, b.project_id)
                 b.org_id, b.project_id, b.period_cap_cost, b.period_cap_tokens
            from public.healing_llm_budget b
           where b.period = 'daily'
             and b.period_start < v_day
           order by b.org_id, b.project_id, b.period_start desc) prev
  on conflict (org_id, project_id, period, period_start) do nothing;

  get diagnostics v_opened = row_count;

  insert into public.platform_sweep_audit
    (actor, action, removed, batch_limit, budget_exhausted)
  values
    ('evidgate_worker_sched', 'healing.llm_budget_reset', v_opened, 1, false);

  return v_opened;
end
$$;

revoke all on function evidgate.roll_healing_llm_budget(date) from public;
grant execute on function evidgate.roll_healing_llm_budget(date) to evidgate_worker_sched;
