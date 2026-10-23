-- 0020_catalog_case_identity.sql
--
-- Owner: 03.1.1-T1.1. The stable identity of a test case (feature plan 03.1.1 §4, 00 §7,
-- DECISIONS A2): one durable UUID per project that survives file renames and moves. The id
-- comes from the `evidgate.id` annotation or from a per-directory sidecar manifest; both
-- present and different is a conflict, never a silent pick (T55).
--
--   test_case             the case. `annotation_id` is its identity inside the project and can
--                         never change; `file_path` is a *current* location and moves on a rename
--                         (the plan's AC6 lists file_path, but T2.1 must rewrite it, so the guard
--                         protects only the columns that make the case the same case).
--   case_identity_source  append-only: which source produced the id, and from which commit.
--   case_rename_history   append-only: one row per detected move.
--
-- Tenant tables of 00 §5.4.1: org_id, unique (org_id, id), composite FKs, FORCE ROW LEVEL
-- SECURITY with the standard policy. `name` is deliberately not unique (two tests may share a
-- name, D2); identity is `annotation_id` only.

create table if not exists public.test_case (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  annotation_id uuid not null,
  name text not null,
  framework text not null,
  language text,
  file_path text,
  status text not null default 'active',
  owner_membership_id uuid,
  labels text[] not null default '{}',
  first_seen_commit text not null,
  last_seen_commit text,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint test_case_pkey primary key (id),
  constraint test_case_org_id_key unique (org_id, id),
  constraint test_case_project_annotation_key unique (org_id, project_id, annotation_id),
  constraint test_case_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint test_case_owner_fk foreign key (org_id, owner_membership_id) references public.membership (org_id, id),
  constraint test_case_status_check check (status in ('active', 'missing', 'archived')),
  constraint test_case_name_length_check check (char_length(name) between 1 and 512),
  constraint test_case_framework_length_check check (char_length(framework) between 1 and 64),
  constraint test_case_path_length_check check (file_path is null or char_length(file_path) between 1 and 1024),
  constraint test_case_labels_check check (cardinality(labels) <= 32),
  constraint test_case_commit_check check (first_seen_commit ~ '^[0-9a-f]{7,64}$')
);

-- Serves GET /v1/projects/{p}/cases (P4) in (created_at, id) order. The plan leads with `status`, but the
-- default list spans active and missing cases, which a status-leading index could not return in order;
-- status and framework are cheap filters on a project's rows.
create index if not exists test_case_list_idx
  on public.test_case (org_id, project_id, created_at, id);

-- The rename lookup of a sync: the live case at a path.
create index if not exists test_case_path_idx
  on public.test_case (org_id, project_id, file_path)
  where file_path is not null;

create table if not exists public.case_identity_source (
  id uuid not null,
  org_id uuid not null,
  case_id uuid not null,
  source text not null,
  raw_key text not null,
  commit_sha text not null,
  created_at timestamptz not null default now(),
  constraint case_identity_source_pkey primary key (id),
  constraint case_identity_source_org_id_key unique (org_id, id),
  constraint case_identity_source_case_fk foreign key (org_id, case_id) references public.test_case (org_id, id),
  constraint case_identity_source_source_check check (source in ('annotation', 'sidecar', 'imported')),
  constraint case_identity_source_key_length_check check (char_length(raw_key) between 1 and 1024)
);

create index if not exists case_identity_source_case_idx
  on public.case_identity_source (org_id, case_id);

create table if not exists public.case_rename_history (
  id uuid not null,
  org_id uuid not null,
  case_id uuid not null,
  from_path text not null,
  to_path text not null,
  commit_sha text not null,
  detected_at timestamptz not null default now(),
  constraint case_rename_history_pkey primary key (id),
  constraint case_rename_history_org_id_key unique (org_id, id),
  constraint case_rename_history_case_fk foreign key (org_id, case_id) references public.test_case (org_id, id),
  constraint case_rename_history_path_length_check check (
    char_length(from_path) between 1 and 1024 and char_length(to_path) between 1 and 1024)
);

create index if not exists case_rename_history_case_idx
  on public.case_rename_history (org_id, case_id, detected_at);

-- The version trigger of 0004 on the one mutable table.
drop trigger if exists test_case_set_updated_at_and_version on public.test_case;
create trigger test_case_set_updated_at_and_version
  before update on public.test_case
  for each row
  execute function public.set_updated_at_and_version();

-- Immutability (C6, D1). The identity columns of a case never change; the two history tables
-- are append-only. The guard is a trigger so it holds for every role that can write, not only
-- for the app role whose grants already omit UPDATE/DELETE on them.
create or replace function evidgate.catalog_case_identity_guard() returns trigger
  language plpgsql
  as $$
  begin
    if tg_table_name = 'test_case' then
      if tg_op = 'UPDATE'
         and (new.id, new.org_id, new.project_id, new.annotation_id, new.first_seen_commit)
             is distinct from (old.id, old.org_id, old.project_id, old.annotation_id, old.first_seen_commit) then
        raise exception 'case_identity_immutable: the identity columns of a test case never change'
          using errcode = '23514';
      end if;
      if tg_op = 'DELETE' then
        raise exception 'case_identity_immutable: a test case is archived, never deleted'
          using errcode = '23514';
      end if;

      return case tg_op when 'DELETE' then old else new end;
    end if;

    raise exception 'case_identity_immutable: % is append-only', tg_table_name
      using errcode = '23514';
  end
  $$;

revoke all on function evidgate.catalog_case_identity_guard() from public;

drop trigger if exists test_case_identity_guard on public.test_case;
create trigger test_case_identity_guard
  before update or delete on public.test_case
  for each row execute function evidgate.catalog_case_identity_guard();

drop trigger if exists case_identity_source_append_only on public.case_identity_source;
create trigger case_identity_source_append_only
  before update or delete on public.case_identity_source
  for each row execute function evidgate.catalog_case_identity_guard();

drop trigger if exists case_rename_history_append_only on public.case_rename_history;
create trigger case_rename_history_append_only
  before update or delete on public.case_rename_history
  for each row execute function evidgate.catalog_case_identity_guard();

-- RLS: the standard tenant policy plus the owner policy that FORCE needs for backfills.
alter table public.test_case enable row level security;
alter table public.test_case force row level security;
alter table public.case_identity_source enable row level security;
alter table public.case_identity_source force row level security;
alter table public.case_rename_history enable row level security;
alter table public.case_rename_history force row level security;

drop policy if exists test_case_app_tenant on public.test_case;
create policy test_case_app_tenant on public.test_case
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists test_case_owner_backfill on public.test_case;
create policy test_case_owner_backfill on public.test_case
  for all to evidgate_owner using (true) with check (true);

drop policy if exists case_identity_source_app_tenant on public.case_identity_source;
create policy case_identity_source_app_tenant on public.case_identity_source
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists case_identity_source_owner_backfill on public.case_identity_source;
create policy case_identity_source_owner_backfill on public.case_identity_source
  for all to evidgate_owner using (true) with check (true);

drop policy if exists case_rename_history_app_tenant on public.case_rename_history;
create policy case_rename_history_app_tenant on public.case_rename_history
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists case_rename_history_owner_backfill on public.case_rename_history;
create policy case_rename_history_owner_backfill on public.case_rename_history
  for all to evidgate_owner using (true) with check (true);

grant select, insert, update on public.test_case to evidgate_app;
revoke delete on public.test_case from evidgate_app;
grant select, insert on public.case_identity_source to evidgate_app;
revoke update, delete on public.case_identity_source from evidgate_app;
grant select, insert on public.case_rename_history to evidgate_app;
revoke update, delete on public.case_rename_history from evidgate_app;

grant select on public.test_case, public.case_identity_source, public.case_rename_history to evidgate_operator;
revoke all on public.test_case, public.case_identity_source, public.case_rename_history from evidgate_worker_sched;
