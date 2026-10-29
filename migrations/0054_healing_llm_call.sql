-- 0054_healing_llm_call.sql
--
-- Owner: 08.5.1-T1.1 (feature plan 08.5.1 §4, AC1-AC7). Two tables in one file: `healing_llm_call`
-- is the ledger of every provider ATTEMPT the healing gateway makes, and `healing_llm_budget` is the
-- rolling daily cap a project consumes before a call is placed. They are created together because the
-- recorder and the budget gate are one command's transaction (08.5.1-T1.4): a reader of one never
-- arrives without the other, and the migration journal gets one unit to reason about.
--
-- WHY `healing_llm_call` IS ONE ROW PER ATTEMPT, NOT PER INVOCATION. AC1 retries a schema-violating
-- response exactly once and AC2 aborts a call that outruns the per-call timeout; both are facts about
-- one provider call, and "how many attempts did this invocation need, and why did the second one end"
-- is the question the ledger exists to answer. Folding attempts into one mutable row would also need
-- an UPDATE grant on a record of what already happened — see APPEND-ONLY below. `attempt` is the
-- ordinal inside one invocation (`1`, `2`, …); it is deliberately not part of any unique key: two
-- invocations of the same incident both have an attempt 1, and they are different calls.
--
-- APPEND-ONLY. A call row has `created_at` and nothing else: no `updated_at`, no `version`, no UPDATE
-- and no DELETE grant for the application role (0015/0031/0049/0053 precedent). The status a row
-- carries is final — the recorder inserts the attempt after it ends (T1.2's `HealingLlmCallRecorder`,
-- one INSERT per attempt inside the caller's tenant transaction), so "correct the optimistic row
-- afterwards" never has to be expressed as a write on history. A privileged backfill by the owner is
-- still possible under the owner policy, which is why the audit columns' absence is a grant decision
-- as well as a shape decision.
--
-- NO SECRET, PROMPT OR RESPONSE COLUMN (AC6, S3). Provider credentials are read from the secret store
-- by the worker identity and never reach this table, and neither does the prompt text, the raw model
-- response, or any free-text payload: `redaction_report_hash` is a hash of the 08.5.3 redaction report
-- (populated once that task lands; nullable now so this story is independently buildable), and that is
-- the whole of the content this ledger keeps. `tokens` and `cost` are counts, and are the loss-free
-- replacement. The shape test pins the column list exactly, so an added secret/prompt column is a
-- failing test rather than a review anecdote.
--
-- ATTRIBUTION (AC4, AC7, T30). `project_id` is NOT NULL: every call is attributable to a project and
-- therefore to a tenant, and it is the column the budget and the cost report read. `purpose` is a
-- closed three-value vocabulary: `authoring_generation` (13.3.1-T2's generation — a project
-- generation, not an incident), `triage_hypothesis` (08.2.2's advisory label) and
-- `candidate_generation` (08.5.4). `incident_id` and `candidate_id` are nullable because an authoring
-- call has neither, and three CHECKs make the shape unambiguous: an authoring call carries no incident
-- and no candidate; every other purpose names its incident; a candidate only ever refines an
-- incident-scoped call. The third rule is implied by the first two and is kept anyway — it is the rule
-- a future purpose would otherwise have to remember.
--
-- DEFERRED FOREIGN KEYS. `incident` and `healing_candidate` do not exist yet, so `incident_id` and
-- `candidate_id` carry NO foreign key here. What is owed, by the migration that creates each table:
-- `alter table public.healing_llm_call add constraint healing_llm_call_incident_fk
-- foreign key (org_id, incident_id) references public.incident (org_id, id)` and the same shape for
-- `(org_id, candidate_id) -> public.healing_candidate (org_id, id)` — composite keys, so a call can
-- only ever name an incident/candidate of its OWN org, exactly as the project key below does. Until
-- then the attribution CHECKs still bound the row, and a dangling uuid can only come from application
-- code that skipped its own resolution; the columns stay nullable-and-unreferenced only until those
-- tables land, which is why the deferral is recorded here rather than left to be rediscovered.
--
-- KEYS AND INDEXES. `unique (org_id, id)` is the composite tenant-FK target every business table
-- declares (00 §5.4.6, `scripts/check-rls.py` rule `missing_unique_key`). Two indexes carry the two
-- read shapes the plan names: `(org_id, project_id, created_at)` answers the budget-window sums (what
-- did this project spend today) and `(org_id, project_id, purpose, created_at)` answers the cost report
-- per purpose (12.3.1 reads it; this story only emits the rows). `created_at` ascending: both reads
-- are range scans over a window, not keyset pages, so an ascending key serves them and a descending one
-- would not be used.
--
-- WHY THE PROJECT REFERENCES ARE COMPOSITE AND CASCADE. `(org_id, project_id) -> project (org_id, id)`
-- is MATCH SIMPLE over two NOT NULL columns, so a row can only ever name a project of its own org (the
-- parallel defect of a plain `project_id -> project (id)` key is what the isolation suite's composite-FK
-- rules exist for). It cascades on delete, as 0052 does and as the 08.5.1 shared contract requires: a
-- project row is hard-deleted only by the tenant-erasure path (06.4.4; the app role holds no DELETE),
-- and a ledger or budget row must not block that delete with a 23503.
--
-- `healing_llm_budget`. One row per (project, period, period_start): `unique (org_id, project_id,
-- period, period_start)` IS the atomic-consume target, not merely a guard — T1.4's hard stop is a
-- single `update public.healing_llm_budget set used_cost = used_cost + @cost, used_tokens =
-- used_tokens + @tokens where org_id = @org and project_id = @project and period = 'daily' and
-- period_start = @day and used_cost + @cost <= period_cap_cost and used_tokens + @tokens <=
-- period_cap_tokens returning id` whose zero-row result means "no call is placed" (AC3, mirroring the
-- `healing_quota` T32 pattern). No consumption code ships in this slice: the key and the column grants
-- below are the plan that statement needs. `period` is `'daily'` alone — a second period value is a new
-- value in the vocabulary and a migration, which is what the CHECK costs and why it is worth paying
-- here: the reset schedule (T1.7) and the consume statement both spell the period literally.
-- `period_start` is a `date` because the boundary is a calendar day the caller resolves; the cap and
-- the usage are `numeric(12,6)`/`integer` (the same scale as the `cost`/`tokens` they bound).
-- `created_at`/`updated_at`/`version` are the 00 §7 audit columns plus the shared
-- `set_updated_at_and_version` trigger, because this row IS updated in place — the consume statement
-- moves `used_cost`/`used_tokens`, and the reset schedule clears them, so the trigger owns the
-- concurrency token for both.
--
-- GRANTS. 0001's default privileges hand `evidgate_app` full DML on every table the migration role
-- creates in `public`, so each grant below would be an additive no-op without the revoke first
-- (0006/0009/0027/0033/0052 precedent). After it:
--   * `healing_llm_call`: SELECT and a column-level INSERT of exactly the thirteen columns an attempt
--     row is made of. `created_at` is excluded so a caller cannot backdate an attempt (a table-level
--     INSERT would let it name the column, so the grant is per column). No UPDATE, no DELETE: the row
--     is history.
--   * `healing_llm_budget`: SELECT, a column-level INSERT of the five identity/period columns (`id`,
--     `org_id`, `project_id`, `period`, `period_start`) and an UPDATE of exactly the two columns the
--     consume/reset statements move (`used_cost`, `used_tokens`) — two, not the six an earlier revision
--     of this comment counted for the UPDATE alone. `period_cap_cost`/`period_cap_tokens` are in NEITHER
--     grant (see THE CAP WRITER IS OWED below), and `updated_at`/`version` are not in the UPDATE grant
--     either because the 0004 trigger assigns them without a column privilege (see the trigger note at
--     its statement).
--     The identity columns stay unwritable, so a consume cannot re-target or backdate the row it just
--     matched, no app statement can raise a cap, and there is no DELETE.
-- Neither table has a `SECURITY DEFINER`: the tenant write path is a plain app-role statement inside
-- the caller's tenant transaction (00 §5.5 C6 prefers a grant to a definer), and `evidgate_operator`
-- keeps a read-only SELECT exactly as 0052 records while `evidgate_worker_sched` holds nothing —
-- the gateway runs inside a tenant context, never as a sweep.
--
-- THE CAP WRITER IS OWED, NOT GRANTED. The two caps are the ceiling a project's own traffic is held
-- to, so a tenant transaction that could write either one would let that project raise its own limit:
-- `period_cap_cost` and `period_cap_tokens` are therefore absent from BOTH the INSERT and the UPDATE
-- grant. They are NOT NULL, so the app role's residual INSERT grant (the five identity/period columns)
-- cannot complete a row either — the statement fails on `period_cap_cost` before any consume could
-- read it, which is the intended state and not an oversight: provisioning is the OWNER/definer path
-- owed to 08.5.1-T1.4 (the budget gate and its atomic hard stop) and T1.7 (the reset schedule), and is
-- tracked as the carried row `healing_llm_budget cap writer owed (S145)`. The tenant transaction only
-- ever moves `used_cost`/`used_tokens` against a cap it did not choose; until the provisioning path
-- lands, no budget row exists and the hard stop is T1.4's to implement, not this migration's.
--
-- RLS (DECISIONS F5, rulings T2/T3). ENABLE + FORCE on both tables, `tenant_isolation` as the tenant
-- policy (`org_id = evidgate.current_org_id()` in USING, applied as WITH CHECK too; no `TO` clause, so
-- it applies to PUBLIC), and the house `_owner_backfill` policy `for all to evidgate_owner using (true)
-- with check (true)` so a migration or fixture write is not refused by FORCE (0004/0005/0028/0046/
-- 0049/0052/0053 precedent). Neither table is listed in `contracts/rls-exceptions.txt`: `org_id` is
-- NOT NULL on both, so there is no NULL-org read row and the boot self-check sees a plain scoped
-- tenant policy.
--
-- DEPLOY ORDER: MIGRATE BEFORE APP. The gateway, its recorder and the budget gate are new in this
-- story, so nothing binds to either table before this file is applied. Rollback:
-- `drop table if exists public.healing_llm_budget; drop table if exists public.healing_llm_call;` —
-- both tables hold derived telemetry (calls made, budget consumed), so dropping them loses cost
-- attribution history and nothing else that cannot be re-derived from the provider's own billing.
--
-- Idempotent in the shape the README's file contract allows: `create table if not exists`,
-- `create index if not exists`, `drop policy if exists` / `drop trigger if exists` before each
-- `create`, and re-stated `revoke`/`grant`. One file runs in one transaction, so the tables, their
-- policies and their grants are never observable apart.

create table if not exists public.healing_llm_call (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  incident_id uuid,
  candidate_id uuid,
  purpose text not null,
  model text not null,
  prompt_version text not null,
  tokens integer not null default 0,
  cost numeric(12,6) not null default 0,
  status text not null,
  attempt integer not null default 1,
  redaction_report_hash text,
  created_at timestamptz not null default now(),
  constraint healing_llm_call_pkey primary key (id),
  constraint healing_llm_call_org_id_key unique (org_id, id),
  constraint healing_llm_call_project_fk
    foreign key (org_id, project_id) references public.project (org_id, id) on delete cascade,
  constraint healing_llm_call_purpose_check
    check (purpose in ('authoring_generation', 'triage_hypothesis', 'candidate_generation')),
  constraint healing_llm_call_status_check
    check (status in ('ok', 'timeout', 'llm_schema_violation', 'budget_exceeded', 'error')),
  constraint healing_llm_call_tokens_check check (tokens >= 0),
  constraint healing_llm_call_cost_check check (cost >= 0),
  constraint healing_llm_call_attempt_check check (attempt >= 1),
  -- AC7: an authoring generation serves a project, never an incident and never a candidate.
  constraint healing_llm_call_authoring_attribution_check
    check (purpose <> 'authoring_generation' or (incident_id is null and candidate_id is null)),
  -- AC4: every other purpose attributes the call to the incident it serves.
  constraint healing_llm_call_incident_required_check
    check (purpose = 'authoring_generation' or incident_id is not null),
  -- A candidate only ever refines an incident-scoped call; implied by the two rules above, kept so a
  -- future purpose cannot introduce the shape by omission.
  constraint healing_llm_call_candidate_incident_check
    check (candidate_id is null or incident_id is not null)
);

-- The budget-window sum ("what did this project spend on this day") and the per-purpose cost report.
create index if not exists healing_llm_call_project_created_idx
  on public.healing_llm_call (org_id, project_id, created_at);
create index if not exists healing_llm_call_project_purpose_created_idx
  on public.healing_llm_call (org_id, project_id, purpose, created_at);

create table if not exists public.healing_llm_budget (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  period text not null,
  period_start date not null,
  period_cap_cost numeric(12,6) not null,
  period_cap_tokens integer not null,
  used_cost numeric(12,6) not null default 0,
  used_tokens integer not null default 0,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  version integer not null default 1,
  constraint healing_llm_budget_pkey primary key (id),
  constraint healing_llm_budget_org_id_key unique (org_id, id),
  -- The atomic-consume target (AC3, T1.4): one counter row per project, period and period start.
  constraint healing_llm_budget_period_key unique (org_id, project_id, period, period_start),
  constraint healing_llm_budget_project_fk
    foreign key (org_id, project_id) references public.project (org_id, id) on delete cascade,
  constraint healing_llm_budget_period_check check (period in ('daily')),
  constraint healing_llm_budget_period_cap_cost_check check (period_cap_cost >= 0),
  constraint healing_llm_budget_period_cap_tokens_check check (period_cap_tokens >= 0),
  constraint healing_llm_budget_used_cost_check check (used_cost >= 0),
  constraint healing_llm_budget_used_tokens_check check (used_tokens >= 0),
  constraint healing_llm_budget_version_check check (version >= 1)
);

-- The convention trigger of 0004: the database owns `updated_at` and the `version` increment, so a
-- consume by the app role and a reset by a later privileged sweep both advance the concurrency token.
-- The app role holds NO column privilege on either — that is deliberate and it still works:
-- `set_updated_at_and_version()` is invoker-rights plpgsql (no SECURITY DEFINER, and 0004 grants it
-- EXECUTE to `evidgate_app`), and PostgreSQL checks column privileges against the columns a statement
-- NAMES, never against the columns a trigger assigns to NEW. So `update ... set used_cost = ...` stamps
-- `updated_at` and increments `version` exactly as 0039's `feature_node` does — a table that carries
-- BOTH the `version` column and this trigger while `grant update (parent_id, kind, title, position,
-- deleted_at)` leaves `updated_at`/`version` outside the app-role grant, the same shape as 0027's
-- `waiver` and 0028's `artifact` — while the caller itself can never write the concurrency token it is
-- validated against.
drop trigger if exists healing_llm_budget_set_updated_at_and_version on public.healing_llm_budget;
create trigger healing_llm_budget_set_updated_at_and_version
  before update on public.healing_llm_budget
  for each row execute function public.set_updated_at_and_version();

alter table public.healing_llm_call enable row level security;
alter table public.healing_llm_call force row level security;
alter table public.healing_llm_budget enable row level security;
alter table public.healing_llm_budget force row level security;

-- The tenant policy of both tables: an org reaches its own ledger and its own budget counters, and
-- nothing else. No `TO` clause, so it applies to PUBLIC (0053 precedent).
drop policy if exists tenant_isolation on public.healing_llm_call;
create policy tenant_isolation on public.healing_llm_call
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists tenant_isolation on public.healing_llm_budget;
create policy tenant_isolation on public.healing_llm_budget
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- `force row level security` applies to the table owner too: the migration role needs its own policy
-- so a backfill or fixture write is not refused (0004/0005/0028/0046/0049/0052/0053 precedent). It is
-- owner-only and grants the app role nothing.
drop policy if exists healing_llm_call_owner_backfill on public.healing_llm_call;
create policy healing_llm_call_owner_backfill on public.healing_llm_call
  for all to evidgate_owner using (true) with check (true);

drop policy if exists healing_llm_budget_owner_backfill on public.healing_llm_budget;
create policy healing_llm_budget_owner_backfill on public.healing_llm_budget
  for all to evidgate_owner using (true) with check (true);

-- Append-only ledger (0015/0031/0049/0053 precedent): the app role inserts attempts and reads them
-- back, but can neither rewrite nor remove one. `created_at` is outside the column grant so an attempt
-- cannot be backdated.
revoke insert, update, delete on public.healing_llm_call from evidgate_app;
grant select on public.healing_llm_call to evidgate_app;
grant insert (id, org_id, project_id, incident_id, candidate_id, purpose, model, prompt_version,
              tokens, cost, status, attempt, redaction_report_hash)
  on public.healing_llm_call to evidgate_app;

-- The budget row is mutable in place, but only where the consume and reset statements write, and only
-- against a cap the app role cannot name: `used_cost`/`used_tokens` are the whole UPDATE grant, while
-- the caps (the owner/provisioning path's to set) and the trigger-owned `updated_at`/`version` are
-- outside every grant. The identity and the period stay unwritable, so a consume cannot re-target the
-- row it matched, and no DELETE exists (the project cascade is the only delete path). The residual
-- INSERT names the identity/period columns only; the NOT NULL caps refuse it, so no app statement can
-- provision or raise a row — see THE CAP WRITER IS OWED in the header.
revoke insert, update, delete on public.healing_llm_budget from evidgate_app;
grant select on public.healing_llm_budget to evidgate_app;
grant insert (id, org_id, project_id, period, period_start)
  on public.healing_llm_budget to evidgate_app;
grant update (used_cost, used_tokens) on public.healing_llm_budget to evidgate_app;

-- TL T76: the operator's read is grant-gated with no operator policy until 02.8.1's
-- `evidgate.operator_grant_ok` lands; the sweep role holds nothing, because it never calls a provider
-- and no reset schedule ships in this slice.
grant select on public.healing_llm_call to evidgate_operator;
grant select on public.healing_llm_budget to evidgate_operator;
revoke all on public.healing_llm_call from evidgate_worker_sched;
revoke all on public.healing_llm_budget from evidgate_worker_sched;
