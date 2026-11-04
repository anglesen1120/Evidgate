-- 0062_evidence_retention_policy.sql
--
-- WHY: evidence deletion needs one per-project answer to "how long may an artifact and its metadata live",
-- and that answer must be a stored setting rather than a constant, because the audit cycle differs per
-- customer. The row is optional: a project with no row is served the platform defaults and nothing is written,
-- so enabling the endpoint changes no existing project.
--
-- SHAPE: one settings row per project, upserted in place. `(org_id, project_id)` is the upsert key
-- (`on conflict (org_id, project_id) do update`), so two concurrent PUTs converge on one row instead of raising
-- a duplicate key. `unique (org_id, id)` is the composite-FK target every business table declares (the composite-FK target
-- every business table declares, checked by the RLS lint), and `(org_id, project_id) -> project (org_id, id)` is the
-- composite tenant FK: a row can only ever name a project of its own org.
--
-- CHECKS: `ck_retention_policy_sane` is a loose, permanent sanity invariant only — a positive artifact window
-- and a metadata window at least as long as it. The compliance floor and ceiling are deployment configuration
-- (`RetentionLimits`), validated by the application before any statement runs, so the owner can retune them
-- without a migration. `version >= 1` mirrors the shared trigger's contract (the shared convention trigger).
--
-- RLS: ENABLE + FORCE with a policy `to evidgate_app` over `evidgate.current_org_id()` (never raw
-- `current_setting`). Another org's row is invisible, so a cross-tenant project id reads no row at all and the
-- route answers the same 404 an unknown id gets. The owner policy exists because FORCE row level security
-- applies to the table owner too (the migration-role policy precedent); BYPASSRLS is rejected by the platform's security decisions.
--
-- GRANTS: 0001's default privileges grant `evidgate_app` full DML on every table the migration role creates in
-- `public`, so the table-level revoke below is required before the column grants (the column-grant precedent).
-- The app role then holds SELECT, INSERT on the six columns a policy row is made of, and UPDATE on exactly the
-- two values a change re-stamps plus the two the shared trigger owns: `id`, `org_id` and `project_id` are
-- unwritable after insert, and no DELETE exists, so a project can neither repoint its policy at another project
-- nor erase the row. The operator keeps its SELECT (an audited read), and the scheduler role holds nothing.
--
-- LOCKS: `set local lock_timeout` bounds the DDL's waits and turns a busy table into a failed migration the
-- operator retries, rather than a stall.
--
-- Idempotent in the shape the README's file contract allows: `create table if not exists` / `drop ... if
-- exists` / `create or replace` so a re-run is a no-op.

set local lock_timeout = '5s';

create table if not exists public.retention_policy (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  artifact_days integer not null,
  metadata_days integer not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint retention_policy_pkey primary key (id),
  constraint retention_policy_org_id_key unique (org_id, id),
  constraint retention_policy_org_project_key unique (org_id, project_id),
  constraint retention_policy_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint retention_policy_sane_check check (artifact_days > 0 and metadata_days >= artifact_days),
  constraint retention_policy_version_check check (version >= 1)
);

-- The convention trigger of 0004: the database owns `updated_at` and the `version` increment, so the upsert
-- never writes either and a retried PUT can never pin the version.
drop trigger if exists retention_policy_set_updated_at_and_version on public.retention_policy;
create trigger retention_policy_set_updated_at_and_version
  before update on public.retention_policy
  for each row execute function public.set_updated_at_and_version();

alter table public.retention_policy enable row level security;
alter table public.retention_policy force row level security;

drop policy if exists retention_policy_app_tenant on public.retention_policy;
create policy retention_policy_app_tenant on public.retention_policy
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists retention_policy_owner_backfill on public.retention_policy;
create policy retention_policy_owner_backfill on public.retention_policy
  for all to evidgate_owner using (true) with check (true);

revoke insert, update, delete on public.retention_policy from evidgate_app;
grant select on public.retention_policy to evidgate_app;
grant insert (id, org_id, project_id, artifact_days, metadata_days) on public.retention_policy to evidgate_app;
grant update (artifact_days, metadata_days, updated_at, version) on public.retention_policy to evidgate_app;
grant select on public.retention_policy to evidgate_operator;
revoke all on public.retention_policy from evidgate_worker_sched;
