-- 0033_policy_add_policy_activation.sql
--
-- Owner: 07.1.1-T4.1. `policy_activation`: the project's single pointer to the policy revision a new
-- run pins (feature plan 07.1.1 §4/§5/§6, 00 §8.6, FR-07-2, ruling T93). One row per project, UPSERTed
-- on every `POST /v1/projects/{p}/policy-activation`; a project without a row has no active policy and
-- run creation keeps `run.policy_revision_id` NULL plus the `EVIDGATE:Runs` defaults (owner decision Q1).
-- There is no activation-history table: the audit log is the history (07.1.1 §4, C9).
--
-- [DECISION] deviations from the plan's DDL (07.1.1 §4), each forced by a repository rule:
--   * `id uuid` + `unique (org_id, id)`: `scripts/check-rls.py` (rule `missing_unique_key`) and
--     `tests/Persistence.IntegrationTests/SchemaConventionScan` require every non-exempt business table
--     to carry `unique (org_id, id)` as the composite-FK target (TL T3). The plan's DDL lists only the
--     natural key `unique (org_id, project_id)`; that key is kept as the row's identity (it is what the
--     UPSERT conflicts on), and the surrogate `id` satisfies the convention (0004/0005 precedent).
--   * `created_at`, `updated_at`, `version` + the shared `set_updated_at_and_version` trigger: the row is
--     UPSERTed, so it needs the 00 §7 audit columns and the shared version trigger (rule `AuditColumns`
--     / `VersionTrigger`), exactly as 0030's `quarantine_entry` does. The plan lists only `activated_at`;
--     `activated_at` stays and is re-stamped on every activation.
--
-- Column-level `grant update (state) on public.policy_revision to evidgate_app` (below): 0025 granted the
-- app role `SELECT, INSERT` only, and 07.1.1-T4's activation must move a revision `draft -> active` and
-- the previous active one `active -> retired`. The column grant is the smallest privilege that allows
-- those two statements and nothing else: `body`, `hash` and the identity columns stay unwritable, and the
-- 0025 `policy_revision_immutable` trigger still refuses every transition outside `draft -> active ->
-- retired` (so `retired -> active` is refused whoever issues it). No SECURITY DEFINER function is added
-- (00 §5.5 C6 prefers a grant to a definer), and the existing `policy_revision_app_tenant` policy is
-- `for all to evidgate_app`, so it already covers UPDATE under the org check.
--
-- Partial unique index `policy_revision (org_id, project_id) where state = 'active'`: one active revision
-- per project, enforced by the database rather than by the activation transaction's ordering (0030's
-- `quarantine_entry_active_case_key` precedent). The activation retires the previous active row before it
-- activates the target, so the index is satisfied at every statement boundary.
--
-- `run.late_window_minutes` CHECK widened from `1..525600` to `0..525600` (owner decision Q4): `0` means
-- no late evidence is accepted for a run pinned to that revision. `late_window_minutes` is written only
-- by run creation (05.2.1-T1) from the pinned revision's body, so no stored row can be invalidated.

-- ---------------------------------------------------------------------------------------------------
-- Part 1: the `policy_activation` table (07.1.1 §4).
-- ---------------------------------------------------------------------------------------------------

create table if not exists public.policy_activation (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  policy_revision_id uuid not null,
  mode text not null default 'advisory',
  activated_by uuid not null,
  activated_at timestamptz not null default now(),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint policy_activation_pkey primary key (id),
  constraint policy_activation_org_id_key unique (org_id, id),
  -- One pointer per project (07.1.1 §4): this is the key the activation UPSERT conflicts on.
  constraint policy_activation_project_key unique (org_id, project_id),
  constraint policy_activation_project_fk foreign key (org_id, project_id) references public.project (org_id, id),
  constraint policy_activation_revision_fk foreign key (org_id, policy_revision_id) references public.policy_revision (org_id, id),
  -- `enforcing` lands with 07.7.1 (M3); this story only writes the column and refuses the mode at the API.
  constraint policy_activation_mode_check check (mode in ('advisory', 'enforcing')),
  constraint policy_activation_version_check check (version >= 1)
);

-- The convention trigger of 0004: the database owns `updated_at` and the `version` increment, so an
-- upserted row advances its optimistic-concurrency token on every activation.
drop trigger if exists policy_activation_set_updated_at_and_version on public.policy_activation;
create trigger policy_activation_set_updated_at_and_version
  before update on public.policy_activation
  for each row execute function public.set_updated_at_and_version();

alter table public.policy_activation enable row level security;
alter table public.policy_activation force row level security;

drop policy if exists policy_activation_app_tenant on public.policy_activation;
create policy policy_activation_app_tenant on public.policy_activation
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- The migration role needs its own policy: `force row level security` applies to the owner too (0004/0005
-- precedent; the alternative would be BYPASSRLS, which DECISIONS F5 rejects).
drop policy if exists policy_activation_owner_backfill on public.policy_activation;
create policy policy_activation_owner_backfill on public.policy_activation
  for all to evidgate_owner using (true) with check (true);

-- The default privileges of 0001 grant `evidgate_app` full DML on every table `evidgate_owner` creates in
-- `public`, so the column grants below would be additive no-ops without this revoke (0006/0009/0027/0030
-- precedent). After it, the app role holds table-level SELECT plus INSERT on the request columns and
-- UPDATE on the four columns the activation re-stamps — it can neither delete the pointer nor rewrite the
-- row's identity. No DELETE at all: the pointer is only ever moved.
revoke insert, update, delete on public.policy_activation from evidgate_app;
grant select on public.policy_activation to evidgate_app;
grant insert (id, org_id, project_id, policy_revision_id, mode, activated_by) on public.policy_activation to evidgate_app;
grant update (policy_revision_id, mode, activated_by, activated_at) on public.policy_activation to evidgate_app;
grant select on public.policy_activation to evidgate_operator;
revoke all on public.policy_activation from evidgate_worker_sched;

-- ---------------------------------------------------------------------------------------------------
-- Part 2: the activation privileges on `policy_revision` (07.1.1 §6).
-- ---------------------------------------------------------------------------------------------------

-- One active revision per project, enforced by the database (see the header). A second `active` row is
-- refused 23505, which the activation transaction never produces (it retires first).
create unique index if not exists policy_revision_active_key
  on public.policy_revision (org_id, project_id)
  where state = 'active';

-- The two lifecycle moves the activation issues. Column-level, so `body`/`hash`/`created_*` stay
-- unwritable; the 0025 trigger still owns the state machine and the `version` increment.
grant update (state) on public.policy_revision to evidgate_app;

-- ---------------------------------------------------------------------------------------------------
-- Part 3: the widened `run.late_window_minutes` bound (owner decision Q4).
-- ---------------------------------------------------------------------------------------------------

-- Drop/recreate by the real constraint name (0031), guarded for re-run: the widened bound accepts 0
-- (no late evidence) up to one year.
alter table public.run drop constraint if exists run_late_window_minutes_check;
alter table public.run
  add constraint run_late_window_minutes_check check (late_window_minutes between 0 and 525600);
