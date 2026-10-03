-- 0024_orchestration_environment.sql
--
-- Owner: 05.1.2-T1.1. Environments and their immutable revisions (feature plan 05.1.2 §4, FR-05-14): a project environment is
-- versioned into `environment_revision` rows holding only secret-free configuration — base URLs, the dependency versions that
-- affect results — and secret *references* (never values), each with a stable `revision_hash` (`sha256:` over RFC 8785).
-- A run locks exactly one revision; a revision is never edited, a new one is appended.
--
--   environment           the named line of revisions in a project. `current_revision_id` is a movable pointer (the one mutable
--                         column), so the table carries the version trigger of 0004 like any mutable tenant table. The pointer has no
--                         foreign key (it would close a cycle with environment_revision); the create command writes it in the
--                         same transaction as the revision it names.
--   environment_revision  append-only.
--
-- Also here, as the plan asks: `project.default_environment_id` gets its composite FK, and `manifest.environment_revision_id`
-- (0023) gets its composite FK. The project <-> environment pair is a foreign-key cycle; the test harness resets with one TRUNCATE for it.

create table if not exists public.environment (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  environment_key text not null,
  name text not null,
  current_revision_id uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint environment_pkey primary key (id),
  constraint environment_org_id_key unique (org_id, id),
  constraint environment_key_unique unique (org_id, project_id, environment_key),
  constraint environment_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint environment_key_format_check check (environment_key ~ '^[a-z0-9][a-z0-9_-]{0,63}$'),
  constraint environment_name_length_check check (char_length(name) between 1 and 128)
);

drop trigger if exists environment_set_updated_at_and_version on public.environment;
create trigger environment_set_updated_at_and_version
  before update on public.environment
  for each row execute function public.set_updated_at_and_version();

create table if not exists public.environment_revision (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  environment_id uuid not null,
  revision_no integer not null,
  base_urls jsonb not null,
  dependency_versions jsonb not null,
  secret_refs text[] not null default '{}',
  revision_hash text not null,
  created_by text not null,
  created_at timestamptz not null default now(),
  constraint environment_revision_pkey primary key (id),
  constraint environment_revision_org_id_key unique (org_id, id),
  constraint environment_revision_no_key unique (org_id, environment_id, revision_no),
  constraint environment_revision_hash_key unique (org_id, environment_id, revision_hash),
  constraint environment_revision_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint environment_revision_environment_fk foreign key (org_id, environment_id) references public.environment (org_id, id),
  constraint environment_revision_no_check check (revision_no >= 1),
  constraint environment_revision_hash_check check (revision_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint environment_revision_json_check check (
    jsonb_typeof(base_urls) = 'object' and jsonb_typeof(dependency_versions) = 'object'
    and octet_length(base_urls::text) <= 16384 and octet_length(dependency_versions::text) <= 16384),
  constraint environment_revision_secret_refs_check check (
    cardinality(secret_refs) <= 64
    and (cardinality(secret_refs) = 0
         or array_to_string(secret_refs, E'\n') ~ '^secret:[A-Za-z0-9_.-]{1,64}(\nsecret:[A-Za-z0-9_.-]{1,64})*$')),
  constraint environment_revision_created_by_check check (char_length(created_by) between 1 and 128)
);

-- The environment's revisions newest first (the diff and the head walk).
create index if not exists environment_revision_env_idx on public.environment_revision (org_id, environment_id, revision_no desc);

create or replace function evidgate.orchestration_environment_revision_immutable() returns trigger
  language plpgsql
  as $$
  begin
    raise exception 'revision_immutable: an environment revision is append-only (%)', tg_op
      using errcode = '23514';
  end
  $$;

revoke all on function evidgate.orchestration_environment_revision_immutable() from public;

drop trigger if exists environment_revision_immutable on public.environment_revision;
create trigger environment_revision_immutable
  before update or delete on public.environment_revision
  for each row execute function evidgate.orchestration_environment_revision_immutable();

-- The environment's identity never changes and its pointer can only name one of its own revisions (the pointer has no foreign
-- key, so this trigger is its integrity), like `suite_guard` in 0022.
create or replace function evidgate.orchestration_environment_guard() returns trigger
  language plpgsql
  as $$
  begin
    if new.id <> old.id or new.org_id <> old.org_id or new.project_id <> old.project_id
       or new.environment_key <> old.environment_key then
      raise exception 'environment_identity_immutable: id, org, project and key never change'
        using errcode = '23514';
    end if;

    if new.current_revision_id is not null
       and new.current_revision_id is distinct from old.current_revision_id
       and not exists (
         select 1
           from public.environment_revision r
          where r.org_id = new.org_id
            and r.id = new.current_revision_id
            and r.environment_id = new.id) then
      raise exception 'environment_pointer_invalid: the current revision must be a revision of this environment'
        using errcode = '23514';
    end if;

    return new;
  end
  $$;

revoke all on function evidgate.orchestration_environment_guard() from public;

drop trigger if exists environment_guard on public.environment;
create trigger environment_guard
  before update on public.environment
  for each row execute function evidgate.orchestration_environment_guard();

-- project.default_environment_id (02's table, added column here by plan) and manifest.environment_revision_id.
alter table public.project drop constraint if exists project_default_env_fk;
alter table public.project
  add constraint project_default_env_fk foreign key (org_id, default_environment_id)
  references public.environment (org_id, id);

alter table public.manifest drop constraint if exists manifest_env_rev_fk;
alter table public.manifest
  add constraint manifest_env_rev_fk foreign key (org_id, environment_revision_id)
  references public.environment_revision (org_id, id);

create index if not exists project_default_environment_idx on public.project (org_id, default_environment_id) where default_environment_id is not null;
create index if not exists manifest_environment_revision_idx on public.manifest (org_id, environment_revision_id) where environment_revision_id is not null;

alter table public.environment enable row level security;
alter table public.environment force row level security;
alter table public.environment_revision enable row level security;
alter table public.environment_revision force row level security;

drop policy if exists environment_app_tenant on public.environment;
create policy environment_app_tenant on public.environment for all to evidgate_app
  using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());
drop policy if exists environment_owner_backfill on public.environment;
create policy environment_owner_backfill on public.environment for all to evidgate_owner using (true) with check (true);

drop policy if exists environment_revision_app_tenant on public.environment_revision;
create policy environment_revision_app_tenant on public.environment_revision for all to evidgate_app
  using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());
drop policy if exists environment_revision_owner_backfill on public.environment_revision;
create policy environment_revision_owner_backfill on public.environment_revision for all to evidgate_owner using (true) with check (true);

grant select, insert, update on public.environment to evidgate_app;
revoke delete on public.environment from evidgate_app;
grant select, insert on public.environment_revision to evidgate_app;
revoke update, delete on public.environment_revision from evidgate_app;
grant select on public.environment, public.environment_revision to evidgate_operator;
revoke all on public.environment, public.environment_revision from evidgate_worker_sched;
