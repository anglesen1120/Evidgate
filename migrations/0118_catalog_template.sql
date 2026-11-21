-- 0118_catalog_template.sql
--
-- WHY: a generated test project starts from a versioned template, and the template must be chosen by data rather than
-- by a code branch: `evidgate init` and the draft renderer read the registry, so a new template release is a migration
-- and the files stay attributable to the version that produced them.
--
-- SHAPE: `template` is a catalogue table on the platform exception list. A built-in row carries `org_id IS NULL` and is
-- read by every tenant; an org-owned row is visible only inside its org. A built-in row's NULL org makes
-- `unique (org_id, kind, name, version)` unable to stop a duplicate built-in (NULLs are distinct), so the partial
-- unique index below is the real guard for built-ins; the composite key stays for org rows and the surrogate key.
--
-- BODY: the template body lives outside the database and is addressed by `body_ref` (`sha256:<hex>`), the same
-- content-addressed form the draft carries, so an edited body cannot pass as the version a row names.
--
-- GRANTS: `evidgate_app` reads only. Rows are written by the migration and by the owner role; `active` flips only
-- through a migration, so the row has no updated_at and no version trigger. `evidgate_operator` keeps SELECT with no
-- operator policy. `evidgate_worker_sched` holds nothing: no scheduled job reads templates.
--
-- ROW SECURITY: ENABLE + FORCE with the all-org read policy for `evidgate_app` and the owner backfill policy. The app
-- role holds no write grant, so a write is refused by the privilege rather than only by the policy.
--
-- LOCKS and ORDER: a catalogue write on a new relation; `lock_timeout` bounds the DDL's wait. No seed rows: tests
-- insert their own rows as the owner, so a database reset starts from an empty catalogue. No down migration: dropping
-- the table would lose which template version produced a draft.
--
-- Idempotent in the shape the README's file contract allows: `create table if not exists`, `drop policy if exists`
-- before every policy, and re-stated `revoke`/`grant` statements.

set local lock_timeout = '5s';

create table if not exists public.template (
  id uuid not null,
  -- NULL is a built-in (platform) template; a non-null value is an org's own row.
  org_id uuid,
  kind text not null,
  -- The template's registry name inside its kind, e.g. `playwright-ts`.
  name text not null,
  version integer not null,
  -- Content-addressed reference to the packaged template files, never the body itself.
  body_ref text not null,
  conformance_rules jsonb not null,
  active boolean not null default true,
  created_at timestamptz not null default now(),
  constraint template_pkey primary key (id),
  constraint template_org_id_key unique (org_id, id),
  constraint template_org_kind_name_version_key unique (org_id, kind, name, version),
  constraint template_kind_check check (kind in ('web', 'api', 'unit', 'mobile')),
  constraint template_name_check check (
    char_length(name) between 1 and 64 and name ~ '^[a-z][a-z0-9-]*$'),
  constraint template_version_check check (version >= 1),
  constraint template_body_ref_check check (body_ref ~ '^sha256:[0-9a-f]{64}$'),
  -- Strict recursive descent counts both object members and array elements from root level zero.
  -- Rejecting level 33 keeps stored rules within the reader's JSON parser depth limit.
  constraint template_conformance_rules_check check (
    jsonb_typeof(conformance_rules) in ('array', 'object')
    and octet_length(conformance_rules::text) <= 65536
    and not jsonb_path_exists(conformance_rules, 'strict $.**{33 to last}'))
);

-- The real uniqueness of a built-in: rows whose `org_id` is NULL have distinct composite keys, so the composite
-- constraint above cannot see a duplicate. The partial index applies to those rows only.
create unique index if not exists template_builtin_kind_name_version_key
  on public.template (kind, name, version)
  where org_id is null;

-- The list query's filter: kind and `active`, in that order.
create index if not exists template_kind_active on public.template (kind, active);

-- The list query's keyset order (00 §9.1).
create index if not exists template_created_id_idx on public.template (created_at, id);

alter table public.template enable row level security;
alter table public.template force row level security;

drop policy if exists template_app_read on public.template;
create policy template_app_read on public.template
  for select to evidgate_app
  using (org_id is null or org_id = evidgate.current_org_id());

-- FORCE row level security applies to the owner too, so the migration role needs its own policy (0004/0071 shape).
drop policy if exists template_owner_backfill on public.template;
create policy template_owner_backfill on public.template
  for all to evidgate_owner using (true) with check (true);

-- The default privileges of 0001 hand `evidgate_app` full DML, so the write grants are revoked explicitly before the
-- read grant: a tenant must not be able to register or retire a template.
revoke insert, update, delete on public.template from evidgate_app;
grant select on public.template to evidgate_app;
grant select on public.template to evidgate_operator;
revoke all on public.template from evidgate_worker_sched;
