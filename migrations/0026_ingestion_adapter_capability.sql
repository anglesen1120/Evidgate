-- 0026_ingestion_adapter_capability.sql
--
-- Owner: 04.7.1-T2. `adapter_capability`: the capability.v1 document an adapter sends as the first
-- envelope of a run (kind `run.log`, sequence 1, `ext.schema` = `<adapter-name>/<protocol-major>`,
-- `ext.capability` = the document), stored once per natural key (org_id, name, protocol_major,
-- protocol_minor, engine_version) (feature plan 04.7.1 §4, contracts/adapter/README.md).
--
-- The row is a first-write fact: it records what the adapter declared the first time it was seen and is
-- never updated. Immutability is defence in depth: the app role holds SELECT, INSERT only (no
-- UPDATE/DELETE), and there is no lifecycle column to move, so a stored capability stays exactly as it
-- was first written. `first_seen_at` is the only time column — no created_at/updated_at/version and no
-- version trigger — which is why the schema-convention scan exempts AuditColumns and VersionTrigger for
-- this table.
--
-- A conflicting first send (same natural key, different engine_family/platforms/supports/mode) is refused
-- 409 `capability_conflict` by the store (04.7.1-T2), not by the database: the unique constraint only
-- stops a second row, and the store compares the stored row to decide no-op vs 409.
--
-- No `project_id` and no foreign key: a capability is an org-level fact about an adapter, not about a
-- project (task file §4). `name` is the `ext.schema` prefix and follows the project-slug grammar;
-- `platforms` is a non-empty array of non-null platform tokens; `supports` is the verbatim `supports`
-- object (unknown flags are stored, never evaluated) and is capped at the schema's x-max-bytes (16384).

create table if not exists public.adapter_capability (
  id uuid not null,
  org_id uuid not null,
  name text not null,
  protocol_major integer not null,
  protocol_minor integer not null,
  engine_family text not null,
  engine_version text not null,
  platforms text[] not null,
  supports jsonb not null,
  mode text not null,
  first_seen_at timestamptz not null default now(),
  constraint adapter_capability_pkey primary key (id),
  constraint adapter_capability_org_id_key unique (org_id, id),
  constraint adapter_capability_natural_key unique (org_id, name, protocol_major, protocol_minor, engine_version),
  constraint adapter_capability_protocol_major_check check (protocol_major >= 1),
  constraint adapter_capability_protocol_minor_check check (protocol_minor >= 0),
  constraint adapter_capability_name_check check (name ~ '^[a-z0-9][a-z0-9._-]{0,63}$'),
  constraint adapter_capability_engine_family_check check (char_length(engine_family) between 1 and 64),
  constraint adapter_capability_engine_version_check check (char_length(engine_version) between 1 and 128),
  constraint adapter_capability_platforms_check check (cardinality(platforms) between 1 and 16 and array_position(platforms, null) is null),
  constraint adapter_capability_supports_check check (jsonb_typeof(supports) = 'object' and octet_length(supports::text) <= 16384),
  constraint adapter_capability_mode_check check (mode in ('full', 'ingest_only'))
);

alter table public.adapter_capability enable row level security;
alter table public.adapter_capability force row level security;

drop policy if exists adapter_capability_app_tenant on public.adapter_capability;
create policy adapter_capability_app_tenant on public.adapter_capability
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists adapter_capability_owner_backfill on public.adapter_capability;
create policy adapter_capability_owner_backfill on public.adapter_capability
  for all to evidgate_owner using (true) with check (true);

grant select, insert on public.adapter_capability to evidgate_app;
revoke update, delete on public.adapter_capability from evidgate_app;
grant select on public.adapter_capability to evidgate_operator;
revoke all on public.adapter_capability from evidgate_worker_sched;
