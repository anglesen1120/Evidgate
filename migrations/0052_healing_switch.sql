-- 0052_healing_switch.sql
--
-- Owner: 08.6.3-T1 (feature plan 08.6.3 §3/§5, rulings T33 and T72, AC1/AC2/AC7/AC9). `healing_switch`
-- is the persisted kill switch of the healing subsystem: ONE row per scope, read once per healing
-- decision and resolved by "most restrictive wins" (`disabled` > `shadow` > `enabled`). The three
-- scopes are the operator's platform-wide default (`system`), a tenant override (`tenant`) and a
-- project override (`project`); a scope without a row contributes nothing to the resolution, and a
-- database with no rows at all resolves to `shadow` — the V1 posture of shadow healing that never
-- applies a fix (feature plan 08.6.3 §3, recorded here because the rule lives in the resolver, not in
-- this DDL).
--
-- WHY THREE SCOPES IN ONE TABLE. The alternative — a column per scope on `project` plus a settings
-- row — would spread the same vocabulary over two shapes and make "which value is in force" a
-- three-way read in application code. One row per scope keeps the resolution a single query over one
-- table (AC9) and keeps the operator's system row visible to every tenant without copying it.
--
-- WHY `org_id` IS NULLABLE. `scope = 'system'` is the platform operator's row: it belongs to no
-- tenant, so `org_id` is NULL — the same shape `public.role` and `public.role_permission` use for
-- their built-in rows (0010). It is therefore registered in `contracts/rls-exceptions.txt` as a
-- `catalogue` table: its read policy is the documented all-org read shape
-- (`org_id IS NULL OR org_id = evidgate.current_org_id()`) so that a tenant transaction reads the
-- system row it inherits, and its write policy is the plain tenant predicate, so a tenant can only
-- ever write its own rows. The boot self-check (`IsolationSelfCheck`, rule
-- `permissive_policy_unscoped`) judges exactly those two shapes; a single OR-shaped policy would be
-- refused as an unscoped permissive policy, which is why the read and the write policy are separate.
--
-- WHY THE SYSTEM ROW IS NOT WRITABLE FROM A TENANT TRANSACTION. The write policy's USING and WITH
-- CHECK are `org_id = evidgate.current_org_id()`, and a NULL `org_id` can never equal it: an app-role
-- UPDATE of the system row matches zero rows (it does not error) and the app role holds no DELETE
-- grant at all, so removing it is a 42501 rather than a silent no-op. An app-role INSERT of a
-- NULL-`org_id` row fails the WITH CHECK (42501). The only writer of the system row is the operator
-- definer below, which runs as `evidgate_owner` and reaches the row through the owner policy that
-- FORCE ROW LEVEL SECURITY would otherwise deny (`DECISIONS F5` rejects BYPASSRLS, 0004/0005
-- precedent).
--
-- COLUMNS. `at` is when this scope's mode was last set (the resolver reports it), and
-- `created_at`/`updated_at`/`version` are the 00 §7 audit columns plus the shared
-- `set_updated_at_and_version` trigger — the row is UPSERTed in place, so it needs them exactly as
-- 0030's `quarantine_entry` and 0033's `policy_activation` do. `set_by` is the operator (or, for a
-- tenant/project row, the member) who set the value; it carries no foreign key because the system row
-- may name a platform principal that no `membership` row describes.
--
-- CHECKS: `scope` and `mode` are closed vocabularies. `mode = 'unavailable'` is NOT storable — it is
-- a runtime observation of a dependency (`dependency_health`, T2's work), never a configured value,
-- so `healing_switch_mode_check` refuses it (AC2). The two cross-column CHECKs make the row's shape
-- unambiguous: a `system` row carries no `org_id`, a non-system row carries one, and only a `project`
-- row carries a `project_id`.
--
-- KEYS: `id` is the surrogate primary key (`unique (org_id, id)` is the composite-FK target every
-- business table declares, 00 §5.4.6, `scripts/check-rls.py` rule `missing_unique_key`), and three
-- PARTIAL unique indexes carry the real identity — one system row, one tenant row per org, one
-- project row per project. The indexes are also the resolution plan: the read is
-- `where scope = 'system' or (org_id = <org> and scope in ('tenant','project'))`, which uses them
-- (AC9; the resolver runs once per healing decision, so it must not scan the table).
--
-- The composite FK `(org_id, project_id) -> project (org_id, id)` is MATCH SIMPLE, so the NULL
-- `project_id` of a `system`/`tenant` row skips it, while a `project` row can only ever name a project
-- of its OWN org (the parallel defect of a plain `project_id -> project (id)` key is what the
-- isolation suite's composite-FK rules exist for). It cascades on delete, as the 08.6.3 shared
-- contract requires: a project row is hard-deleted only by the tenant-erasure path (06.4.4; the app
-- role holds no DELETE), and a project override must not survive its project and block that delete
-- with a 23503.
--
-- GRANTS. The app role reads (SELECT) and writes its own tenant/project rows: table-level INSERT plus
-- an UPDATE limited to the six columns a switch change re-stamps (`mode`, `reason`, `set_by`, `at`,
-- `updated_at`, `version`), so `id`, `scope`, `org_id` and `project_id` — the row's identity — stay
-- unwritable and no DELETE exists at all (M1W3-05, 0001's default privileges are revoked first, as
-- 0033:91 does). The operator reads through its SELECT grant (TL T76: an audited read with no
-- operator policy until 02.8.1's `evidgate.operator_grant_ok` lands), and the sweep role holds
-- nothing.
--
-- THE DEFINER. `evidgate.healing_switch_set_system` is the operator's only write path to the system
-- row (it cannot be reached from a tenant transaction, see above). It validates `p_mode` against the
-- three storable values (`'unavailable'` and anything else raise 22023
-- `invalid_parameter_value`), upserts the single `scope = 'system'` row (`insert` when absent,
-- `update` otherwise, so the shared trigger bumps `version` and `updated_at`) and RETURNs the stored
-- row so the route can answer exactly what the database now holds. A concurrent first write cannot
-- defeat it: the loser of the insert race re-runs the update on the `unique_violation`. It is
-- `SECURITY DEFINER` with a pinned empty `search_path` and every relation schema-qualified (0051
-- precedent); EXECUTE is granted to `evidgate_app` alone, which is the role the operator ingress runs
-- as (02.8.1's `RequireOperatorFilter` gate).
--
-- DEPLOY ORDER: MIGRATE BEFORE APP. The module, its wire routes and the operator ingress are new in
-- this story, so nothing binds to the table before this file is applied. Rollback: `drop function if
-- exists evidgate.healing_switch_set_system(text, text, uuid); drop table if exists
-- public.healing_switch;` and remove the `contracts/rls-exceptions.txt` line in the same change —
-- the table holds configuration only, so dropping it loses the operator's switch value and nothing
-- else.
--
-- Idempotent in the shape the README's file contract allows: `create table if not exists`,
-- `create unique index if not exists`, `drop policy if exists` / `drop trigger if exists` before each
-- `create`, `create or replace function`, and re-stated `revoke`/`grant`. One file runs in one
-- transaction, so the table, its policies and its definer are never observable apart.

create table if not exists public.healing_switch (
  id uuid not null,
  scope text not null,
  org_id uuid,
  project_id uuid,
  mode text not null,
  reason text,
  set_by uuid not null,
  at timestamptz not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint healing_switch_pkey primary key (id),
  constraint healing_switch_org_id_key unique (org_id, id),
  constraint healing_switch_project_fk
    foreign key (org_id, project_id) references public.project (org_id, id) on delete cascade,
  constraint healing_switch_scope_check check (scope in ('system', 'tenant', 'project')),
  constraint healing_switch_mode_check check (mode in ('enabled', 'shadow', 'disabled')),
  -- A `system` row is the operator's and belongs to no tenant; every other scope must name its org.
  constraint healing_switch_scope_org_check check ((scope = 'system') = (org_id is null)),
  -- Only a `project` row carries a project; `system` and `tenant` rows carry none.
  constraint healing_switch_scope_project_check check ((scope = 'project') = (project_id is not null)),
  -- The wire bound of the route (`reason` ≤ 2000), enforced by the database as the last line;
  -- an empty string is a stored "no reason given", not a violation.
  constraint healing_switch_reason_length_check check (reason is null or char_length(reason) <= 2000),
  constraint healing_switch_version_check check (version >= 1)
);

-- One row per scope. Partial unique indexes, not constraints: the identity of a `system`/`tenant` row
-- is its scope (plus org), and only one row of each may exist — the property the operator definer's
-- upsert and the resolver's `most restrictive` read both rely on.
create unique index if not exists healing_switch_system_key
  on public.healing_switch (scope)
  where scope = 'system';
create unique index if not exists healing_switch_tenant_key
  on public.healing_switch (org_id)
  where scope = 'tenant';
create unique index if not exists healing_switch_project_key
  on public.healing_switch (org_id, project_id)
  where scope = 'project';

-- The convention trigger of 0004: the database owns `updated_at` and the `version` increment, so a
-- switch change advances its optimistic-concurrency token whether the app role or the definer writes it.
drop trigger if exists healing_switch_set_updated_at_and_version on public.healing_switch;
create trigger healing_switch_set_updated_at_and_version
  before update on public.healing_switch
  for each row execute function public.set_updated_at_and_version();

alter table public.healing_switch enable row level security;
alter table public.healing_switch force row level security;

-- Read: the org's own rows plus the platform-wide system row it inherits — the all-org read shape of
-- a `catalogue` table (02 §4.5, the 0010 `role_catalogue_read` precedent). A tenant transaction
-- therefore resolves a switch with one SELECT, and the system row is never copied per tenant.
drop policy if exists healing_switch_app_read on public.healing_switch;
create policy healing_switch_app_read on public.healing_switch
  for select
  to evidgate_app
  using (org_id is null or org_id = evidgate.current_org_id());

-- Write: the org's own rows only. A NULL `org_id` never equals `evidgate.current_org_id()`, so the
-- system row is invisible to UPDATE/DELETE (zero rows, no error) and refused by INSERT (42501) —
-- the operator definer is the system row's only writer.
drop policy if exists healing_switch_app_write on public.healing_switch;
create policy healing_switch_app_write on public.healing_switch
  for all
  to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- The migration role needs its own policy: `force row level security` applies to the owner too, so
-- the definer below would otherwise write zero rows without failing (0005:80-90 precedent; the
-- alternative would be BYPASSRLS, which DECISIONS F5 rejects).
drop policy if exists healing_switch_owner_backfill on public.healing_switch;
create policy healing_switch_owner_backfill on public.healing_switch
  for all to evidgate_owner using (true) with check (true);

-- 0001's default privileges grant `evidgate_app` full DML on every table the migration role creates
-- in `public`, so the grants below would be additive no-ops without the revoke (0006/0009/0027/0033
-- precedent). After it the app role holds SELECT, table-level INSERT on the columns a switch row
-- needs, and UPDATE on exactly the six re-stamped columns: it can neither delete a switch nor rewrite
-- a row's identity, and it can never write a `system` row (the policy above refuses one).
revoke insert, update, delete on public.healing_switch from evidgate_app;
grant select on public.healing_switch to evidgate_app;
grant insert on public.healing_switch to evidgate_app;
grant update (mode, reason, set_by, at, updated_at, version) on public.healing_switch to evidgate_app;

-- TL T76: the operator's read is grant-gated with no operator policy until 02.8.1's
-- `evidgate.operator_grant_ok` lands, exactly as 0005:97 records for `project`; the sweep role holds
-- nothing, because it never heals anything.
grant select on public.healing_switch to evidgate_operator;
revoke all on public.healing_switch from evidgate_worker_sched;

-- The operator's write path to the system row. The tenant write path is a plain app-role UPDATE on
-- its own rows (no definer needed, 00 §5.5 C6 prefers a grant to a definer); only the tenant-less
-- `system` row cannot be reached that way, which is what makes this function necessary rather than
-- decorative.
--
-- The body's `returning` lists are qualified with the row alias `h` and the results land in `v_*`
-- locals: the RETURNS TABLE column names (`mode`, `reason`, `set_by`, `at`, `version`) are plpgsql
-- variables inside the body, so an unqualified reference would be ambiguous.
create or replace function evidgate.healing_switch_set_system(
  p_mode text,
  p_reason text,
  p_set_by uuid)
returns table (mode text, reason text, set_by uuid, at timestamptz, version integer)
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
  v_mode text;
  v_reason text;
  v_set_by uuid;
  v_at timestamptz;
  v_version integer;
begin
  -- `unavailable` is a dependency observation, never a storable mode (AC2); the refusal is the
  -- database's own, so no caller can widen the vocabulary by forgetting the route's validation.
  if p_mode is null or p_mode not in ('enabled', 'shadow', 'disabled') then
    raise exception 'healing switch mode % is not one of enabled, shadow, disabled', coalesce(p_mode, '(null)')
      using errcode = 'invalid_parameter_value';
  end if;

  -- The column CHECK's own bound, raised as a named refusal instead of a 23514 from a check the
  -- caller cannot read. `null` (no reason recorded) stays valid.
  if p_reason is not null and char_length(p_reason) > 2000 then
    raise exception 'healing switch reason is % characters, the maximum is 2000', char_length(p_reason)
      using errcode = 'invalid_parameter_value';
  end if;

  update public.healing_switch h
     set mode = p_mode,
         reason = p_reason,
         set_by = p_set_by,
         at = now()
   where h.scope = 'system'
  returning h.mode, h.reason, h.set_by, h.at, h.version
       into v_mode, v_reason, v_set_by, v_at, v_version;

  if not found then
    -- The create branch. A concurrent first write can slip between the update above and this insert;
    -- the partial unique `healing_switch_system_key` refuses the loser's row and it re-runs the
    -- update, so both calls answer the one stored value instead of one of them failing with 23505.
    begin
      insert into public.healing_switch as h (id, scope, org_id, project_id, mode, reason, set_by, at)
      values (uuidv7(), 'system', null, null, p_mode, p_reason, p_set_by, now())
      returning h.mode, h.reason, h.set_by, h.at, h.version
           into v_mode, v_reason, v_set_by, v_at, v_version;
    exception when unique_violation then
      update public.healing_switch h
         set mode = p_mode,
             reason = p_reason,
             set_by = p_set_by,
             at = now()
       where h.scope = 'system'
      returning h.mode, h.reason, h.set_by, h.at, h.version
           into v_mode, v_reason, v_set_by, v_at, v_version;
    end;
  end if;

  return query select v_mode, v_reason, v_set_by, v_at, v_version;
end
$$;

-- The built-in PUBLIC EXECUTE grant is closed, and EXECUTE stays with the one role the operator
-- ingress runs as (02.8.1's `RequireOperatorFilter` path). `evidgate_worker_sched` and
-- `evidgate_operator` are refused on purpose: the system row is set by an authenticated operator
-- request, never by a sweep or by the operator's own database login.
revoke all on function evidgate.healing_switch_set_system(text, text, uuid) from public;
grant execute on function evidgate.healing_switch_set_system(text, text, uuid) to evidgate_app;
