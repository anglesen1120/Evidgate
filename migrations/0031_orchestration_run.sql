-- 0031_orchestration_run.sql
--
-- Owner: 05.2.1-T1.1. `run` and `planned_execution`: one run of one verified manifest and the
-- per-case plan it will execute (feature plan 05.2.1 §4, area plan 05 §4, 00 §7/§8.1, rulings
-- T3/T9/T11/T13/T54/T88/T93). `POST /v1/projects/{p}/runs` writes the `run` row, its
-- `planned_execution` rows, one `producer_slot` registration per manifest shard (04's
-- `IProducerRegistry.RegisterSlotsAsync`, T9) and the outbox row in ONE tenant transaction (D2,
-- T13), so a run and its plan commit or not at all. `run.state` starts at `created`; the
-- `created -> queued` move is 05.2.1-T4, the rest of the 00 §8.1 machine is 05.3.1.
--
-- Two deferred foreign keys land here (owner decision 2026-10-10, chat). `artifact` (0028) and
-- `producer_slot` (0029) were created before `run` existed, so both kept a bare `run_id uuid not
-- null` with no key. This migration adds the composite tenant key each one's plan declares:
--   * `artifact_run_fk (org_id, run_id) -> run (org_id, id)`
--   * `producer_slot_run_fk (org_id, run_id) -> run (org_id, id)`
-- Both are added `not valid` first, then an explicit orphan check runs, then the constraint is
-- validated. A plain `add constraint` scans the table and fails with a bare `23503` on the first
-- orphan, naming neither the table nor the fix; the three-step form turns that into a message that
-- says which table, how many rows and what to do. Existing-row safety: 06.1.2 is the first writer
-- of `artifact` and 05's run-create is the first writer of `producer_slot`, so on every database
-- that reaches this file both tables are empty and the orphan check counts zero — the check exists
-- for a database that was hand-seeded or restored out of band, and it refuses to validate a
-- constraint over rows that would violate it. The `not valid` window is inside this file's single
-- transaction (`WithTransactionPerScript`), so no reader ever sees the unvalidated state.
--
-- `test_revision`, not `case_revision`: the plan §4 DDL writes `case_revision (org_id, id)`, but
-- the catalogue table that holds a case's revision is `public.test_revision` (migration 0021,
-- 03.1.3). `planned_execution.case_revision_id` therefore references `public.test_revision`, and
-- the column keeps the plan's name because the wire contract and the manifest schema call it a
-- case revision.
--
-- [DECISION] Q1 (coordinator, 2026-10-11): the handler passes the manifest shard COUNT (1..256) to
-- `IProducerRegistry.RegisterSlotsAsync(runId, shardCount)`, and `planned_execution.shard_id` is
-- `shard-<1-based ordinal of the manifest shard>` — the same string 04 derives as
-- `producer_slot.producer_id` (T54). The manifest's own `shard_id` label stays only in
-- `manifest.shard_plan`; it is not copied here. The CHECK below pins that derived shape.
--
-- `updated_at` is a deliberate addition to the plan §4 DDL: the plan lists `created_at` and
-- `version` but not `updated_at`, and 00 §7 requires all three on a business table. `run` is
-- mutable — 05.3.1's state machine updates `state` under the optimistic-concurrency token — so it
-- carries the shared `set_updated_at_and_version` trigger, and that trigger writes `new.updated_at`
-- (0004). Without the column the trigger would fail at runtime and the Persistence schema-convention
-- scan (01.4.2-T2.2) would reject the table. `planned_execution` is append-only (nothing updates a
-- planned row; 05.5.1's rerun inserts new ones), so it carries no audit columns and no trigger —
-- the same posture as `suite_member`/`variant` (0022), and it is listed in that scan's allow-list.
--
-- Column grants (why not the plain `select, insert, update` of 0024): both tables are
-- create-time-only in this story. `ci_run_ref`, `collection_deadline_at`, `late_window_minutes`,
-- `purpose` and `replay_grant_id` are computed and written once by the create handler and never
-- mutated afterwards (T88, T93, T67), and a planned row is never edited. `0001`'s default
-- privileges grant `evidgate_app` full DML on every table `evidgate_owner` creates in `public`, so
-- the table-level `revoke update, delete` below is required before the minimal grant (0027/0028
-- precedent). 05.3.1 adds its own `grant update (state) on public.run` with its own migration; no
-- DELETE on either table — a run is canceled, never deleted.
--
-- Row security: both tenant policies are `for all to evidgate_app`, exactly like 0005/0020-0030. A
-- policy with no `to` clause applies to PUBLIC, which would let `evidgate_operator` (it holds the
-- SELECT grant below) set `evidgate.org_id` and read any org. No `TO evidgate_operator` policy is
-- created here (TL T76, D1): the grant-gated operator policy is 02.8.1's
-- `evidgate.operator_grant_ok` form, and until it lands the operator's SELECT grant reads zero rows.
--
-- Bound CHECKs (the 0006-0030 `char_length`/regex style): `execution_key` is the 00 §7
-- `sha256(case_id ‖ variant_hash ‖ target_env)` hex — lowercase, the envelope schema pattern
-- `^[0-9a-f]{64}$` (04.3.1-T1) — `variant_hash` is the 03.2.1 `sha256:<64 hex>` content address,
-- `shard_id` is the Q1 `shard-<n>` shape, `target_env` and `test_commit` carry their documented
-- bounds, `late_window_minutes` is 1..525600 (one minute to one year, T62/T93), `rerun_scope` is
-- null or the 05.5.1 closed pair, `ci_run_ref` is null or exactly the T88
-- `{provider, repository_id, run_id, run_attempt}` object (the key set is exact, expressed with
-- `?&` plus a key-stripping equality because `jsonb_build_object` is STABLE and a CHECK may only
-- call IMMUTABLE functions), and `components` is a bounded jsonb
-- object. `state` and `purpose` enumerate their full 00 §8.1 / T67 vocabularies now, so no later
-- migration widens them.
--
-- Idempotent like 0025/0027-0030: `create table if not exists` / `drop ... if exists` / guarded
-- `do` blocks, so a re-run is a no-op.

-- ---------------------------------------------------------------------------------------------------
-- Part 1: `run` (05.2.1 §4, 00 §8.1).
-- ---------------------------------------------------------------------------------------------------

create table if not exists public.run (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  manifest_id uuid not null,
  policy_revision_id uuid,
  suite_revision_id uuid not null,
  environment_revision_id uuid not null,
  test_commit text,
  ci_run_ref jsonb,
  components jsonb not null default '{}'::jsonb,
  parent_run_id uuid,
  rerun_scope text,
  purpose text not null default 'regular',
  replay_grant_id uuid,
  state text not null default 'created',
  collection_deadline_at timestamptz not null,
  late_window_minutes integer not null,
  version integer not null default 0,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint run_pkey primary key (id),
  constraint run_org_id_key unique (org_id, id),
  constraint run_manifest_fk foreign key (org_id, manifest_id) references public.manifest (org_id, id),
  constraint run_suite_revision_fk foreign key (org_id, suite_revision_id) references public.suite_revision (org_id, id),
  constraint run_environment_revision_fk foreign key (org_id, environment_revision_id) references public.environment_revision (org_id, id),
  constraint run_policy_revision_fk foreign key (org_id, policy_revision_id) references public.policy_revision (org_id, id),
  constraint run_parent_run_fk foreign key (org_id, parent_run_id) references public.run (org_id, id),
  constraint run_purpose_check check (purpose in ('regular', 'replay')),
  constraint run_replay_grant_check check (purpose <> 'replay' or replay_grant_id is not null),
  constraint run_state_check check (state in
    ('created', 'queued', 'running', 'collecting', 'complete', 'incomplete', 'canceled')),
  constraint run_rerun_scope_check check (rerun_scope is null or rerun_scope in ('full', 'failed_subset')),
  constraint run_late_window_minutes_check check (late_window_minutes between 1 and 525600),
  constraint run_test_commit_check check (test_commit is null or char_length(test_commit) <= 128),
  constraint run_ci_run_ref_check check (
    ci_run_ref is null
    or (jsonb_typeof(ci_run_ref) = 'object'
        and ci_run_ref ?& array['provider', 'repository_id', 'run_id', 'run_attempt']
        and ci_run_ref - 'provider' - 'repository_id' - 'run_id' - 'run_attempt' = '{}'::jsonb)),
  constraint run_components_check check (
    jsonb_typeof(components) = 'object' and pg_column_size(components) <= 65536),
  constraint run_version_check check (version >= 0)
);

-- The run list query (09) walks one project's runs newest-first; the keyset tail is `id` (P4).
create index if not exists run_project_created_idx on public.run (org_id, project_id, created_at desc, id);

-- The shared concurrency trigger of 0004 (M1W3-01): the database owns `updated_at` and the `version`
-- increment, so 05.3.1's `where version = @original` write cannot be won by two concurrent writers.
drop trigger if exists run_set_updated_at_and_version on public.run;
create trigger run_set_updated_at_and_version
  before update on public.run
  for each row execute function public.set_updated_at_and_version();

alter table public.run enable row level security;
alter table public.run force row level security;

drop policy if exists run_tenant on public.run;
create policy run_tenant on public.run
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
drop policy if exists run_owner_backfill on public.run;
create policy run_owner_backfill on public.run
  for all to evidgate_owner using (true) with check (true);

revoke update, delete on public.run from evidgate_app;
grant select, insert on public.run to evidgate_app;
grant select on public.run to evidgate_operator;
revoke all on public.run from evidgate_worker_sched;

-- ---------------------------------------------------------------------------------------------------
-- Part 2: `planned_execution` (05.2.1 §4, AC6).
-- ---------------------------------------------------------------------------------------------------

create table if not exists public.planned_execution (
  id uuid not null,
  org_id uuid not null,
  run_id uuid not null,
  execution_key text not null,
  case_revision_id uuid not null,
  variant_hash text not null,
  target_env text not null,
  shard_id text not null,
  required boolean not null default true,
  constraint planned_execution_pkey primary key (id),
  constraint planned_execution_org_id_key unique (org_id, id),
  constraint planned_execution_run_execution_key unique (org_id, run_id, execution_key),
  constraint planned_execution_run_fk foreign key (org_id, run_id) references public.run (org_id, id),
  constraint planned_execution_case_revision_fk foreign key (org_id, case_revision_id) references public.test_revision (org_id, id),
  constraint planned_execution_execution_key_check check (execution_key ~ '^[0-9a-f]{64}$'),
  constraint planned_execution_variant_hash_check check (variant_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint planned_execution_target_env_check check (char_length(target_env) between 1 and 64),
  constraint planned_execution_shard_id_check check (
    shard_id ~ '^shard-[1-9][0-9]*$' and char_length(shard_id) <= 128)
);

-- The reconcile scan of 05.3.2 reads every planned row of one run (P4).
create index if not exists planned_execution_run_idx on public.planned_execution (org_id, run_id);

alter table public.planned_execution enable row level security;
alter table public.planned_execution force row level security;

drop policy if exists planned_execution_tenant on public.planned_execution;
create policy planned_execution_tenant on public.planned_execution
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- Deliberately no `TO evidgate_operator` policy here (TL T76, D1): see the note on `run` above.
drop policy if exists planned_execution_owner_backfill on public.planned_execution;
create policy planned_execution_owner_backfill on public.planned_execution
  for all to evidgate_owner using (true) with check (true);

revoke update, delete on public.planned_execution from evidgate_app;
grant select, insert on public.planned_execution to evidgate_app;
grant select on public.planned_execution to evidgate_operator;
revoke all on public.planned_execution from evidgate_worker_sched;

-- ---------------------------------------------------------------------------------------------------
-- Part 3: the two deferred composite tenant keys (owner decision 2026-10-10).
-- Add `not valid`, refuse to validate over orphans, then validate. Each step is guarded so a re-run
-- is a no-op.
-- ---------------------------------------------------------------------------------------------------

do $fk$
begin
  if not exists (
    select 1
      from pg_catalog.pg_constraint
     where conrelid = 'public.artifact'::regclass
       and conname = 'artifact_run_fk')
  then
    alter table public.artifact
      add constraint artifact_run_fk foreign key (org_id, run_id) references public.run (org_id, id) not valid;
  end if;
end
$fk$;

do $orphans$
declare
  orphan_count bigint;
begin
  select count(*) into orphan_count
    from public.artifact a
   where not exists (select 1 from public.run r where r.org_id = a.org_id and r.id = a.run_id);

  if orphan_count > 0 then
    raise exception
      'artifact_run_fk: % artifact row(s) have a run_id with no run in the same org; delete or repair them before this migration can validate the composite tenant key',
      orphan_count
      using errcode = '23503';
  end if;
end
$orphans$;

alter table public.artifact validate constraint artifact_run_fk;

do $fk$
begin
  if not exists (
    select 1
      from pg_catalog.pg_constraint
     where conrelid = 'public.producer_slot'::regclass
       and conname = 'producer_slot_run_fk')
  then
    alter table public.producer_slot
      add constraint producer_slot_run_fk foreign key (org_id, run_id) references public.run (org_id, id) not valid;
  end if;
end
$fk$;

do $orphans$
declare
  orphan_count bigint;
begin
  select count(*) into orphan_count
    from public.producer_slot s
   where not exists (select 1 from public.run r where r.org_id = s.org_id and r.id = s.run_id);

  if orphan_count > 0 then
    raise exception
      'producer_slot_run_fk: % producer_slot row(s) have a run_id with no run in the same org; delete or repair them before this migration can validate the composite tenant key',
      orphan_count
      using errcode = '23503';
  end if;
end
$orphans$;

alter table public.producer_slot validate constraint producer_slot_run_fk;
