-- 0027_policy_add_waiver.sql
--
-- Owner: 07.3.1-T1.1. `waiver`: a project-scoped, time-boxed exception that lets a case the active
-- policy revision would fail pass anyway (feature plan 07.3.1 §4/§5, 00 §8.8, FR-07-8). A waiver names
-- a `scope` ('case' or 'suite') and a `target_id`, carries a mandatory `reason` and `expires_at`, and
-- moves through `requested -> approved -> expired | revoked` (07.3.1 §6): `expired` and `revoked` are
-- terminal, and a correction is a new row, never a re-approval.
--
-- Column grants + guard (why not the plain `select, insert, update` of 0024): the app role must never be
-- able to write an already-approved waiver, rewrite identity/target/reason/expiry, or delete a row, so
-- `evidgate_app` holds only `insert` on the columns a request needs and `update (state, approved_by)` —
-- the two columns the approve/revoke commands move. Everything else (identity, `created_at`,
-- `updated_at`, `version`) is out of its reach at the privilege layer, and `evidgate.waiver_guard()`
-- enforces the same rules for every role that can write, including the owner. `expired` is refused for
-- `evidgate_app` in the guard: the time-driven expiry is the 07.3.2-T2 sweeper's job, and that sweeper
-- must run as an owner-run function (a `security definer` sweep, so `current_user` inside the guard is
-- `evidgate_owner`, not `evidgate_app`) — the app role can never fabricate an expiry, and the sweeper
-- keeps its own audit trail.
--
-- `requested_by`/`approved_by` are member ids with no foreign key (a member row can be removed while the
-- waiver stays, and the ids are audit facts) — same posture as `policy_revision.created_by` (0025).
--
-- Two BEFORE UPDATE triggers share this table. PostgreSQL fires same-timing triggers in name order, so
-- `waiver_guard` runs before `waiver_set_updated_at_and_version`: the guard judges the caller's intent
-- against the stored row first, then the convention trigger of 0004 owns `updated_at`/`version`. The
-- guard never inspects `updated_at`/`version`, so the reverse order would be equally correct — the
-- deliberate name keeps the validation ahead of the bookkeeping.
--
-- Idempotent like 0025: `create table if not exists` / `drop ... if exists` so a re-run is a no-op.

create table if not exists public.waiver (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  scope text not null,
  target_id uuid not null,
  reason text not null,
  requested_by uuid not null,
  approved_by uuid,
  state text not null default 'requested',
  expires_at timestamptz not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint waiver_pkey primary key (id),
  constraint waiver_org_id_key unique (org_id, id),
  constraint waiver_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint waiver_scope_check check (scope in ('case', 'suite')),
  constraint waiver_state_check check (state in ('requested', 'approved', 'expired', 'revoked')),
  constraint waiver_self_approval_check check (approved_by is null or approved_by <> requested_by),
  constraint waiver_approver_check check (state <> 'approved' or approved_by is not null),
  constraint waiver_reason_check check (char_length(reason) between 1 and 2000),
  constraint waiver_version_check check (version >= 1)
);

-- The expiry sweeper (07.3.2-T2) scans `state`/`expires_at` inside a project; the list keyset walks
-- `(created_at, id)` inside a project (P4). Both lead with `org_id, project_id`.
create index if not exists waiver_state_expiry_idx on public.waiver (org_id, project_id, state, expires_at);
create index if not exists waiver_project_created_idx on public.waiver (org_id, project_id, created_at, id);

-- The convention trigger of 0004: the database owns `updated_at` and the `version` increment. It fires
-- after `waiver_guard` (name order), so a refused update never reaches the bookkeeping.
drop trigger if exists waiver_set_updated_at_and_version on public.waiver;
create trigger waiver_set_updated_at_and_version
  before update on public.waiver
  for each row execute function public.set_updated_at_and_version();

-- The guard: append-only identity, the §6 state machine, the approver rules and the app-role expiry ban.
-- `set search_path = ''` and schema-qualified names only; not SECURITY DEFINER, so it runs as the writer.
create or replace function evidgate.waiver_guard() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if tg_op = 'DELETE' then
      raise exception 'waiver_immutable: a waiver is never deleted'
        using errcode = '23514';
    end if;

    if new.id is distinct from old.id
       or new.org_id is distinct from old.org_id
       or new.project_id is distinct from old.project_id
       or new.scope is distinct from old.scope
       or new.target_id is distinct from old.target_id
       or new.reason is distinct from old.reason
       or new.requested_by is distinct from old.requested_by
       or new.expires_at is distinct from old.expires_at
       or new.created_at is distinct from old.created_at then
      raise exception 'waiver_immutable: a waiver identity, target, reason and expiry never change (UPDATE)'
        using errcode = '23514';
    end if;

    if new.state is distinct from old.state then
      if not ((old.state = 'requested' and new.state = 'approved')
              or (old.state = 'requested' and new.state = 'revoked')
              or (old.state = 'approved' and new.state = 'expired')
              or (old.state = 'approved' and new.state = 'revoked')) then
        raise exception 'waiver_state_invalid: % -> %', old.state, new.state
          using errcode = '23514';
      end if;

      if new.state = 'expired' and current_user = 'evidgate_app' then
        raise exception 'waiver_state_invalid: expired is written only by the 07.3.2-T2 sweeper, not by evidgate_app'
          using errcode = '23514';
      end if;
    end if;

    -- `approved_by` moves only on requested -> approved; every other update must leave it alone.
    if new.approved_by is distinct from old.approved_by
       and not (old.state = 'requested' and new.state = 'approved') then
      raise exception 'waiver_state_invalid: approved_by changes only on requested -> approved'
        using errcode = '23514';
    end if;

    if old.state = 'requested' and new.state = 'approved' then
      if old.approved_by is not null or new.approved_by is null then
        raise exception 'waiver_state_invalid: requested -> approved needs a new approver on an unapproved waiver'
          using errcode = '23514';
      end if;

      if old.expires_at <= now() then
        raise exception 'waiver_state_invalid: requested -> approved needs an unexpired waiver'
          using errcode = '23514';
      end if;
    end if;

    return new;
  end
  $$;

revoke all on function evidgate.waiver_guard() from public;

drop trigger if exists waiver_guard on public.waiver;
create trigger waiver_guard
  before update or delete on public.waiver
  for each row execute function evidgate.waiver_guard();

alter table public.waiver enable row level security;
alter table public.waiver force row level security;

drop policy if exists waiver_app_tenant on public.waiver;
create policy waiver_app_tenant on public.waiver
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists waiver_owner_backfill on public.waiver;
create policy waiver_owner_backfill on public.waiver
  for all to evidgate_owner using (true) with check (true);

-- The default privileges of 0001 grant `evidgate_app` full DML on every table `evidgate_owner` creates in
-- `public`, so the column-level grants below would be additive no-ops without this revoke (0006/0009
-- precedent: revoke the table-level privilege, then grant the minimal column set). After it, the app role
-- holds table-level SELECT only, plus INSERT on the request columns and UPDATE on the two lifecycle
-- columns — it can neither insert an already-approved waiver nor rewrite identity/target/reason/expiry.
revoke insert, update on public.waiver from evidgate_app;
grant select on public.waiver to evidgate_app;
grant insert (id, org_id, project_id, scope, target_id, reason, requested_by, expires_at) on public.waiver to evidgate_app;
grant update (state, approved_by) on public.waiver to evidgate_app;
revoke delete on public.waiver from evidgate_app;
grant select on public.waiver to evidgate_operator;
revoke all on public.waiver from evidgate_worker_sched;
