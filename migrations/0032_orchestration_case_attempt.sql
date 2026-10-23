-- 0032_orchestration_case_attempt.sql
--
-- Owner: 05.3.3-T1.1. `case_attempt`: one executed attempt of one planned execution, with the raw
-- status exactly as the producer sent it and the 00 §8.3 normalised status beside it (feature plan
-- 05.3.3 §4/§6, area plan 05 §2 FR-05-15, 00 §7/§8.3, rulings T3/T50). The identity is
-- `(org_id, run_id, execution_key, attempt_no)`; `attempt_no` starts at 1 and a corrected status is a
-- NEW attempt row with the next `attempt_no`, never an edit (C6, T50's `ON CONFLICT DO NOTHING`).
--
-- Two [DECISION]s of this task, recorded here because they are not in the plan §4 DDL verbatim:
--
--   * Both composite tenant keys are kept. `case_attempt_run_fk (org_id, run_id) -> run (org_id, id)`
--     is redundant with `case_attempt_planned_execution_fk` in the strict sense — a planned row
--     already implies its run — but it is kept because the run is the reconciler's (05.3.2) primary
--     scope and the run key is the one a run delete / tenant sweep must be able to name; the plan §4
--     DDL declares it, and dropping it would silently widen the FK surface the 02.9.1 composite-FK
--     lint asserts. `case_attempt_planned_execution_fk (org_id, run_id, execution_key) ->
--     planned_execution (org_id, run_id, execution_key)` is the identity key: 04 refuses an unplanned
--     execution key with 422 `unknown_execution_key` (T90), so every attempt has a planned row.
--
--   * `raw_status` is bounded `char_length(raw_status) between 0 and 64`, not `1 and 64`. The
--     envelope schema (`contracts/schemas/event-envelope.v1.json`) declares `status` with
--     `maxLength: 64` and NO `minLength`, so an empty raw status is a value the producer may legally
--     send; the adapter truncates only a value longer than 64 UTF-16 units
--     (`contracts/adapter/playwright-mapping.md` §status). An empty raw status is stored verbatim and
--     normalises to `error` (05.3.3-T1.2), so the column must accept it. The upper bound is the
--     envelope's own `maxLength`.
--
-- No `updated_at`/`version` and no version trigger: an attempt row is immutable (C6, `revoke update,
-- delete` below), so it keeps `created_at` only — the same posture as `planned_execution` (0031) and
-- `suite_member`/`variant` (0022), and it is listed in the Persistence schema-convention scan's
-- allow-list for `AuditColumns | VersionTrigger`.
--
-- `case_id` vs `planned_execution.case_revision_id`: the plan §4 DDL carries both a `case_id` column
-- and the planned-execution key. `case_id` must equal the `test_revision.case_id` of the planned row
-- (the case the attempt belongs to), but that equality is NOT a database constraint — it is enforced
-- by 05.3.3-T2's writer, which resolves `case_id` from the planned row inside the caller's
-- transaction. A CHECK cannot express it (it spans two tables), and a trigger would add write cost to
-- every batch insert (P2/T50). The composite `case_attempt_case_fk (org_id, case_id) ->
-- test_case (org_id, id)` still guarantees the case exists in the same org.
--
-- `job_id` is `uuid` with NO foreign key: the `job` table is created only by 05.7.2-T1 (V2, M4), so
-- the key is deferred exactly as 0028/0029 deferred their run keys. `job_id` is null in CI mode and
-- set in agent mode (V2).
--
-- 06.4.6 later repartitions this table (`PARTITION BY RANGE (run_created_at)`, a new
-- `run_created_at` column and a widened unique key). This migration deliberately keeps the DDL plain
-- — no partitioning, no `run_created_at` — so 06.4.6 owns that change in one place.
--
-- Deferred FK from the 06.1.1-T1 hand-over: `artifact.attempt_id` (0028) was nullable with no key
-- because `case_attempt` did not exist at mint time (T17). This migration adds
-- `artifact_attempt_fk (org_id, attempt_id) -> case_attempt (org_id, id)` with 0031's three-step
-- pattern (add `not valid`, orphan check, validate). `attempt_id` stays nullable; the resolution of
-- `attempt_id` from `(org_id, execution_key, attempt_no)` at freeze time is 06.5.1-T2, NOT this
-- migration. The artifact grants are unchanged.
--
-- Bound CHECKs (the 0006-0031 `char_length`/regex style): `execution_key` is the 00 §7
-- `sha256(case_id ‖ variant_hash ‖ target_env)` hex — lowercase, the envelope schema pattern
-- `^[0-9a-f]{64}$` (04.3.1-T1) — `attempt_no` starts at 1 (00 §7), `normalized_status` enumerates
-- the full 00 §8.3 vocabulary now so no later migration widens it, `duration_ms` is non-negative
-- (the envelope's `minimum: 0`), and `finished_at >= started_at` when both are set.
--
-- Indexes: `case_attempt_run_status_idx (org_id, run_id, normalized_status)` serves the reconciler's
-- completeness comparison (AC6) and `case_attempt_case_started_idx (org_id, case_id, started_at)`
-- serves the history views (AC6). No standalone `(org_id, run_id, execution_key, attempt_no)` index:
-- the unique constraint `case_attempt_run_execution_attempt_key` is left-prefixed by
-- `(org_id, run_id, execution_key, attempt_no)` and already serves the join 06.5.1-T2 makes.
--
-- Row security: the tenant policy is `for all to evidgate_app`, exactly like 0005/0020-0031. A policy
-- with no `to` clause applies to PUBLIC, which would let `evidgate_operator` (it holds the SELECT
-- grant below) set `evidgate.org_id` and read any org. No `TO evidgate_operator` policy is created
-- here (TL T76, D1): the grant-gated operator policy is 02.8.1's `evidgate.operator_grant_ok` form,
-- and until it lands the operator's SELECT grant reads zero rows.
--
-- Column grants (why not the plain `select, insert, update` of 0024): an attempt row is
-- create-time-only in this story. `0001`'s default privileges grant `evidgate_app` full DML on every
-- table `evidgate_owner` creates in `public`, so the table-level `revoke update, delete` below is
-- required before the minimal grant (0027/0028/0031 precedent). No DELETE: an attempt is never
-- deleted, only superseded by a new `attempt_no`.
--
-- Idempotent like 0025/0027-0031: `create table if not exists` / `drop ... if exists` / guarded
-- `do` blocks, so a re-run is a no-op.

-- ---------------------------------------------------------------------------------------------------
-- Part 1: `case_attempt` (05.3.3 §4, 00 §8.3).
-- ---------------------------------------------------------------------------------------------------

create table if not exists public.case_attempt (
  id uuid not null,
  org_id uuid not null,
  run_id uuid not null,
  job_id uuid,
  execution_key text not null,
  case_id uuid not null,
  attempt_no integer not null,
  raw_status text not null,
  normalized_status text not null,
  started_at timestamptz,
  finished_at timestamptz,
  duration_ms integer,
  created_at timestamptz not null default now(),
  constraint case_attempt_pkey primary key (id),
  constraint case_attempt_org_id_key unique (org_id, id),
  constraint case_attempt_run_execution_attempt_key unique (org_id, run_id, execution_key, attempt_no),
  constraint case_attempt_run_fk foreign key (org_id, run_id) references public.run (org_id, id),
  constraint case_attempt_planned_execution_fk foreign key (org_id, run_id, execution_key)
    references public.planned_execution (org_id, run_id, execution_key),
  constraint case_attempt_case_fk foreign key (org_id, case_id) references public.test_case (org_id, id),
  constraint case_attempt_execution_key_check check (execution_key ~ '^[0-9a-f]{64}$'),
  constraint case_attempt_attempt_no_check check (attempt_no >= 1),
  constraint case_attempt_raw_status_check check (char_length(raw_status) between 0 and 64),
  constraint case_attempt_normalized_status_check check (normalized_status in
    ('passed', 'failed', 'skipped', 'timed_out', 'error', 'blocked')),
  constraint case_attempt_duration_ms_check check (duration_ms is null or duration_ms >= 0),
  constraint case_attempt_finished_after_started_check check (
    finished_at is null or started_at is null or finished_at >= started_at)
);

-- The reconciler's completeness comparison walks one run's attempts by normalised status (AC6).
create index if not exists case_attempt_run_status_idx on public.case_attempt (org_id, run_id, normalized_status);
-- The history views walk one case's attempts newest-first (AC6).
create index if not exists case_attempt_case_started_idx on public.case_attempt (org_id, case_id, started_at);

alter table public.case_attempt enable row level security;
alter table public.case_attempt force row level security;

drop policy if exists case_attempt_tenant on public.case_attempt;
create policy case_attempt_tenant on public.case_attempt
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
drop policy if exists case_attempt_owner_backfill on public.case_attempt;
create policy case_attempt_owner_backfill on public.case_attempt
  for all to evidgate_owner using (true) with check (true);

revoke update, delete on public.case_attempt from evidgate_app;
grant select, insert on public.case_attempt to evidgate_app;
grant select on public.case_attempt to evidgate_operator;
revoke all on public.case_attempt from evidgate_worker_sched;

-- ---------------------------------------------------------------------------------------------------
-- Part 2: the deferred composite tenant key from the 06.1.1-T1 hand-over.
-- `artifact.attempt_id` (0028) was nullable with no key because `case_attempt` did not exist at mint
-- time (T17). Add `not valid`, refuse to validate over orphans, then validate. Each step is guarded
-- so a re-run is a no-op. `attempt_id` stays nullable; resolving it from
-- `(org_id, execution_key, attempt_no)` at freeze time is 06.5.1-T2, not this migration.
-- ---------------------------------------------------------------------------------------------------

do $fk$
begin
  if not exists (
    select 1
      from pg_catalog.pg_constraint
     where conrelid = 'public.artifact'::regclass
       and conname = 'artifact_attempt_fk')
  then
    alter table public.artifact
      add constraint artifact_attempt_fk foreign key (org_id, attempt_id)
      references public.case_attempt (org_id, id) not valid;
  end if;
end
$fk$;

do $orphans$
declare
  orphan_count bigint;
begin
  select count(*) into orphan_count
    from public.artifact a
   where a.attempt_id is not null
     and not exists (select 1 from public.case_attempt c where c.org_id = a.org_id and c.id = a.attempt_id);

  if orphan_count > 0 then
    raise exception
      'artifact_attempt_fk: % artifact row(s) have an attempt_id with no case_attempt in the same org; delete or repair them before this migration can validate the composite tenant key',
      orphan_count
      using errcode = '23503';
  end if;
end
$orphans$;

alter table public.artifact validate constraint artifact_attempt_fk;
