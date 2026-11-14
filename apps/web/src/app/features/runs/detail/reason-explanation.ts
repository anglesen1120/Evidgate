import { MISSING_CODES, type MissingCode } from './decision/decision-view-model';

/** The diagnostic code remains visible beside this explanation on both evidence surfaces. */
export function missingReasonExplanation(code: MissingCode): string {
  switch (code) {
    case 'missing_shard':
      return $localize`:@@runs.evidence.reason.missing-shard:Shard evidence is missing.`;
    case 'count_mismatch':
      return $localize`:@@runs.evidence.reason.count-mismatch:Collected and expected counts do not match.`;
    case 'artifact_missing':
      return $localize`:@@runs.evidence.reason.artifact-missing:A required artifact is missing.`;
    case 'build_mismatch':
      return $localize`:@@runs.evidence.reason.build-mismatch:The reported build does not match the manifest.`;
    case 'unknown_status':
      return $localize`:@@runs.evidence.reason.unknown-status:A reported result status is not recognized.`;
    case 'setup_error':
      return $localize`:@@runs.evidence.reason.setup-error:Test setup reported an error.`;
    case 'stale_epoch':
      return $localize`:@@runs.evidence.reason.stale-epoch:Evidence belongs to a stale producer epoch.`;
    case 'missing_result':
      return $localize`:@@runs.evidence.reason.missing-result:An expected test result is missing.`;
  }
}

export function decisionReasonExplanation(code: string): string | null {
  const missing = MISSING_CODES.find((known) => known === code);
  if (missing !== undefined) return missingReasonExplanation(missing);
  switch (code) {
    case 'scope_build_policy_pinned':
      return $localize`:@@runs.decision.reason.scope-build-policy-pinned:The pinned scope, build and policy passed evaluation.`;
    case 'required_assertion_failed':
      return $localize`:@@runs.decision.reason.required-assertion-failed:A required assertion failed.`;
    case 'stale_epoch_only':
      return $localize`:@@runs.decision.reason.stale-epoch-only:Only evidence from stale producer epochs was received.`;
    case 'expected_count_zero':
      return $localize`:@@runs.decision.reason.expected-count-zero:The required scope expects no tests, and the policy does not allow an empty suite.`;
    case 'empty_suite_allowed':
      return $localize`:@@runs.decision.reason.empty-suite-allowed:The suite is empty, as permitted by the policy.`;
    case 'critical_not_executed':
      return $localize`:@@runs.decision.reason.critical-not-executed:A test required by the policy was skipped or blocked.`;
    case 'unknown_required_artifact_kind':
      return $localize`:@@runs.decision.reason.unknown-required-artifact-kind:The policy requires an unrecognized artifact kind.`;
    case 'flaky_budget_exceeded':
      return $localize`:@@runs.decision.reason.flaky-budget-exceeded:Non-critical retry changes exceeded the policy's flaky-test budget.`;
    case 'advisory_only_run':
      return $localize`:@@runs.decision.reason.advisory-only-run:This fork run is advisory only and cannot produce a passing decision.`;
    default:
      return null;
  }
}
