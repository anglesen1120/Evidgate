-- 0028_evidence_artifact.sql
--
-- Owner: 06.1.1-T1.1. `artifact`: the metadata row of one captured evidence object, minted by the
-- server before the object exists (feature plan 06.1.1 §4, area plan 06 §2 FR-06-1, 00 §7/§8.4,
-- rulings T2/T3/T17/T19/T20). The object key is server-chosen and unique per org
-- (`org/<org_id>/project/<project_id>/run/<run_id>/attempt/<execution_key>/<attempt_no>/<artifact_id>`),
-- so 06.1.2 can stage an upload and 05 can resolve evidence without ever trusting a client key.
-- `artifact_kind_policy` is the per-project capture policy the mint command reads: the maximum size,
-- the allowed media types, the default sensitivity, the secret-rejection switch and the capture
-- switch, one row per (project, kind).
--
-- Owner decision 2026-10-10 (chat): the `run` table does not exist yet — 05.2.1-T1 creates it — so
-- the plan's `foreign key (org_id, run_id) references run (org_id, id)` is deferred to that task.
-- `run_id` stays `uuid not null` with no foreign key here, and both tables instead carry the
-- composite tenant FK `(org_id, project_id) -> project (org_id, id)` (00 §5.4.1/§5.4.6, T3).
--
-- T17: `attempt_id` is nullable and carries no foreign key — the `case_attempt` row does not exist
-- when the artifact is minted; the link is written at evidence freeze.
--
-- Column grants (why not the plain `select, insert, update` of 0024): the app role may move only the
-- lifecycle columns — `attempt_id`, `bytes`, `scan_state`, `state`, `expires_at`, `failure_code` on
-- `artifact`, and the policy knobs on `artifact_kind_policy` — never the identity, the server-chosen
-- object key, the checksum or the audit columns. `0001`'s default privileges grant `evidgate_app`
-- full DML on every table `evidgate_owner` creates in `public`, so the table-level `revoke update`
-- below is required before the column-level `grant update` (0027 precedent). No DELETE on either
-- table: a removed artifact is the `state = 'deleted'` soft delete of 06.4.x, never a hard delete.
--
-- Row security: both tenant policies are `for all to evidgate_app`, exactly like 0005/0020-0027. A
-- policy with no `to` clause applies to PUBLIC, which would let `evidgate_operator` (it holds the
-- SELECT grant below) set `evidgate.org_id` and read any org. No `TO evidgate_operator` policy is
-- created here (TL T76, D1): the grant-gated operator policy is 02.8.1's
-- `evidgate.operator_grant_ok` form, and until it lands the operator's SELECT grant reads zero rows.
--
-- Bound CHECKs (the 0006-0027 `char_length`/`cardinality` style): `execution_key` is the 00 §7
-- `sha256(case_id ‖ variant_hash ‖ target_env)` hex — lowercase, the envelope schema pattern
-- `^[0-9a-f]{64}$` (04.3.1-T1) — `checksum` is the 06.1.1 §5 `sha256:<64 hex>` content address,
-- `attempt_no` starts at 1 (00 §7), and the text/blob columns carry their documented bounds.
-- `object_key` is additionally pinned to the two documented server-chosen forms: the 06.1.1 §1/AC2
-- final key `org/<org_id>/project/<project_id>/run/<run_id>/attempt/<execution_key>/<attempt_no>/<id>`
-- and the 06.1.2 §3 staging key `pending/<org_id>/<id>` (06.1.2 plan line 35, FR-06-2), so a client
-- cannot name, traverse or collide with a key even by writing the table directly.
--
-- Idempotent like 0025/0027: `create table if not exists` / `drop ... if exists` so a re-run is a no-op.

create table if not exists public.artifact (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  run_id uuid not null,
  execution_key text not null,
  attempt_no integer not null,
  attempt_id uuid,
  kind text not null,
  object_key text not null,
  media_type text not null,
  bytes bigint,
  checksum text not null,
  sensitivity text not null,
  scan_state text not null default 'pending',
  state text not null default 'pending_upload',
  expires_at timestamptz,
  failure_code text,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint artifact_pkey primary key (id),
  constraint artifact_org_id_key unique (org_id, id),
  constraint artifact_org_object_key_key unique (org_id, object_key),
  constraint artifact_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint artifact_kind_check check (kind in ('trace', 'video', 'screenshot', 'raw_report', 'dom_snapshot', 'a11y_snapshot', 'patch', 'ui_hierarchy')),
  constraint artifact_sensitivity_check check (sensitivity in ('normal', 'sensitive')),
  constraint artifact_scan_state_check check (scan_state in ('pending', 'clean', 'flagged')),
  constraint artifact_state_check check (state in ('pending_upload', 'uploaded', 'ready', 'rejected', 'pending_delete', 'deleted')),
  constraint artifact_execution_key_check check (execution_key ~ '^[0-9a-f]{64}$'),
  constraint artifact_checksum_check check (checksum ~ '^sha256:[0-9a-f]{64}$'),
  constraint artifact_attempt_no_check check (attempt_no >= 1),
  constraint artifact_media_type_check check (char_length(media_type) between 1 and 255),
  constraint artifact_bytes_check check (bytes is null or bytes > 0),
  constraint artifact_failure_code_check check (failure_code is null or char_length(failure_code) between 1 and 128),
  constraint artifact_object_key_check check (char_length(object_key) between 1 and 1024),
  constraint artifact_object_key_shape_check check (
    object_key = 'pending/' || org_id::text || '/' || id::text
    or object_key = 'org/' || org_id::text || '/project/' || project_id::text || '/run/' || run_id::text
         || '/attempt/' || execution_key || '/' || attempt_no::text || '/' || id::text),
  constraint artifact_version_check check (version >= 1)
);

-- The freeze/reconcile read walks the live artifacts of one run; the retention job (06.4.2) walks the
-- ready rows by expiry. Both are partial, so the deleted rows and the not-yet-ready rows stay out of
-- the index (P4).
create index if not exists artifact_run_idx on public.artifact (org_id, run_id) where state <> 'deleted';
create index if not exists artifact_expiry_idx on public.artifact (expires_at) where state = 'ready';
-- The referencing side of the composite project FK (a project or tenant delete), 0024 precedent.
create index if not exists artifact_project_idx on public.artifact (org_id, project_id);

-- The shared concurrency trigger of 0004 (M1W3-01): the database owns `updated_at` and the `version`
-- increment, so a second tracked update in the same scope matches the incremented token.
drop trigger if exists artifact_set_updated_at_and_version on public.artifact;
create trigger artifact_set_updated_at_and_version
  before update on public.artifact
  for each row execute function public.set_updated_at_and_version();

alter table public.artifact enable row level security;
alter table public.artifact force row level security;

drop policy if exists artifact_tenant on public.artifact;
create policy artifact_tenant on public.artifact
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- Deliberately no `TO evidgate_operator` policy here (TL T76, D1): the base migration does not grant
-- the operator standing read access. The operator reaches this table only through 02.8.1's
-- grant-gated `evidgate.operator_grant_ok` policy; until that lands the SELECT grant below reads
-- zero rows.

-- The migration role needs its own policy: `force row level security` applies to the table owner too,
-- so an expand/contract backfill or an owner SECURITY DEFINER sweep would otherwise update zero rows
-- without failing (0004/0005 precedent). Explicit and permissive on purpose — the alternative would
-- be BYPASSRLS, which DECISIONS F5 rejects.
drop policy if exists artifact_owner_backfill on public.artifact;
create policy artifact_owner_backfill on public.artifact
  for all to evidgate_owner using (true) with check (true);

revoke update on public.artifact from evidgate_app;
grant select, insert on public.artifact to evidgate_app;
grant update (attempt_id, bytes, scan_state, state, expires_at, failure_code) on public.artifact to evidgate_app;
revoke delete on public.artifact from evidgate_app;
grant select on public.artifact to evidgate_operator;
revoke all on public.artifact from evidgate_worker_sched;

create table if not exists public.artifact_kind_policy (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  kind text not null,
  max_bytes bigint not null,
  media_types text[] not null,
  default_sensitivity text not null,
  reject_on_secret boolean not null default false,
  capture_enabled boolean not null default true,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint artifact_kind_policy_pkey primary key (id),
  constraint artifact_kind_policy_org_id_key unique (org_id, id),
  constraint artifact_kind_policy_project_kind_key unique (org_id, project_id, kind),
  constraint artifact_kind_policy_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint artifact_kind_policy_kind_check check (kind in ('trace', 'video', 'screenshot', 'raw_report', 'dom_snapshot', 'a11y_snapshot', 'patch', 'ui_hierarchy')),
  constraint artifact_kind_policy_default_sensitivity_check check (default_sensitivity in ('normal', 'sensitive')),
  constraint artifact_kind_policy_max_bytes_check check (max_bytes > 0),
  constraint artifact_kind_policy_media_types_check check (cardinality(media_types) between 1 and 64),
  constraint artifact_kind_policy_version_check check (version >= 1)
);

drop trigger if exists artifact_kind_policy_set_updated_at_and_version on public.artifact_kind_policy;
create trigger artifact_kind_policy_set_updated_at_and_version
  before update on public.artifact_kind_policy
  for each row execute function public.set_updated_at_and_version();

alter table public.artifact_kind_policy enable row level security;
alter table public.artifact_kind_policy force row level security;

drop policy if exists artifact_kind_policy_tenant on public.artifact_kind_policy;
create policy artifact_kind_policy_tenant on public.artifact_kind_policy
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- Deliberately no `TO evidgate_operator` policy here (TL T76, D1): the grant-gated operator policy is
-- 02.8.1's `evidgate.operator_grant_ok` form; until it lands the SELECT grant below reads zero rows.

drop policy if exists artifact_kind_policy_owner_backfill on public.artifact_kind_policy;
create policy artifact_kind_policy_owner_backfill on public.artifact_kind_policy
  for all to evidgate_owner using (true) with check (true);

revoke update on public.artifact_kind_policy from evidgate_app;
grant select, insert on public.artifact_kind_policy to evidgate_app;
grant update (max_bytes, media_types, default_sensitivity, reject_on_secret, capture_enabled) on public.artifact_kind_policy to evidgate_app;
revoke delete on public.artifact_kind_policy from evidgate_app;
grant select on public.artifact_kind_policy to evidgate_operator;
revoke all on public.artifact_kind_policy from evidgate_worker_sched;
