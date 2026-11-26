-- 0124_evaluation_quarantine_waiver_effects.sql
--
-- WHY: the evaluator now honours exceptions. A failed required case under an active quarantine entry no longer gates
-- the verdict, and an approved case waiver annotates a FAIL without changing it. The stored row must say which cases
-- were excluded and why, and `evaluation_reasons_check` (0059, widened by 0109) is a closed list that does not name the
-- two new reasons, `quarantined_case_failed` and `waiver_approved`.
--
-- SHAPE: one new column, `quarantined_cases uuid[] not null default '{}'`: the catalog case ids whose confirmed failure
-- was excluded, sorted ascending by the writer. It is a plain uuid array with no foreign key on purpose: the row is an
-- immutable verdict and a case id is an audit fact that must outlive the case. The reasons CHECK is the list of 0109
-- plus the two tokens. Both checks use the CASE shape of 0059 because `array_position` raises on a multi-dimensional
-- array and CHECK term order is not guaranteed, so the dimension test has to run first. A NULL element is refused too.
--
-- GRANTS: none changed. `evidgate_app` holds table-level SELECT and INSERT on `evaluation` (0059), so the new column
-- is insertable like the others; no UPDATE or DELETE path exists for any role of the application.
--
-- DEFAULT: a constant default, so every existing row reads `{}` without a table rewrite. The existing rows hold only
-- tokens of the old list and no case ids, so both checks validate in place; the table is insert-only and small per run.
--
-- LOCKS: ACCESS EXCLUSIVE on `evaluation` for the add column, the drops and the adds, one transaction, `lock_timeout`
-- 5 s so the file fails instead of queuing behind a long reader.
--
-- Re-run safe: `add column if not exists` and `drop constraint if exists` before each add.

set local lock_timeout = '5s';

alter table public.evaluation add column if not exists quarantined_cases uuid[] not null default '{}';

alter table public.evaluation drop constraint if exists evaluation_quarantined_cases_check;

alter table public.evaluation add constraint evaluation_quarantined_cases_check check (
  case
    when array_ndims(quarantined_cases) is null or array_ndims(quarantined_cases) = 1 then
      array_position(quarantined_cases, null::uuid) is null
    else false
  end);

alter table public.evaluation drop constraint if exists evaluation_reasons_check;

alter table public.evaluation add constraint evaluation_reasons_check check (
  case
    when array_ndims(reasons) is null or array_ndims(reasons) = 1 then
      reasons <@ array['scope_build_policy_pinned', 'required_assertion_failed', 'missing_shard',
                      'count_mismatch', 'artifact_missing', 'build_mismatch', 'unknown_status',
                      'setup_error', 'stale_epoch_only', 'missing_result', 'expected_count_zero',
                      'empty_suite_allowed', 'critical_not_executed', 'unknown_required_artifact_kind',
                      'flaky_budget_exceeded', 'advisory_only_run', 'quarantined_case_failed',
                      'waiver_approved']::text[]
      and array_position(reasons, null) is null
    else false
  end);
