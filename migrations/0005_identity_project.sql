-- 0005_identity_project.sql
--
-- Owner: 02.1.1-T1.2. `project` is a business table in the ordinary sense of
-- 00 §5.4.1: it carries `org_id`, and every later tenant table hangs off it
-- through the composite key `(org_id, id)` declared here (TL T3).
--
-- Two keys, two jobs:
--   * `project_pkey` on `id` keeps a UUIDv7 project id globally unique, because
--     the API addresses a project by id alone (`GET/PATCH /v1/projects/{p}`, 00
--     §9.3) and an id must never be ambiguous across orgs;
--   * `project_org_id_key` on `(org_id, id)` is the composite-FK target the
--     template of 0003_foundation_rls_probe.sql requires, so a child row
--     (`environment` from 05.1.2-T1, and every later project-scoped table) can
--     declare `foreign key (org_id, project_id) references project (org_id, id)`
--     and cannot be pointed at another tenant's project.
--
-- `unique (org_id, slug)` is the per-org onboarding key behind
-- 409 `slug_taken` (AC2); the same slug in another org is a different project.
--
-- `(org_id, status, created_at, id)` is the covering index of the keyset list
-- query `GET /v1/orgs/{o}/projects` (00 §5.7 P4; the query and its statement
-- budget are 02.1.1-T2's `ListProjectsQueryTests`).
--
-- Column ownership: `retention_days` and `monthly_run_quota` (02.1.1-T3) and
-- `default_environment_id`'s composite FK (05.1.2-T1) are NOT created here; both
-- arrive with their own migrations.

create table if not exists public.project (
  id uuid not null,
  org_id uuid not null,
  name text not null,
  slug text not null,
  status text not null default 'active',
  default_environment_id uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint project_pkey primary key (id),
  constraint project_org_id_key unique (org_id, id),
  constraint project_org_slug_key unique (org_id, slug),
  constraint project_organization_fk foreign key (org_id) references public.organization (id),
  constraint project_slug_format_check check (slug ~ '^[a-z0-9][a-z0-9-]{1,62}$'),
  constraint project_name_length_check check (char_length(name) between 1 and 128),
  constraint project_status_check check (status in ('active', 'suspended', 'deleted'))
);

-- The shared concurrency trigger of 0004 (M1W3-01): `updated_at = now()` and
-- `version = old.version + 1` on every UPDATE. One function for every business
-- table, one trigger name per table.
drop trigger if exists project_set_updated_at_and_version on public.project;
create trigger project_set_updated_at_and_version
  before update on public.project
  for each row
  execute function public.set_updated_at_and_version();

-- 00 §5.7 P4: the list query filters `org_id` and `status`, then walks the keyset
-- `(created_at, id)`; the leading `org_id` also serves the FK check when an
-- organization row is deleted.
create index if not exists project_org_status_created_id_idx
  on public.project (org_id, status, created_at, id);

alter table public.project enable row level security;
alter table public.project force row level security;

drop policy if exists project_app_tenant on public.project;
create policy project_app_tenant on public.project
  for all
  to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- Deliberately no `TO evidgate_operator` policy here (TL T76, D1): the base
-- migration does not grant the operator standing read access. The operator
-- reaches this table only through 02.8.1's grant-gated
-- `evidgate.operator_grant_ok` policy; until that lands the SELECT grant below
-- reads zero rows. (The project-scoped token's narrower scope is the
-- `project_scope_mismatch` check of 02.1.1-T2, 00 §9.1, not a row filter — the
-- registry row itself must stay readable for the project the token is scoped to.)

-- The migration role needs its own policy: `force row level security` applies to
-- the table owner too, so an expand/contract backfill or an owner SECURITY
-- DEFINER sweep would otherwise update zero rows without failing. Explicit and
-- permissive on purpose — the alternative would be BYPASSRLS, which DECISIONS F5
-- rejects.
drop policy if exists project_owner_backfill on public.project;
create policy project_owner_backfill on public.project
  for all
  to evidgate_owner
  using (true)
  with check (true);

-- M1W3-05 / 00 §5.4.3 least privilege: no hard DELETE for the app role here
-- either (0004 explains the `revoke` next to the omitted grant: 0001's default
-- privileges grant DELETE on every table owner creates in `public`).
grant select, insert, update on public.project to evidgate_app;
revoke delete on public.project from evidgate_app;
grant select on public.project to evidgate_operator;
revoke all on public.project from evidgate_worker_sched;
