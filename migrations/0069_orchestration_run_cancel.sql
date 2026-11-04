-- 0069_orchestration_run_cancel.sql
--
-- WHY: a cancel has to outlive the request that asked for it. The run turns `canceled` the moment the request is
-- accepted, but execution stops in two stages (soft stop, then kill at a deadline), and that has to survive an API or
-- worker restart — an in-memory timer would be lost with the process. `run_cancel_request` is the durable record of the
-- request and of the stage the stop has reached, one row per run.
--
-- SHAPE: one row per run, enforced by `unique (org_id, run_id)`; the handler's insert is `on conflict (org_id, run_id)
-- do nothing`, so a repeat cancel (a retry, a second operator) records nothing new and cannot open a second window.
-- `unique (org_id, id)` is the composite-FK target every business table declares (TL T3) and `(org_id, run_id) ->
-- run (org_id, id)` is the composite tenant FK (D3): a request can only ever name a run of its own org, so a
-- cross-tenant run id inserts nothing instead of overriding another org's run.
--
-- CHECKS: `state` is the closed local vocabulary of the execution stop — `requested`, `soft_stopping`, `soft_stopped`,
-- `killed` — and is NOT the run state machine (`run.state` is already `canceled` when this row exists); a new value is
-- a migration, like every other enum-like text column (D4). `reason` is caller free text, nullable, at most 1000
-- characters as written and not blank once trimmed: the route applies the same bound before any statement, and the
-- database check is the cheap half that keeps a writer skipping the route from storing text no screen can render.
-- `soft_stop_deadline_at >= requested_at and kill_at >= soft_stop_deadline_at` keeps the two windows in order whatever
-- writes the row (the route also computes them from configuration, in that order).
--
-- RLS: ENABLE + FORCE with a policy `to evidgate_app` over `evidgate.current_org_id()` (never raw `current_setting`):
-- another org's request is not a row at all, so a foreign run id reads and writes nothing and the route answers the same
-- 404 an unknown id gets. The owner policy exists because FORCE row level security applies to the migration role too
-- (0004/0028/0062/0066 precedent); BYPASSRLS is rejected by the platform's security decisions.
--
-- GRANTS: 0001's default privileges grant `evidgate_app` full DML on every table created in `public`, so the table-level
-- revoke below is required before the column grants (0062/0066 precedent). The app role then holds SELECT, INSERT on the
-- eight columns a request is made of, and UPDATE on `state` alone — the one column the soft-stop/kill stages move — so a
-- recorded request can never be repointed at another run, re-reasoned, or given a different window, and no DELETE exists.
-- The operator keeps its default SELECT (a decision recorded here, not a grant: it holds no policy on this table, so
-- until 02.8.1's grant-gated operator policy lands it reads zero rows, 0028's posture). The scheduler role holds nothing.
--
-- GUARD: `state` encodes a state machine the app may move, so it gets a trigger (D2) rather than trusting every future
-- writer: the row's identity, requester, reason and both deadlines are immutable, `requested` moves only forward
-- (`soft_stopping`, or straight to a stop when nothing was executing), `soft_stopping` moves to a stop, and a stopped
-- request (`soft_stopped`, `killed`) never changes again. The guard is an invoker, so it refuses an owner write too
-- without needing a definer grant.
--
-- LOCKS: `set local lock_timeout` bounds the DDL's waits and turns a busy table into a failed migration the operator
-- retries, rather than a stall.
--
-- Idempotent in the shape the README's file contract allows: `create table if not exists`, `drop ... if exists` before
-- every policy/trigger, and `create or replace function`, so a re-run is a no-op.

set local lock_timeout = '5s';

create table if not exists public.run_cancel_request (
  id uuid not null,
  org_id uuid not null,
  run_id uuid not null,
  requested_by uuid not null,
  reason text,
  requested_at timestamptz not null default now(),
  soft_stop_deadline_at timestamptz not null,
  kill_at timestamptz not null,
  state text not null default 'requested',
  constraint run_cancel_request_pkey primary key (id),
  constraint run_cancel_request_org_id_key unique (org_id, id),
  constraint run_cancel_request_run_key unique (org_id, run_id),
  constraint run_cancel_request_run_fk foreign key (org_id, run_id) references public.run (org_id, id),
  constraint run_cancel_request_state_check
    check (state in ('requested', 'soft_stopping', 'soft_stopped', 'killed')),
  constraint run_cancel_request_reason_check
    check (reason is null or (char_length(reason) between 1 and 1000 and btrim(reason) <> '')),
  constraint run_cancel_request_window_check
    check (soft_stop_deadline_at >= requested_at and kill_at >= soft_stop_deadline_at)
);

alter table public.run_cancel_request enable row level security;
alter table public.run_cancel_request force row level security;

drop policy if exists run_cancel_request_app_tenant on public.run_cancel_request;
create policy run_cancel_request_app_tenant on public.run_cancel_request
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- Deliberately no `TO evidgate_operator` policy (TL T76, D1): the grant-gated operator policy is 02.8.1's
-- `evidgate.operator_grant_ok` form; until it lands the operator's default SELECT reads zero rows.
drop policy if exists run_cancel_request_owner_backfill on public.run_cancel_request;
create policy run_cancel_request_owner_backfill on public.run_cancel_request
  for all to evidgate_owner using (true) with check (true);

revoke insert, update, delete, truncate on public.run_cancel_request from evidgate_app;
grant select on public.run_cancel_request to evidgate_app;
grant insert (id, org_id, run_id, requested_by, reason, requested_at, soft_stop_deadline_at, kill_at)
  on public.run_cancel_request to evidgate_app;
grant update (state) on public.run_cancel_request to evidgate_app;
grant select on public.run_cancel_request to evidgate_operator;
revoke all on public.run_cancel_request from evidgate_worker_sched;

-- The local stop-state machine (D2). Identity, requester, reason and both deadlines are facts of the request and never
-- move; a stop only moves forward, and a stopped request is final. Re-writing the value a row already holds is not a
-- change and stays allowed, so a replay of the same stage write is not an error.
create or replace function evidgate.run_cancel_request_guard() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if tg_op = 'DELETE' then
      raise exception 'run_cancel_request_immutable: a cancel request is never deleted'
        using errcode = '23514';
    end if;

    if new.id is distinct from old.id
       or new.org_id is distinct from old.org_id
       or new.run_id is distinct from old.run_id
       or new.requested_by is distinct from old.requested_by
       or new.reason is distinct from old.reason
       or new.requested_at is distinct from old.requested_at
       or new.soft_stop_deadline_at is distinct from old.soft_stop_deadline_at
       or new.kill_at is distinct from old.kill_at then
      raise exception 'run_cancel_request_immutable: the request, its requester and its windows never change (UPDATE)'
        using errcode = '23514';
    end if;

    if new.state is distinct from old.state then
      if not ((old.state = 'requested' and new.state in ('soft_stopping', 'soft_stopped', 'killed'))
              or (old.state = 'soft_stopping' and new.state in ('soft_stopped', 'killed'))) then
        raise exception 'run_cancel_request_state_invalid: % -> %', old.state, new.state
          using errcode = '23514';
      end if;
    end if;

    return new;
  end;
  $$;

revoke all on function evidgate.run_cancel_request_guard() from public;

-- The migration role owns it; stated so a re-run over a function some other role once created ends owner-owned too.
alter function evidgate.run_cancel_request_guard() owner to evidgate_owner;

drop trigger if exists run_cancel_request_guard on public.run_cancel_request;
create trigger run_cancel_request_guard
  before update or delete on public.run_cancel_request
  for each row execute function evidgate.run_cancel_request_guard();
