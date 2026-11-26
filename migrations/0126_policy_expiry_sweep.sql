-- 0126_policy_expiry_sweep.sql
--
-- The expiry of waivers and quarantine entries. A `waiver` (0027) and a `quarantine_entry` (0030) carry an
-- `expires_at`, and both guards refuse `expired` from `evidgate_app`: nothing could write that state, so a due
-- row stayed `approved` / `active` for ever (a due quarantine even kept blocking a new entry for its case).
--
-- Two `SECURITY DEFINER` functions, owner-owned with an empty `search_path`, close the gap without widening any
-- table grant: `evidgate_app` keeps its column-scoped UPDATE, and a raw `UPDATE ... SET state = 'expired'` from it
-- is still refused by the guards (current_user is the owner only inside these functions).
--
-- `evidgate.exception_expiry_org_ids(p_after, p_limit)` is the scheduled job's cross-org probe: the ids of the
-- orgs that hold at least one due row (an `approved` waiver or an `active` quarantine entry with
-- `expires_at <= now()`), as one keyset page in id order, the limit clamped to 1..1000. It answers org ids only.
-- EXECUTE is `evidgate_worker_sched`'s alone, like `usage_ledger_org_ids` (0076).
--
-- `evidgate.sweep_expired_exceptions(p_org_id, p_limit)` moves the due rows of ONE org to `expired` and returns
-- exactly the rows it moved (`kind` 'waiver' | 'quarantine', `id`, `project_id`, the stored `expires_at`), so the
-- caller can write one audit row per transition in the same transaction. It takes no time argument: the boundary
-- is the transaction's `now()`, so `expires_at = now()` is due and a caller cannot move the clock. It raises
-- 42501 unless `p_org_id` is the caller's own tenant (`evidgate.current_org_id()`, which must be set), so it can
-- never touch another org even with a forged argument. EXECUTE is `evidgate_app`'s alone: the sweep runs inside a
-- tenant transaction, which is what lets the state change and its audit rows commit or roll back together.
-- A `requested` waiver is never effective, so it is never swept; `approved -> expired` and `active -> expired` are
-- the transitions the guards allow. One limit covers both kinds: the oldest `expires_at` first, rows locked by
-- another transaction are skipped (`for update skip locked`) and picked up by the next call. Rows that were
-- locked but fell outside the limit stay locked until the caller's transaction ends.
--
-- Indexes: none are added. Both statements filter `org_id`, `state` and `expires_at`, which the existing
-- `waiver_state_expiry_idx` (0027) and `quarantine_entry_state_expiry_idx` (0030) cover as
-- `(org_id, project_id, state, expires_at)`; the probe reads them without an org prefix and the sweep with one.
-- Both tables hold human-made rows, so an hourly pass stays small, and a new partial index could take the plan
-- away from the per-project due queries those indexes are pinned for.
--
-- Idempotent: `create or replace function` and re-stated `revoke`/`grant`; a rerun of the sweep returns nothing.
-- One file runs in one transaction.
--
-- Locks: function DDL and `GRANT`/`REVOKE` take ACCESS SHARE plus a catalogue ACL update, under the 5 s
-- `lock_timeout`; the functions take row locks only when called.
--
-- Rollback: `drop function if exists evidgate.sweep_expired_exceptions(uuid, integer);` and
-- `drop function if exists evidgate.exception_expiry_org_ids(uuid, integer);`. Rows already expired stay expired
-- (the guards allow no way back).

set local lock_timeout = '5s';

create or replace function evidgate.exception_expiry_org_ids(p_after uuid, p_limit integer) returns setof uuid
  language sql
  stable
  security definer
  set search_path = ''
  as $$
    select due.org_id
      from (select w.org_id
              from public.waiver w
             where w.state = 'approved'
               and w.expires_at <= now()
               and w.org_id > coalesce(p_after, '00000000-0000-0000-0000-000000000000'::uuid)
            union
            select q.org_id
              from public.quarantine_entry q
             where q.state = 'active'
               and q.expires_at <= now()
               and q.org_id > coalesce(p_after, '00000000-0000-0000-0000-000000000000'::uuid)) due
     order by due.org_id
     limit least(greatest(coalesce(p_limit, 100), 1), 1000)
  $$;

create or replace function evidgate.sweep_expired_exceptions(p_org_id uuid, p_limit integer)
  returns table (kind text, id uuid, project_id uuid, expires_at timestamptz)
  language plpgsql
  volatile
  security definer
  set search_path = ''
  as $$
  #variable_conflict use_column
  declare
    v_limit integer := least(greatest(coalesce(p_limit, 100), 1), 1000);
  begin
    if p_org_id is null
       or evidgate.current_org_id() is null
       or p_org_id <> evidgate.current_org_id() then
      raise exception 'sweep_expired_exceptions: the org must be the caller''s own tenant'
        using errcode = '42501';
    end if;

    return query
    with due_w as materialized (
      select w.id, w.expires_at
        from public.waiver w
       where w.org_id = p_org_id and w.state = 'approved' and w.expires_at <= now()
       order by w.expires_at, w.id
       limit v_limit
         for update skip locked
    ), due_q as materialized (
      select q.id, q.expires_at
        from public.quarantine_entry q
       where q.org_id = p_org_id and q.state = 'active' and q.expires_at <= now()
       order by q.expires_at, q.id
       limit v_limit
         for update skip locked
    ), picked as materialized (
      select c.kind, c.id, c.expires_at
        from (select 'waiver'::text as kind, dw.id, dw.expires_at from due_w dw
              union all
              select 'quarantine'::text, dq.id, dq.expires_at from due_q dq) c
       order by c.expires_at, c.id
       limit v_limit
    ), expired_w as (
      update public.waiver t
         set state = 'expired'
        from picked p
       where p.kind = 'waiver' and t.id = p.id and t.org_id = p_org_id and t.state = 'approved'
      returning p.kind, t.id, t.project_id, p.expires_at
    ), expired_q as (
      update public.quarantine_entry t
         set state = 'expired'
        from picked p
       where p.kind = 'quarantine' and t.id = p.id and t.org_id = p_org_id and t.state = 'active'
      returning p.kind, t.id, t.project_id, p.expires_at
    )
    select ew.kind, ew.id, ew.project_id, ew.expires_at from expired_w ew
    union all
    select eq.kind, eq.id, eq.project_id, eq.expires_at from expired_q eq;
  end
  $$;

-- lint: audit-deferred expiry-sweep-caller-audit evidgate.exception_expiry_org_ids, evidgate.sweep_expired_exceptions
-- The projection spans orgs and `audit_log.org_id` is NOT NULL, so no audit row can belong to it (the 0076
-- precedent). The sweep returns the rows it moved and its only caller writes one `waiver.expire` /
-- `quarantine.expire` audit row per returned row in the same tenant transaction, so the per-call row lives there.
revoke all on function evidgate.exception_expiry_org_ids(uuid, integer) from public;
grant execute on function evidgate.exception_expiry_org_ids(uuid, integer) to evidgate_worker_sched;

revoke all on function evidgate.sweep_expired_exceptions(uuid, integer) from public;
grant execute on function evidgate.sweep_expired_exceptions(uuid, integer) to evidgate_app;
