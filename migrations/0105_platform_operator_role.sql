-- 0105_platform_operator_role.sql
--
-- The platform-wide healing kill switch gets its own login, `evidgate_platform_operator`, and
-- `evidgate_app` loses the one grant that let any Api request flip a switch every tenant inherits.
-- Until this file, 0052 handed EXECUTE on `evidgate.healing_switch_set_system` to the app role, so
-- code running inside one tenant's request reached the tenant-less `system` row with the definer's
-- owner rights, and the function's own tenant guard was the only barrier. After it, the writer is
-- reachable by a role no Api host holds, every call appends one row to
-- `public.platform_operator_audit`, and the switch is set from an operator's `psql` session outside
-- the request path.
--
-- THE MATRIX (least privilege; no role appears with a privilege it is not listed with).
--
--   | Role                      | Object                                        | Privileges                       |
--   | ------------------------- | --------------------------------------------- | -------------------------------- |
--   | evidgate_platform_operator | schema evidgate                               | USAGE                            |
--   | evidgate_platform_operator | evidgate.healing_switch_set_system(...)       | EXECUTE                          |
--   | evidgate_platform_operator | public.healing_switch                         | none                             |
--   | evidgate_platform_operator | public.platform_operator_audit                | none                             |
--   | evidgate_app              | evidgate.healing_switch_set_system(...)       | none (revoked; 0052 had EXECUTE) |
--   | evidgate_app              | public.platform_operator_audit                | none                             |
--   | evidgate_operator         | public.platform_operator_audit                | SELECT (grant-gated, no policy)  |
--   | evidgate_worker           | any object above                              | none                             |
--   | evidgate_worker_sched     | any object above                              | none                             |
--   | PUBLIC / other roles      | any object above                              | none                             |
--
-- WHY EACH PRIVILEGE, FROM THE STATEMENTS THAT NEED IT (nothing here is wider than the code):
--   * operator `USAGE` on schema `evidgate` — the credential exists to call one function, and a
--     function is reachable only through USAGE on its schema. It gets no USAGE on `public`: it
--     never names a public relation, because the function is `SECURITY DEFINER` and reads and
--     writes both tables as `evidgate_owner`.
--   * operator `EXECUTE` on the definer — that call is the role's whole purpose, and the function
--     keeps the tenant-transaction refusal, so the borrowed owner rights never act for one org.
--   * operator: no table privilege at all — a direct INSERT into `platform_operator_audit` would
--     be an unaudited way to write a trail row, and a direct UPDATE of `healing_switch` would
--     bypass the definer's guard. The unaudited path must not exist as a grant.
--   * app: EXECUTE revoked — the Api is not the platform operator (02.8.1's real operator auth is
--     still owed, and the route's own gate already denies every caller). A leaked app credential
--     must not reach the platform row.
--   * operator (`evidgate_operator`) SELECT with no policy — 0088's posture for
--     `platform_sweep_audit` and 0052:196-199's for `healing_switch`: the grant is in place and
--     FORCE row level security with no operator policy yields zero rows until the operator scope
--     lands. The read is never silently unfiltered.
--
-- WHY THE AUDIT TABLE HAS NO `org_id` (the same decision as `platform_sweep_audit`, 0088): the
-- system row belongs to no tenant, so the act that sets it has no tenant to name and no per-org
-- audit chain to append to — `public.audit_log.org_id` is NOT NULL and 0015 chains every row per
-- org. Forcing an org would take one tenant's chain lock for a platform act and misattribute it.
-- The table is therefore registered in `contracts/rls-exceptions.txt` as `platform`, carries no
-- customer business data, and names no tenant, record id or payload:
-- `actor, action, old_mode, new_mode, version, created_at`.
--
-- APPEND-ONLY, AT TWO LAYERS (0088's shape). A guard trigger refuses UPDATE and DELETE for every
-- statement that reaches a row (23514). FORCE row level security is the other layer: the owner
-- holds insert and select policies only, so an owner UPDATE or DELETE changes zero rows and the
-- trigger never fires for it. TRUNCATE is not granted to any runtime role; only the table owner
-- (the migration role) could run it, and it is not exposed to a stored path.
--
-- ONE WRITE PATH. The audit row is inserted inside `evidgate.healing_switch_set_system`, in the
-- same statement and transaction as the switch write, so a failed call (a refused tenant context,
-- an unstorable mode, an over-long reason) rolls back both and leaves no row. The actor is the
-- `p_set_by` the operator names; it is never taken from the table's current row, so a call
-- cannot re-attribute an earlier change. `old_mode` is NULL only when the call creates the row,
-- and the value is read under the row lock (`select … for update`) so a concurrent pair records
-- the value each call actually replaced.
--
-- THE DEFINER is re-created with the 0052 signature, validation and refusals unchanged (22023 for
-- a mode outside the three storable values and for a reason over 2000 characters, 23514 for a call
-- made inside a tenant transaction, the `unique_violation` retry on the create branch), plus the
-- audit row above. It stays `SECURITY DEFINER`, owned by the migration role, with `search_path`
-- pinned empty and every relation schema-qualified; `proconfig` keeps exactly that one setting.
--
-- LOCKS. `create table` takes no lock on an existing relation (nothing can hold a conflicting lock
-- on a table that does not exist yet) and `create trigger`/`create policy` lock only the new
-- table; `create or replace function` locks the function, not a table; `GRANT`/`REVOKE` take
-- ACCESS SHARE on each named table plus a catalogue update of its ACL, so no row is read,
-- rewritten or scanned and no writer of `healing_switch` is blocked. `lock_timeout` bounds every
-- wait, so a database where an operator connection holds a conflicting lock on the new table fails
-- this file with 55P03 (rerun at a quieter moment) instead of stalling the migrator's queue.
--
-- ROLLBACK (application first: no build may call the definer as the operator role once this is
-- undone, and the operator's psql access goes with it):
--   revoke execute on function evidgate.healing_switch_set_system(text, text, uuid)
--     from evidgate_platform_operator;
--   grant execute on function evidgate.healing_switch_set_system(text, text, uuid) to evidgate_app;
--   -- then re-create the function from 0052's body (git history) so no audit row is written;
--   drop trigger if exists platform_operator_audit_append_only on public.platform_operator_audit;
--   drop function if exists evidgate.platform_operator_audit_append_only();
--   drop table if exists public.platform_operator_audit;
--   revoke usage on schema evidgate from evidgate_platform_operator;
--   -- and only then, once no connection names it: drop role evidgate_platform_operator;
--   -- and remove the `platform` line from contracts/rls-exceptions.txt in the same change.
-- Reverting the two function grants is what restores the 0052 posture; the switch value itself is
-- untouched, and only the audit history is lost (the trail records acts on platform
-- configuration, and no tenant row depends on it).
--
-- DEPLOY ORDER: this file first (it creates the role and re-creates the definer), then the
-- bootstrap's `credentials` step (LOGIN + `EVIDGATE_PLATFORM_OPERATOR_PASSWORD`); from then on the
-- flip is a human `psql` session that logs in as `evidgate_platform_operator` and calls the definer,
-- so no host, route or script carries that connection string. The Api takes neither: its EXECUTE is
-- gone the moment this commits, so the route fails closed instead of writing the platform row.
--
-- Idempotent in the shape the README's file contract allows: the role is created behind
-- `pg_roles` (0001/0104 shape), `create table if not exists`, `create or replace function`,
-- `drop trigger if exists` / `drop policy if exists` before each `create`, and re-stated
-- `revoke`/`grant` statements. One file runs in one transaction, so the role, the table, its
-- policies and the definer are never observable apart.
set local lock_timeout = '5s';

-- The role itself, in 0001's shape: NOLOGIN (the bootstrap grants LOGIN and a password, never a
-- committed file) and NOINHERIT (a membership granted for convenience never silently adds
-- authority).
do $$
begin
  if not exists (select 1 from pg_catalog.pg_roles where rolname = 'evidgate_platform_operator') then
    create role evidgate_platform_operator nologin noinherit;
  end if;
end
$$;

-- Fail loudly on a role that would break the boundary, in 0001/0104's shape: a migration role
-- cannot correct SUPERUSER or BYPASSRLS (only a superuser may), so an environment that ships one
-- must not serve.
do $$
declare
  offenders text;
begin
  select string_agg(rolname || ' (' || concat_ws(', ',
           case when rolsuper then 'SUPERUSER' end,
           case when rolbypassrls then 'BYPASSRLS' end,
           case when rolcreatedb then 'CREATEDB' end,
           case when rolcreaterole then 'CREATEROLE' end,
           case when rolreplication then 'REPLICATION' end) || ')', '; ')
    into offenders
    from pg_catalog.pg_roles
   where rolname = 'evidgate_platform_operator'
     and (rolsuper or rolbypassrls or rolcreatedb or rolcreaterole or rolreplication);

  if offenders is not null then
    raise exception 'evidgate_platform_operator carries unsafe attributes (00 §5.4.3): %', offenders;
  end if;
end
$$;

-- Schema reach: USAGE on `evidgate` is all the role needs (it calls one function there), and it
-- gets none on `public`: every relation the call touches is reached as the function's owner.
revoke usage on schema public from evidgate_platform_operator;
revoke all on schema evidgate from evidgate_platform_operator;
grant usage on schema evidgate to evidgate_platform_operator;

-- The platform-scope trail. `id` is the surrogate key; `actor` is the operator the call names;
-- `action` is the closed statement family (`healing.switch.system_set`), bounded like
-- `platform_sweep_audit.action` so a renamed writer cannot store an essay; the two mode columns
-- share the switch vocabulary, so a trail row can never describe a value the switch cannot hold;
-- `version` is the row version the call produced, which answers "which change is this". `actor` is
-- a bare uuid with no foreign key for the reason 0052's `healing_switch.set_by` has none: the
-- operator the call names is a platform principal, and no `membership` row describes one.
create table if not exists public.platform_operator_audit (
  id uuid not null default gen_random_uuid(),
  actor uuid not null,
  action text not null,
  old_mode text,
  new_mode text not null,
  version integer not null,
  created_at timestamptz not null default now(),
  constraint platform_operator_audit_pkey primary key (id),
  constraint platform_operator_audit_action_check check (char_length(action) between 1 and 128),
  -- NULL is "this call created the row", the only state a switch row does not already have.
  constraint platform_operator_audit_old_mode_check
    check (old_mode is null or old_mode in ('enabled', 'shadow', 'disabled')),
  constraint platform_operator_audit_new_mode_check
    check (new_mode in ('enabled', 'shadow', 'disabled')),
  constraint platform_operator_audit_version_check check (version >= 1)
);

-- One guard for both forbidden commands, so no future grant can quietly turn the trail into a
-- mutable table. 23514 is the shape the other append-only tables raise (0064, 0072, 0081, 0088).
create or replace function evidgate.platform_operator_audit_append_only() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    raise exception 'platform_operator_audit is append-only: % is refused', tg_op
      using errcode = '23514';
  end
  $$;

revoke all on function evidgate.platform_operator_audit_append_only() from public;

drop trigger if exists platform_operator_audit_append_only on public.platform_operator_audit;
create trigger platform_operator_audit_append_only
  before update or delete on public.platform_operator_audit
  for each row execute function evidgate.platform_operator_audit_append_only();

alter table public.platform_operator_audit enable row level security;
alter table public.platform_operator_audit force row level security;

-- The definer below runs as the table owner, and FORCE row level security applies to the owner
-- too, so these two policies are what let the audit row be written and read by the owner path at
-- all. The alternative, BYPASSRLS, is what DECISIONS F5 rejects (0004/0005/0088 precedent).
drop policy if exists platform_operator_audit_owner_insert on public.platform_operator_audit;
create policy platform_operator_audit_owner_insert on public.platform_operator_audit
  for insert
  to evidgate_owner
  with check (true);

drop policy if exists platform_operator_audit_owner_read on public.platform_operator_audit;
create policy platform_operator_audit_owner_read on public.platform_operator_audit
  for select
  to evidgate_owner
  using (true);

-- No `TO evidgate_operator` policy on purpose (D1, 0088's posture): the operator's SELECT is
-- grant-gated, and with FORCE and no operator policy it reads zero rows until 02.8.1's operator
-- scope lands. The drop is the idempotency the file contract asks for: it also removes the policy
-- from a database where an earlier revision of this file created one.
drop policy if exists platform_operator_audit_operator_read on public.platform_operator_audit;

-- 0001's default privileges hand `evidgate_app` full DML and `evidgate_operator` SELECT on every
-- table the migration role creates in `public`, so the revokes below are load-bearing, not a
-- formality (0006/0009/0052/0088 precedent). After them the app role holds nothing (no policy
-- names it either), the operator holds the read grant and only that, and the platform operator
-- role holds no table privilege at all: its write goes through the definer.
revoke all on public.platform_operator_audit from public;
revoke all on public.platform_operator_audit from evidgate_app;
revoke all on public.platform_operator_audit from evidgate_worker;
revoke all on public.platform_operator_audit from evidgate_worker_sched;
revoke all on public.platform_operator_audit from evidgate_platform_operator;
revoke all on public.platform_operator_audit from evidgate_operator;
grant select on public.platform_operator_audit to evidgate_operator;

-- The definer, re-created with 0052's signature and refusals, plus the audit row. The tenant guard
-- stays first, before the argument checks and before any statement, and it leaves both tables
-- untouched; the operator's `psql` session runs on its own non-tenant connection, where the setting
-- is unset (NULL) and the call proceeds. 23514, not 42501: the caller holds EXECUTE, so no grant is
-- missing — what is wrong is the writer's context (0027:102-105 / 0030:164-167 shape).
--
-- The body's `returning` lists are qualified with the row alias `h` and land in `v_*` locals: the
-- RETURNS TABLE column names are plpgsql variables inside the body, so an unqualified reference
-- would be ambiguous. The pre-update mode is read under `for update`, which both serialises a
-- concurrent pair and gives the audit row the value this call actually replaced; the create
-- branch keeps 0052's `unique_violation` retry, and that retry re-reads the old mode because the
-- row it lost the race to is exactly the value it replaced.
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
  v_old_mode text;
begin
  if evidgate.current_org_id() is not null then
    raise exception 'healing switch: the system row is not writable inside a tenant transaction (org %)', evidgate.current_org_id()
      using errcode = '23514';
  end if;

  -- `unavailable` is a dependency observation, never a storable mode: the refusal is the
  -- database's own, so no caller can widen the vocabulary by forgetting its own validation.
  if p_mode is null or p_mode not in ('enabled', 'shadow', 'disabled') then
    raise exception 'healing switch mode % is not one of enabled, shadow, disabled', coalesce(p_mode, '(null)')
      using errcode = 'invalid_parameter_value';
  end if;

  if p_reason is not null and char_length(p_reason) > 2000 then
    raise exception 'healing switch reason is % characters, the maximum is 2000', char_length(p_reason)
      using errcode = 'invalid_parameter_value';
  end if;

  select h.mode
    into v_old_mode
    from public.healing_switch h
   where h.scope = 'system'
     for update;

  if v_old_mode is null then
    begin
      insert into public.healing_switch as h (id, scope, org_id, project_id, mode, reason, set_by, at)
      values (uuidv7(), 'system', null, null, p_mode, p_reason, p_set_by, now())
      returning h.mode, h.reason, h.set_by, h.at, h.version
           into v_mode, v_reason, v_set_by, v_at, v_version;
    exception when unique_violation then
      select h.mode
        into v_old_mode
        from public.healing_switch h
       where h.scope = 'system'
         for update;

      update public.healing_switch h
         set mode = p_mode,
             reason = p_reason,
             set_by = p_set_by,
             at = now()
       where h.scope = 'system'
      returning h.mode, h.reason, h.set_by, h.at, h.version
           into v_mode, v_reason, v_set_by, v_at, v_version;
    end;
  else
    update public.healing_switch h
       set mode = p_mode,
           reason = p_reason,
           set_by = p_set_by,
           at = now()
     where h.scope = 'system'
    returning h.mode, h.reason, h.set_by, h.at, h.version
         into v_mode, v_reason, v_set_by, v_at, v_version;
  end if;

  -- Same transaction as the switch write: a caller that fails earlier writes neither, and the
  -- trail can never describe a mode the switch did not reach. `v_old_mode` is NULL only on the
  -- create branch, so the trail says which call created the row and which replaced which value.
  insert into public.platform_operator_audit (actor, action, old_mode, new_mode, version)
  values (p_set_by, 'healing.switch.system_set', v_old_mode, v_mode, v_version);

  return query select v_mode, v_reason, v_set_by, v_at, v_version;
end
$$;

-- EXECUTE is the platform operator's alone. The app role's 0052 grant is revoked here, so the Api
-- cannot reach the platform row at all; the operator, worker and sweep roles are refused on
-- purpose: the system row is set by the operator's own login, never by a request, a sweep or a
-- read-only operator login.
revoke all on function evidgate.healing_switch_set_system(text, text, uuid)
  from public, evidgate_app, evidgate_operator, evidgate_worker, evidgate_worker_sched,
       evidgate_platform_operator;
grant execute on function evidgate.healing_switch_set_system(text, text, uuid)
  to evidgate_platform_operator;
