-- 0059_policy_add_evaluation.sql
--
-- WHY: a run's verdict has to outlive the process that computed it, and two workers may compute the same
-- verdict at the same time — the reconcile that freezes evidence can be redelivered, and a retried task is
-- not a new question. This table is where the answer becomes durable and where "exactly once" is decided:
-- one row per (org_id, run_id, evidence_revision, policy_revision_id, evaluator_version, mode), and at most
-- one `live` row per (org_id, run_id, evidence_revision). The unique keys, not the handler, are the arbiter:
-- both racers INSERT, the loser takes 23505 and reads the winner's row back, so a crash and a retry produce
-- the same stored answer instead of a second one.
--
-- `shadow` is a second opinion about the same evidence, not a later state of the `live` row: it is a value
-- of `mode`, so the pair (live, shadow) is legal while two `live` rows for one evidence revision are not.
-- That split is why the partial unique index carries `where mode = 'live'` and the wider key carries `mode`
-- as its last column.
--
-- WHAT: the table, its keys and CHECKs, the lookup index behind the decision read, the immutability trigger,
-- row level security and the role grants. A row is never updated: no column encodes a lifecycle, so any
-- UPDATE or DELETE is a bug, refused by the trigger for every role and by the ACL for `evidgate_app`
-- (SELECT, INSERT only). Values are the evaluator's own wire tokens, so a stored row reads back into the
-- contract without a mapping layer; every closed list is a CHECK here, and a new token is a new migration.
--
-- REFERENCES: both foreign keys are composite on `org_id`, so a row cannot name another tenant's run or
-- policy revision even if a bug let the policy pass (D3). Neither carries ON DELETE: a run with an
-- evaluation is erased through the owner's one erasure path, never by a cascade that would delete evidence
-- of the decision.
--
-- ORDER: additive for every existing reader, so migrate before the worker that writes rows. No down
-- migration: dropping the table would delete every stored verdict, which no rollback can restore.

set local lock_timeout = '5s';

create table if not exists public.evaluation (
  id uuid not null,
  org_id uuid not null,
  run_id uuid not null,
  evidence_revision integer not null,
  policy_revision_id uuid not null,
  policy_hash text not null,
  manifest_hash text not null,
  completeness text not null,
  missing text[] not null default '{}',
  outcome text not null,
  decision text not null,
  reasons text[] not null default '{}',
  evaluator_version text not null,
  mode text not null,
  exceptions_hash text not null,
  exceptions_as_of timestamptz not null,
  created_at timestamptz not null default now(),
  constraint evaluation_pkey primary key (id),
  -- The composite-FK target of any later pointer to one evaluation, and the tenant key every table carries.
  constraint evaluation_org_id_key unique (org_id, id),
  -- The idempotency key: the same inputs and the same evaluator produce one stored row.
  constraint evaluation_result_key unique
    (org_id, run_id, evidence_revision, policy_revision_id, evaluator_version, mode),
  constraint evaluation_run_fk foreign key (org_id, run_id) references public.run (org_id, id),
  constraint evaluation_policy_revision_fk foreign key (org_id, policy_revision_id)
    references public.policy_revision (org_id, id),
  constraint evaluation_evidence_revision_check check (evidence_revision >= 1),
  constraint evaluation_completeness_check check (completeness in ('complete', 'incomplete')),
  constraint evaluation_outcome_check check (outcome in ('all_passed', 'has_failures', 'no_results')),
  constraint evaluation_decision_check check (decision in ('PASS', 'FAIL', 'INCONCLUSIVE')),
  constraint evaluation_mode_check check (mode in ('live', 'shadow')),
  constraint evaluation_policy_hash_check check (policy_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint evaluation_manifest_hash_check check (char_length(manifest_hash) between 1 and 200),
  constraint evaluation_exceptions_hash_check check (exceptions_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint evaluation_evaluator_version_check check (char_length(evaluator_version) between 1 and 32),
  -- The closed reason sets, enforced per element and one level only: an empty `{}` has no dimensions, a flat
  -- array one, so a nested array is refused. `missing` also rejects a NULL element, because `<@` accepts one.
  -- A CASE, not a conjunction: `array_position` raises on a multi-dimensional array and CHECK term order is
  -- not guaranteed, so the dimension test has to run — and short-circuit — first.
  constraint evaluation_missing_check check (
    case
      when array_ndims(missing) is null or array_ndims(missing) = 1 then
        missing <@ array['missing_shard', 'count_mismatch', 'artifact_missing', 'build_mismatch',
                         'unknown_status', 'setup_error', 'stale_epoch', 'missing_result']::text[]
        and array_position(missing, null) is null
      else false
    end),
  constraint evaluation_reasons_check check (
    case
      when array_ndims(reasons) is null or array_ndims(reasons) = 1 then
        reasons <@ array['scope_build_policy_pinned', 'required_assertion_failed', 'missing_shard',
                        'count_mismatch', 'artifact_missing', 'build_mismatch', 'unknown_status',
                        'setup_error', 'stale_epoch_only', 'missing_result', 'expected_count_zero',
                        'empty_suite_allowed', 'critical_not_executed', 'unknown_required_artifact_kind',
                        'flaky_budget_exceeded']::text[]
        and array_position(reasons, null) is null
      else false
    end),
  -- Cross-column, so the closed tokens above are not enough: the evaluator reports `complete` only with an
  -- empty `missing`, and `PASS` only over that pair. The CHECKs keep a handler bug from storing a verdict the
  -- decision matrix cannot produce (0049 pins the same completeness shape).
  constraint evaluation_complete_has_no_missing_check
    check (completeness <> 'complete' or missing = '{}'),
  constraint evaluation_pass_requires_complete_check
    check (decision <> 'PASS' or (completeness = 'complete' and missing = '{}'))
);

-- One `live` verdict per frozen evidence revision: the decision endpoint reads the newest row of a run, and
-- a second `live` row for the same revision would make "the" decision ambiguous. `shadow` rows are outside
-- the predicate on purpose — they are advisory and may be many.
create unique index if not exists evaluation_live_key
  on public.evaluation (org_id, run_id, evidence_revision)
  where mode = 'live';

-- The decision read walks a run's evaluations newest first; without it that lookup sorts the whole run's
-- history and the partial unique index cannot serve an `order by created_at desc` over every mode.
create index if not exists evaluation_run_idx
  on public.evaluation (org_id, run_id, created_at desc);

-- A stored evaluation is evidence, not state: nothing about it may change after the INSERT, for any role.
-- The app role cannot reach the trigger at all (the grants below refuse UPDATE/DELETE first), so the
-- trigger is the backstop for the owner and for any future grant, which is exactly the class of mistake it
-- exists to catch.
create or replace function evidgate.evaluation_immutable() returns trigger
  language plpgsql
  set search_path = ''
  as $$
  begin
    if tg_op = 'DELETE' then
      raise exception 'evaluation_immutable: a stored evaluation is never deleted (DELETE)'
        using errcode = '23514';
    end if;

    raise exception 'evaluation_immutable: a stored evaluation is never rewritten (UPDATE)'
      using errcode = '23514';
  end
  $$;

revoke all on function evidgate.evaluation_immutable() from public;

drop trigger if exists evaluation_immutable on public.evaluation;
create trigger evaluation_immutable
  before update or delete on public.evaluation
  for each row execute function evidgate.evaluation_immutable();

alter table public.evaluation enable row level security;
alter table public.evaluation force row level security;

drop policy if exists evaluation_app_tenant on public.evaluation;
create policy evaluation_app_tenant on public.evaluation
  for all to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

-- Deliberately no `TO evidgate_operator` policy here: the base migration does not grant the operator
-- standing read access. The operator reaches this table only through the grant-gated
-- `evidgate.operator_grant_ok` policy; until that lands the SELECT grant below reads zero rows.

-- `force row level security` binds the owner too, so the migration role needs its own permissive policy
-- (0004/0005/0025/0049 precedent); the alternative, BYPASSRLS, is rejected by DECISIONS F5.
drop policy if exists evaluation_owner_backfill on public.evaluation;
create policy evaluation_owner_backfill on public.evaluation
  for all to evidgate_owner using (true) with check (true);

-- 0001's default privileges hand `evidgate_app` full DML on every table the owner creates in `public`, so
-- the revoke has to come before the minimal grant. The app role writes a row once and reads it; the
-- operator keeps read only; the scheduler holds nothing (the sweep reads runs, never evaluations).
revoke insert, update, delete on public.evaluation from evidgate_app;
grant select, insert on public.evaluation to evidgate_app;
grant select on public.evaluation to evidgate_operator;
revoke all on public.evaluation from evidgate_worker_sched;
