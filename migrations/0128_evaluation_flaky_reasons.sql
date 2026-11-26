-- 0128_evaluation_flaky_reasons.sql
--
-- WHY: the evaluator now reads the attempt history of a frozen revision. A case that failed and then passed on a retry
-- is flaky; a flaky critical case fails the verdict and flaky cases beyond the policy budget downgrade a pass. The
-- stored row must name those outcomes, and `evaluation_reasons_check` (0059, widened by 0109 and 0124) is a closed list
-- that does not name the two new reasons, `flaky_critical_retry_pass` and `flaky_within_budget`.
--
-- SHAPE: the reasons CHECK is the list of 0124 plus the two tokens, in the CASE shape of 0059 because `array_position`
-- raises on a multi-dimensional array and CHECK term order is not guaranteed, so the dimension test has to run first.
-- A NULL element is refused too. No column changes: flaky facts are an in-memory input to the verdict, not stored.
--
-- GRANTS: none changed. `evidgate_app` keeps table-level SELECT and INSERT on `evaluation`; no UPDATE or DELETE path
-- exists for any role of the application.
--
-- LOCKS: ACCESS EXCLUSIVE on `evaluation` for one `alter table` statement in one transaction, `lock_timeout` 5 s so
-- the file fails instead of queuing behind a long reader. The drop and the add run in one statement, so the table is
-- scanned once under the lock; existing rows hold only tokens of the old list, so the check validates in place and the
-- table is insert-only, growing one row per evaluation.
--
-- Re-run safe: `drop constraint if exists` before the add.

set local lock_timeout = '5s';

alter table public.evaluation
  drop constraint if exists evaluation_reasons_check,
  add constraint evaluation_reasons_check check (
    case
      when array_ndims(reasons) is null or array_ndims(reasons) = 1 then
        reasons <@ array['scope_build_policy_pinned', 'required_assertion_failed', 'missing_shard',
                        'count_mismatch', 'artifact_missing', 'build_mismatch', 'unknown_status',
                        'setup_error', 'stale_epoch_only', 'missing_result', 'expected_count_zero',
                        'empty_suite_allowed', 'critical_not_executed', 'unknown_required_artifact_kind',
                        'flaky_budget_exceeded', 'advisory_only_run', 'quarantined_case_failed',
                        'waiver_approved', 'flaky_critical_retry_pass', 'flaky_within_budget']::text[]
        and array_position(reasons, null) is null
      else false
    end);
