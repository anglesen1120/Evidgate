-- 0088_platform_sweep_audit.sql
--
-- Every call of an owner sweep is audited here when the sweep is cross-tenant
-- platform work. `public.audit_log` cannot hold such a row: its `org_id` is NOT
-- NULL and every row of it is hash-chained per org (0015), so a purge that spans
-- every tenant has no org to name and forcing one would take N per-org chain
-- locks for a delete that touches N orgs. The platform-scope record therefore
-- lives in its own append-only table, the same decision shape `organization` and
-- the platform identity tables already use: no `org_id`, no customer business
-- data, and no privilege at all for `evidgate_app`.
--
-- WHAT A ROW IS. One row per sweep fire, written by the handler after its drain
-- loop ends and before the call returns: the action (`idempotency.purge`), the
-- number of rows that fire removed, the batch limit it used, whether the time
-- budget stopped the drain with rows still due, and when. It names no tenant, no
-- record id and no payload, so it cannot become a second, unprotected copy of
-- tenant data.
--
-- APPEND-ONLY, AT TWO LAYERS. An audit record that can be edited is not an audit
-- record, so a guard trigger refuses UPDATE and DELETE for every statement that
-- reaches a row. FORCE row level security is the other layer: the owner holds
-- insert and select policies only, so an UPDATE or DELETE as the owner changes
-- zero rows and the trigger never fires for it. TRUNCATE is revoked for every
-- runtime role as well; the only statement that empties the table is the test
-- harness's reset, which runs as the table owner and is not subject to row
-- triggers.
--
-- ONE WRITE PATH. Rows are inserted only by `evidgate.record_sweep_audit`, a
-- `SECURITY DEFINER` function owned by the migration role with a pinned empty
-- `search_path`. `evidgate_worker_sched` holds EXECUTE on that function and no
-- table privilege, so the actor column is stamped inside the function and a
-- caller cannot forge it; `evidgate_app` holds nothing at all and has no policy,
-- so even a leaked privilege would read zero rows. `evidgate_operator` holds
-- SELECT and no policy -- the grant-gated read posture 0052:196-199 records for
-- `healing_switch`: the grant is in place and the operator reads zero rows until
-- 02.8.1's operator scope lands; `evidgate_worker_sched` cannot read the trail
-- back.
--
-- Idempotent in the shape the README's file contract allows: `create table if not
-- exists`, `create or replace function`, `drop trigger if exists` / `drop policy
-- if exists` before each `create`, and re-stated `revoke`/`grant` statements. One
-- file runs in one transaction, so the table, its policies and its definer are
-- never observable apart.
--
-- Rollback: `drop trigger if exists platform_sweep_audit_append_only on
-- public.platform_sweep_audit; drop function if exists
-- evidgate.record_sweep_audit(text, integer, integer, boolean); drop table if exists
-- public.platform_sweep_audit; drop function if exists
-- evidgate.platform_sweep_audit_append_only();` -- the trigger and the table go
-- before the trigger function they depend on -- and remove the `platform` line
-- from `contracts/rls-exceptions.txt` in the same change. Only the sweep history
-- is lost; no tenant row depends on it.

create table if not exists public.platform_sweep_audit (
  id uuid not null default gen_random_uuid(),
  actor text not null,
  action text not null,
  removed integer not null,
  batch_limit integer not null,
  budget_exhausted boolean not null,
  created_at timestamptz not null default now(),
  constraint platform_sweep_audit_pkey primary key (id),
  -- The action is a sweep's handler key (`idempotency.purge`). The definer
  -- accepts any 1-128 character string -- it cannot know the handler keys -- and
  -- the only caller is the purge handler, passing its own task type.
  constraint platform_sweep_audit_action_check check (char_length(action) between 1 and 128),
  -- A negative count would read as a refund; the only producer is a delete loop.
  constraint platform_sweep_audit_removed_check check (removed >= 0),
  -- Below one, a batch limit would describe a call that asked for no rows at all;
  -- the handler clamps its payload into `[1, 10000]` before it calls.
  constraint platform_sweep_audit_batch_limit_check check (batch_limit >= 1)
);

-- One guard for both forbidden commands, so no future grant can quietly turn the
-- trail into a mutable table. 23514 is the same shape the other append-only
-- tables raise (0064, 0072, 0081).
create or replace function evidgate.platform_sweep_audit_append_only() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    raise exception 'platform_sweep_audit is append-only: % is refused', tg_op
      using errcode = '23514';
  end
  $$;

revoke all on function evidgate.platform_sweep_audit_append_only() from public;

drop trigger if exists platform_sweep_audit_append_only on public.platform_sweep_audit;
create trigger platform_sweep_audit_append_only
  before update or delete on public.platform_sweep_audit
  for each row execute function evidgate.platform_sweep_audit_append_only();

alter table public.platform_sweep_audit enable row level security;
alter table public.platform_sweep_audit force row level security;

-- The definer below runs as the table owner, and FORCE row level security applies
-- to the owner too, so these two policies are what let the audit row be written
-- and read by the owner path at all. The alternative, BYPASSRLS, is what
-- DECISIONS F5 rejects (0004/0005 precedent).
drop policy if exists platform_sweep_audit_owner_insert on public.platform_sweep_audit;
create policy platform_sweep_audit_owner_insert on public.platform_sweep_audit
  for insert
  to evidgate_owner
  with check (true);

drop policy if exists platform_sweep_audit_owner_read on public.platform_sweep_audit;
create policy platform_sweep_audit_owner_read on public.platform_sweep_audit
  for select
  to evidgate_owner
  using (true);

-- No `TO evidgate_operator` policy on purpose (D1, the same posture 0052:196-199
-- records for `healing_switch`): the operator's SELECT is grant-gated, and with
-- FORCE and no operator policy it reads zero rows until 02.8.1's operator scope
-- lands. The table carries no tenant dimension by construction, so the scope that
-- opens it is the grant, not an `org_id` predicate. The drop below is the
-- idempotency the file contract asks for: it also removes the policy from a
-- database where an earlier revision of this file created one.
drop policy if exists platform_sweep_audit_operator_read on public.platform_sweep_audit;

-- 0001's default privileges hand `evidgate_app` full DML and `evidgate_operator`
-- SELECT on every table the migration role creates in `public`, so the revokes
-- below are load-bearing, not a formality (0006/0009/0052 precedent). After them
-- the app role holds nothing — no policy names it either — the sweep role holds
-- no table privilege at all (its write goes through the definer), and the
-- operator holds the read grant and only that.
revoke all on public.platform_sweep_audit from public;
revoke all on public.platform_sweep_audit from evidgate_app;
revoke all on public.platform_sweep_audit from evidgate_worker_sched;
revoke all on public.platform_sweep_audit from evidgate_operator;
grant select on public.platform_sweep_audit to evidgate_operator;

-- The sweep's only write. The actor is stamped here, never accepted from the
-- caller, so a compromised or mis-wired sweep role cannot attribute an action it
-- did not perform. The return value is the new row's id, so a caller can prove
-- the write happened without being able to read the table.
create or replace function evidgate.record_sweep_audit(
  p_action text,
  p_removed integer,
  p_limit integer,
  p_budget_exhausted boolean)
returns uuid
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_id uuid;
begin
  insert into public.platform_sweep_audit
    (actor, action, removed, batch_limit, budget_exhausted)
  values
    ('evidgate_worker_sched', p_action, p_removed, p_limit, p_budget_exhausted)
  returning id into v_id;

  return v_id;
end
$$;

-- A new function created by the migration role starts owner-only (README "Roles
-- and credentials"), and the re-stated revoke keeps a future default from
-- widening it: EXECUTE is the sweep role's, and nobody else's.
revoke all on function evidgate.record_sweep_audit(text, integer, integer, boolean) from public;
grant execute on function evidgate.record_sweep_audit(text, integer, integer, boolean) to evidgate_worker_sched;
