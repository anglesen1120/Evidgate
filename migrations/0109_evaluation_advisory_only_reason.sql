-- 0109_evaluation_advisory_only_reason.sql
--
-- WHY: the evaluator holds an otherwise passing fork run at INCONCLUSIVE with the reason `advisory_only_run`, and the
-- `evaluation_reasons_check` of 0059 is a closed list that does not name it, so the handler could not store that verdict.
--
-- SHAPE: the CHECK is replaced by the same expression with one more token; no column, index or data changes. The
-- existing rows only hold tokens of the old list, so the new, wider list accepts all of them and the ADD validates
-- without a rewrite of the data (the `evaluation` table is insert-only and small per run).
--
-- VALIDATION: the add is validated in place rather than NOT VALID plus VALIDATE; the table is insert-only and small, and
-- ACCESS EXCLUSIVE is held to the commit either way.
--
-- LOCKS: ACCESS EXCLUSIVE on `evaluation` for the drop and the add, one transaction, `lock_timeout` 5 s so the file
-- fails instead of queuing behind a long reader.

set local lock_timeout = '5s';

alter table public.evaluation drop constraint if exists evaluation_reasons_check;

alter table public.evaluation add constraint evaluation_reasons_check check (
  case
    when array_ndims(reasons) is null or array_ndims(reasons) = 1 then
      reasons <@ array['scope_build_policy_pinned', 'required_assertion_failed', 'missing_shard',
                      'count_mismatch', 'artifact_missing', 'build_mismatch', 'unknown_status',
                      'setup_error', 'stale_epoch_only', 'missing_result', 'expected_count_zero',
                      'empty_suite_allowed', 'critical_not_executed', 'unknown_required_artifact_kind',
                      'flaky_budget_exceeded', 'advisory_only_run']::text[]
      and array_position(reasons, null) is null
    else false
  end);
