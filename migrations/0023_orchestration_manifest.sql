-- 0023_orchestration_manifest.sql
--
-- Owner: 05.1.1-T3.2/T4.1. `manifest`: the verified, immutable plan a run is later locked to (feature plan 05.1.1 §4, 00 §8,
-- DECISIONS A8). The body is verified against `contracts/manifest/manifest.v1.json` and the catalogue before it gets here
-- and is stored under a stable `manifest_hash` (`sha256:` over RFC 8785); the same canonical content in the same project is the
-- same row. Nothing updates or deletes a manifest.
--
-- Deviations from the plan's DDL, recorded in the feature plan: the composite FK to `project` (a project-scoped table), and
-- `verified_by` is text (the audit actor id), because CI credentials upload manifests and are not members.
-- `environment_revision_id` is a plain column until migration 0024 creates `environment_revision` and adds its composite FK.

create table if not exists public.manifest (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  manifest_hash text not null,
  schema_version integer not null,
  suite_revision_id uuid not null,
  environment_revision_id uuid,
  shard_plan jsonb not null,
  matrix jsonb not null,
  env_needs jsonb not null,
  planned_count integer not null,
  document jsonb not null,
  verified_at timestamptz not null default now(),
  verified_by text not null,
  created_at timestamptz not null default now(),
  constraint manifest_pkey primary key (id),
  constraint manifest_org_id_key unique (org_id, id),
  constraint manifest_hash_key unique (org_id, project_id, manifest_hash),
  constraint manifest_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint manifest_suite_revision_fk foreign key (org_id, suite_revision_id) references public.suite_revision (org_id, id),
  constraint manifest_hash_check check (manifest_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint manifest_planned_count_check check (planned_count >= 0),
  constraint manifest_schema_version_check check (schema_version = 1),
  constraint manifest_verified_by_check check (char_length(verified_by) between 1 and 128),
  constraint manifest_document_check check (jsonb_typeof(document) = 'object')
);

-- Run creation joins the manifest to its suite revision; the FK needs the index for the suite_revision side.
create index if not exists manifest_suite_revision_idx on public.manifest (org_id, suite_revision_id);

create or replace function evidgate.orchestration_manifest_immutable() returns trigger
  language plpgsql
  as $$
  begin
    raise exception 'manifest_immutable: a manifest is append-only (%)', tg_op
      using errcode = '23514';
  end
  $$;

revoke all on function evidgate.orchestration_manifest_immutable() from public;

drop trigger if exists manifest_immutable on public.manifest;
create trigger manifest_immutable
  before update or delete on public.manifest
  for each row execute function evidgate.orchestration_manifest_immutable();

alter table public.manifest enable row level security;
alter table public.manifest force row level security;

drop policy if exists manifest_app_tenant on public.manifest;
create policy manifest_app_tenant on public.manifest for all to evidgate_app
  using (org_id = evidgate.current_org_id()) with check (org_id = evidgate.current_org_id());
drop policy if exists manifest_owner_backfill on public.manifest;
create policy manifest_owner_backfill on public.manifest for all to evidgate_owner using (true) with check (true);

grant select, insert on public.manifest to evidgate_app;
revoke update, delete on public.manifest from evidgate_app;
grant select on public.manifest to evidgate_operator;
revoke all on public.manifest from evidgate_worker_sched;
