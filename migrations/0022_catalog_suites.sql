-- 0022_catalog_suites.sql
--
-- Owner: 03.2.1-T1.1. Versioned suites (feature plan 03.2.1 §4, 00 §8.10, TL T3/T46): a `suite_revision` pins what a
-- run must execute — members (case revision x variant), the dependency DAG between members, the shard strategy, the
-- retry policy and the secret-free environment requirements — content-hashed and immutable, superseded by
-- `revision_no + 1` (D4). Variants are content-addressed normalised parameter sets and never carry secrets (D5).
--
--   suite            the named, project-scoped line of revisions; `head_revision_id` is its newest revision.
--   suite_revision   one immutable version. Its only permitted change is the one-way status draft -> published, made
--                    when a run locks it (05.1.1); every other column, and every row of the tables below, is frozen.
--   variant          shared, append-only parameter sets, unique per (project, variant_hash).
--   suite_member     the revision's members; surrogate id (T46) so edges can reference them with composite FKs.
--   case_dependency  parent -> child edges between members of one revision.
--
-- Deviation from the plan's DDL, recorded in the feature plan: uniqueness of `revision_hash` is per suite, because two
-- suites may legitimately hold identical content and must not dedupe into each other's revision.

create table if not exists public.suite (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  name text not null,
  slug text not null,
  status text not null default 'active',
  head_revision_id uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint suite_pkey primary key (id),
  constraint suite_org_id_key unique (org_id, id),
  constraint suite_project_slug_key unique (org_id, project_id, slug),
  constraint suite_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint suite_status_check check (status in ('active', 'archived')),
  constraint suite_name_length_check check (char_length(name) between 1 and 128),
  constraint suite_slug_format_check check (slug ~ '^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$')
);

create index if not exists suite_list_idx on public.suite (org_id, project_id, created_at, id);

create table if not exists public.suite_revision (
  id uuid not null,
  org_id uuid not null,
  suite_id uuid not null,
  revision_no integer not null,
  revision_hash text not null,
  status text not null default 'draft',
  shard_strategy jsonb not null,
  retry_policy jsonb not null,
  environment_requirements jsonb not null default '{}',
  created_by uuid,
  created_at timestamptz not null default now(),
  constraint suite_revision_pkey primary key (id),
  constraint suite_revision_org_id_key unique (org_id, id),
  constraint suite_revision_no_key unique (org_id, suite_id, revision_no),
  constraint suite_revision_hash_key unique (org_id, suite_id, revision_hash),
  constraint suite_revision_suite_fk foreign key (org_id, suite_id) references public.suite (org_id, id),
  constraint suite_revision_status_check check (status in ('draft', 'published')),
  constraint suite_revision_no_check check (revision_no >= 1),
  constraint suite_revision_hash_check check (revision_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint suite_revision_json_check check (
    jsonb_typeof(shard_strategy) = 'object' and jsonb_typeof(retry_policy) = 'object'
    and jsonb_typeof(environment_requirements) = 'object'
    and octet_length(environment_requirements::text) <= 16384)
);

-- The head lookup and the diff walk revisions of one suite newest first (P4).
create index if not exists suite_revision_suite_idx on public.suite_revision (org_id, suite_id, revision_no desc);

-- suite <-> suite_revision would be a circular pair if `suite.head_revision_id` carried a foreign key. It deliberately
-- carries none (like `membership_role.role_id`, 0011): the test harness's table reset and any ordered bulk delete cannot
-- order a cycle without disabling triggers, and the column is written only by the revision-create command, in the same
-- transaction that inserts the revision it names. The head lookup falls back to the highest revision_no when it is null.

create table if not exists public.variant (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  variant_hash text not null,
  params jsonb not null,
  created_at timestamptz not null default now(),
  constraint variant_pkey primary key (id),
  constraint variant_org_id_key unique (org_id, id),
  constraint variant_hash_key unique (org_id, project_id, variant_hash),
  constraint variant_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint variant_hash_check check (variant_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint variant_params_check check (jsonb_typeof(params) = 'object' and octet_length(params::text) <= 8192)
);

create table if not exists public.suite_member (
  id uuid not null,
  org_id uuid not null,
  suite_revision_id uuid not null,
  case_revision_id uuid not null,
  variant_id uuid not null,
  ordinal integer not null,
  required boolean not null default true,
  created_at timestamptz not null default now(),
  constraint suite_member_pkey primary key (id),
  constraint suite_member_org_id_key unique (org_id, id),
  constraint suite_member_unique_key unique (suite_revision_id, case_revision_id, variant_id),
  constraint suite_member_revision_fk foreign key (org_id, suite_revision_id) references public.suite_revision (org_id, id),
  constraint suite_member_case_revision_fk foreign key (org_id, case_revision_id) references public.test_revision (org_id, id),
  constraint suite_member_variant_fk foreign key (org_id, variant_id) references public.variant (org_id, id),
  constraint suite_member_ordinal_check check (ordinal >= 0)
);

create index if not exists suite_member_order_idx on public.suite_member (suite_revision_id, ordinal);
create index if not exists suite_member_case_revision_idx on public.suite_member (org_id, case_revision_id);
create index if not exists suite_member_variant_idx on public.suite_member (org_id, variant_id);

create table if not exists public.case_dependency (
  id uuid not null,
  org_id uuid not null,
  suite_revision_id uuid not null,
  parent_member_id uuid not null,
  child_member_id uuid not null,
  created_at timestamptz not null default now(),
  constraint case_dependency_pkey primary key (id),
  constraint case_dependency_org_id_key unique (org_id, id),
  constraint case_dependency_unique_key unique (suite_revision_id, parent_member_id, child_member_id),
  constraint case_dependency_revision_fk foreign key (org_id, suite_revision_id) references public.suite_revision (org_id, id),
  constraint case_dependency_parent_fk foreign key (org_id, parent_member_id) references public.suite_member (org_id, id),
  constraint case_dependency_child_fk foreign key (org_id, child_member_id) references public.suite_member (org_id, id)
);

create index if not exists case_dependency_revision_idx on public.case_dependency (suite_revision_id);
create index if not exists case_dependency_parent_idx on public.case_dependency (org_id, parent_member_id);
create index if not exists case_dependency_child_idx on public.case_dependency (org_id, child_member_id);

-- The version trigger on the one table that is edited (name, status, head).
drop trigger if exists suite_set_updated_at_and_version on public.suite;
create trigger suite_set_updated_at_and_version
  before update on public.suite
  for each row execute function public.set_updated_at_and_version();

-- Immutability (C6, D4). A revision's content never changes; the single permitted edit is draft -> published.
-- Variants, members and edges are append-only for every role, so a published revision can never be reshaped by
-- touching its parts.
create or replace function evidgate.catalog_suite_immutable() returns trigger
  language plpgsql
  as $$
  begin
    if tg_table_name = 'suite_revision' and tg_op = 'UPDATE' then
      if old.status = 'draft' and new.status = 'published'
         and (new.id, new.org_id, new.suite_id, new.revision_no, new.revision_hash, new.shard_strategy,
              new.retry_policy, new.environment_requirements, new.created_by, new.created_at)
             is not distinct from
             (old.id, old.org_id, old.suite_id, old.revision_no, old.revision_hash, old.shard_strategy,
              old.retry_policy, old.environment_requirements, old.created_by, old.created_at) then
        return new;
      end if;
    end if;

    raise exception 'revision_immutable: % is append-only (%)', tg_table_name, tg_op
      using errcode = '23514';
  end
  $$;

revoke all on function evidgate.catalog_suite_immutable() from public;

drop trigger if exists suite_revision_immutable on public.suite_revision;
create trigger suite_revision_immutable
  before update or delete on public.suite_revision
  for each row execute function evidgate.catalog_suite_immutable();

drop trigger if exists variant_immutable on public.variant;
create trigger variant_immutable
  before update or delete on public.variant
  for each row execute function evidgate.catalog_suite_immutable();

drop trigger if exists suite_member_immutable on public.suite_member;
create trigger suite_member_immutable
  before update or delete on public.suite_member
  for each row execute function evidgate.catalog_suite_immutable();

drop trigger if exists case_dependency_immutable on public.case_dependency;
create trigger case_dependency_immutable
  before update or delete on public.case_dependency
  for each row execute function evidgate.catalog_suite_immutable();

-- RLS: the standard tenant policy plus the owner policy FORCE needs for backfills.
alter table public.suite enable row level security;
alter table public.suite force row level security;
alter table public.suite_revision enable row level security;
alter table public.suite_revision force row level security;
alter table public.variant enable row level security;
alter table public.variant force row level security;
alter table public.suite_member enable row level security;
alter table public.suite_member force row level security;
alter table public.case_dependency enable row level security;
alter table public.case_dependency force row level security;

drop policy if exists suite_app_tenant on public.suite;
create policy suite_app_tenant on public.suite for all to evidgate_app
  using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());
drop policy if exists suite_owner_backfill on public.suite;
create policy suite_owner_backfill on public.suite for all to evidgate_owner using (true) with check (true);

drop policy if exists suite_revision_app_tenant on public.suite_revision;
create policy suite_revision_app_tenant on public.suite_revision for all to evidgate_app
  using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());
drop policy if exists suite_revision_owner_backfill on public.suite_revision;
create policy suite_revision_owner_backfill on public.suite_revision for all to evidgate_owner using (true) with check (true);

drop policy if exists variant_app_tenant on public.variant;
create policy variant_app_tenant on public.variant for all to evidgate_app
  using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());
drop policy if exists variant_owner_backfill on public.variant;
create policy variant_owner_backfill on public.variant for all to evidgate_owner using (true) with check (true);

drop policy if exists suite_member_app_tenant on public.suite_member;
create policy suite_member_app_tenant on public.suite_member for all to evidgate_app
  using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());
drop policy if exists suite_member_owner_backfill on public.suite_member;
create policy suite_member_owner_backfill on public.suite_member for all to evidgate_owner using (true) with check (true);

drop policy if exists case_dependency_app_tenant on public.case_dependency;
create policy case_dependency_app_tenant on public.case_dependency for all to evidgate_app
  using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());
drop policy if exists case_dependency_owner_backfill on public.case_dependency;
create policy case_dependency_owner_backfill on public.case_dependency for all to evidgate_owner using (true) with check (true);

-- Grants: a suite is archived, never deleted; a revision may be inserted and (draft -> published) updated; the rest is
-- insert-only.
grant select, insert, update on public.suite to evidgate_app;
revoke delete on public.suite from evidgate_app;
grant select, insert, update on public.suite_revision to evidgate_app;
revoke delete on public.suite_revision from evidgate_app;
grant select, insert on public.variant, public.suite_member, public.case_dependency to evidgate_app;
revoke update, delete on public.variant, public.suite_member, public.case_dependency from evidgate_app;

grant select on public.suite, public.suite_revision, public.variant, public.suite_member, public.case_dependency to evidgate_operator;
revoke all on public.suite, public.suite_revision, public.variant, public.suite_member, public.case_dependency from evidgate_worker_sched;
