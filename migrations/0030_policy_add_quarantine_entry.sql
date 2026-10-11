-- 0030_policy_add_quarantine_entry.sql
--
-- Owner: 07.3.1-T2.1. `quarantine_entry`: a project-scoped, time-boxed exclusion of one catalog case
-- from gating while its failure stays visible (feature plan 07.3.1 §4/§5, 00 §8.6/§8.8, FR-07-9). An
-- entry names the `case_id` it covers and the `owner` (a membership) accountable for it, carries a
-- mandatory `reason` and `expires_at`, and moves through `active -> expired | removed` (07.3.1 §6):
-- both targets are terminal, and a correction is a new row, never a re-activation.
--
-- T23 split (why the critical-list check is NOT here): a case on the active policy revision's
-- critical-case list can never be quarantined — the write is refused `422 critical_case_not_quarantinable`
-- before any row is written (AC4). That check reads the project's `policy_revision` row with
-- `state = 'active'` and its `body->'critical_cases'` inside the SAME tenant transaction as the INSERT
-- (07.3.1 §8, T23), so there is no TOCTOU window; it is a data-dependent read of another table and
-- cannot be expressed as a table CHECK. The database owns only the state machine below.
--
-- Why `expired` is not writable by `evidgate_app`: the time-driven expiry is the 07.3.2-T2 sweeper's
-- job, and that sweeper runs as an owner-run function (a `security definer` sweep, so `current_user`
-- inside the guard is `evidgate_owner`, not `evidgate_app`). The app role can therefore never fabricate
-- an expiry, and the sweeper keeps its own audit trail — the same posture as `waiver` (0027).
--
-- `expires_at` must be in the future at INSERT. This is enforced by the API (07.3.1 §5, D5: "strictly
-- in the future"), not by the database: PostgreSQL cannot use `now()` in a CHECK (it is not immutable),
-- and the guard trigger below is the §6 state machine (BEFORE UPDATE OR DELETE) per the D3 contract, so
-- a BEFORE INSERT check would be a second function outside that contract. A past-`expires_at` row that
-- the sweeper has not yet swept is a legitimate `active` row that may still be removed (active ->
-- removed); creating a second entry for the same case while one is unswept is `409 quarantine_conflict`
-- (D2), cleared by the 07.3.2-T2 sweeper.
--
-- Column grants (why not the plain `select, insert, update` of 0024): the app role may move only `state`
-- — the one column the remove command writes — and may INSERT only the request columns, never `state`
-- (so it cannot insert an already-`removed`/`expired` row) and never the audit columns. `0001`'s default
-- privileges grant `evidgate_app` full DML on every table `evidgate_owner` creates in `public`, so the
-- table-level `revoke insert, update` below is required before the column-level grants (0027/0028
-- precedent). No DELETE: an entry is removed by the `active -> removed` transition, never hard-deleted.
--
-- Row security: the tenant policy is `for all to evidgate_app`, exactly like 0005/0020-0029. A policy
-- with no `to` clause applies to PUBLIC, which would let `evidgate_operator` (it holds the SELECT grant
-- below) set `evidgate.org_id` and read any org. No `TO evidgate_operator` policy is created here
-- (TL T76, D1): the grant-gated operator policy is 02.8.1's `evidgate.operator_grant_ok` form, and until
-- it lands the operator's SELECT grant reads zero rows.
--
-- Two BEFORE UPDATE triggers share this table. PostgreSQL fires same-timing triggers in name order, so
-- `quarantine_entry_guard` runs before `quarantine_entry_set_updated_at_and_version`: the guard judges
-- the caller's intent against the stored row first, then the convention trigger of 0004 owns
-- `updated_at`/`version`. The guard never inspects `updated_at`/`version`, so the reverse order would be
-- equally correct — the deliberate name keeps the validation ahead of the bookkeeping.
--
-- Idempotent like 0025/0027/0028: `create table if not exists` / `drop ... if exists` / `on conflict do
-- nothing` so a re-run is a no-op.

-- ---------------------------------------------------------------------------------------------------
-- Part 1: the catalogue permission (D1). `contracts/permissions.json` grants `policy:write` to
-- `release_approver` only, but 07.3.1 §5 and 00 §9.3 give quarantine writes to `test_maintainer`.
-- Granting `policy:write` to `test_maintainer` would also hand it waiver create/approve/revoke and
-- policy-revision/activation power (waiver power leaking), so the smallest safe change is ONE new
-- catalogue permission `quarantine:write`, granted to the built-in role `test_maintainer` ONLY.
-- `policy:write` stays `release_approver` only; reads stay `policy:read` (every member role, T58).
--
-- The migration runs as `evidgate_owner`, whose `permission_owner_backfill` / `role_permission_owner_backfill`
-- policies (0010) permit these writes under FORCE row level security.
-- `SeedMatrixTests.RolesMatchOverview` diffs the seeded rows against `contracts/permissions.json`, so the fields
-- below must equal that file's `quarantine:write` entry exactly (description, sensitive=false, reserved=false,
-- tenant_assignable=true).
--
-- The two inserts are wrapped in a `do` block tagged `$seed$` (the same marker 0010's seed block uses): the
-- catalogue suites (tests/Identity.IntegrationTests/CatalogueTestBase) truncate the catalogue tables between
-- tests and re-apply every migration's seed block, so a permission added by a later migration survives the reset.
-- ---------------------------------------------------------------------------------------------------

do $seed$
begin
  insert into public.permission (code, resource, action, description, sensitive, reserved, tenant_assignable)
  values ('quarantine:write', 'quarantine', 'write',
          $desc$Create or remove a quarantine entry; never for a case on the active policy's critical list (T23, 07.3.1).$desc$,
          false, false, true)
  on conflict (code) do nothing;

  insert into public.role_permission (id, org_id, role_id, permission_code)
  select uuidv7(), null, r.id, 'quarantine:write'
    from public.role r
   where r.org_id is null
     and r.code = 'test_maintainer'
  on conflict (role_id, permission_code) do nothing;
end
$seed$;

-- ---------------------------------------------------------------------------------------------------
-- Part 2: the `quarantine_entry` table (07.3.1 §4, D3).
-- ---------------------------------------------------------------------------------------------------

create table if not exists public.quarantine_entry (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  case_id uuid not null,
  owner uuid not null,
  reason text not null,
  state text not null default 'active',
  expires_at timestamptz not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint quarantine_entry_pkey primary key (id),
  constraint quarantine_entry_org_id_key unique (org_id, id),
  constraint quarantine_entry_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint quarantine_entry_case_fk foreign key (org_id, case_id) references public.test_case (org_id, id),
  constraint quarantine_entry_owner_fk foreign key (org_id, owner) references public.membership (org_id, id),
  constraint quarantine_entry_state_check check (state in ('active', 'expired', 'removed')),
  constraint quarantine_entry_reason_check check (char_length(reason) between 1 and 2000),
  constraint quarantine_entry_version_check check (version >= 1)
);

-- A case has at most one active quarantine at a time (07.3.1 §4): the partial unique index is what
-- makes two concurrent `CreateQuarantineEntryCommand` calls for the same case resolve to one `active`
-- row and one `409 quarantine_conflict` (D2), and it never blocks a new active row after the previous
-- one is `removed`/`expired`.
create unique index if not exists quarantine_entry_active_case_key
  on public.quarantine_entry (org_id, project_id, case_id)
  where state = 'active';

-- The expiry sweeper (07.3.2-T2) scans `state`/`expires_at` inside a project; the list keyset walks
-- `(created_at, id)` inside a project (P4). Both lead with `org_id, project_id`.
create index if not exists quarantine_entry_state_expiry_idx on public.quarantine_entry (org_id, project_id, state, expires_at);
create index if not exists quarantine_entry_project_created_idx on public.quarantine_entry (org_id, project_id, created_at, id);

-- The convention trigger of 0004: the database owns `updated_at` and the `version` increment. It fires
-- after `quarantine_entry_guard` (name order), so a refused update never reaches the bookkeeping.
drop trigger if exists quarantine_entry_set_updated_at_and_version on public.quarantine_entry;
create trigger quarantine_entry_set_updated_at_and_version
  before update on public.quarantine_entry
  for each row execute function public.set_updated_at_and_version();

-- The guard: append-only identity/content, the §6 state machine and the app-role expiry ban.
-- `set search_path = ''` and schema-qualified names only; not SECURITY DEFINER, so it runs as the writer.
create or replace function evidgate.quarantine_entry_guard() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if tg_op = 'DELETE' then
      raise exception 'quarantine_entry_immutable: a quarantine entry is never deleted'
        using errcode = '23514';
    end if;

    if new.id is distinct from old.id
       or new.org_id is distinct from old.org_id
       or new.project_id is distinct from old.project_id
       or new.case_id is distinct from old.case_id
       or new.owner is distinct from old.owner
       or new.reason is distinct from old.reason
       or new.expires_at is distinct from old.expires_at
       or new.created_at is distinct from old.created_at then
      raise exception 'quarantine_entry_immutable: a quarantine entry identity, owner, reason and expiry never change (UPDATE)'
        using errcode = '23514';
    end if;

    if new.state is distinct from old.state then
      if not ((old.state = 'active' and new.state = 'removed')
              or (old.state = 'active' and new.state = 'expired')) then
        raise exception 'quarantine_entry_state_invalid: % -> %', old.state, new.state
          using errcode = '23514';
      end if;

      if new.state = 'expired' and current_user = 'evidgate_app' then
        raise exception 'quarantine_entry_state_invalid: expired is written only by the 07.3.2-T2 sweeper, not by evidgate_app'
          using errcode = '23514';
      end if;
    end if;

    return new;
  end
  $$;

revoke all on function evidgate.quarantine_entry_guard() from public;

drop trigger if exists quarantine_entry_guard on public.quarantine_entry;
create trigger quarantine_entry_guard
  before update or delete on public.quarantine_entry
  for each row execute function evidgate.quarantine_entry_guard();

alter table public.quarantine_entry enable row level security;
alter table public.quarantine_entry force row level security;

drop policy if exists quarantine_entry_app_tenant on public.quarantine_entry;
create policy quarantine_entry_app_tenant on public.quarantine_entry
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- The migration role needs its own policy: `force row level security` applies to the table owner too,
-- so an expand/contract backfill or an owner SECURITY DEFINER sweep would otherwise update zero rows
-- without failing (0004/0005 precedent). Explicit and permissive on purpose — the alternative would be
-- BYPASSRLS, which DECISIONS F5 rejects.
drop policy if exists quarantine_entry_owner_backfill on public.quarantine_entry;
create policy quarantine_entry_owner_backfill on public.quarantine_entry
  for all to evidgate_owner using (true) with check (true);

-- The default privileges of 0001 grant `evidgate_app` full DML on every table `evidgate_owner` creates in
-- `public`, so the column-level grants below would be additive no-ops without this revoke (0006/0009/0027
-- precedent: revoke the table-level privilege, then grant the minimal column set). After it, the app role
-- holds table-level SELECT only, plus INSERT on the request columns and UPDATE on `state` — it can neither
-- insert an already-removed/expired entry nor rewrite identity/owner/reason/expiry.
revoke insert, update on public.quarantine_entry from evidgate_app;
grant select on public.quarantine_entry to evidgate_app;
grant insert (id, org_id, project_id, case_id, owner, reason, expires_at) on public.quarantine_entry to evidgate_app;
grant update (state) on public.quarantine_entry to evidgate_app;
revoke delete on public.quarantine_entry from evidgate_app;
grant select on public.quarantine_entry to evidgate_operator;
revoke all on public.quarantine_entry from evidgate_worker_sched;
