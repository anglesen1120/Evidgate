import { clearTranslations, loadTranslations } from '@angular/localize';
import { MISSING_CODES } from './decision/decision-view-model';
import { decisionReasonExplanation, missingReasonExplanation } from './reason-explanation';

const DECISION_REASONS = [
  ['scope_build_policy_pinned', 'The pinned scope, build and policy passed evaluation.'],
  ['required_assertion_failed', 'A required assertion failed.'],
  ['stale_epoch_only', 'Only evidence from stale producer epochs was received.'],
  [
    'expected_count_zero',
    'The required scope expects no tests, and the policy does not allow an empty suite.',
  ],
  ['empty_suite_allowed', 'The suite is empty, as permitted by the policy.'],
  ['critical_not_executed', 'A test required by the policy was skipped or blocked.'],
  ['unknown_required_artifact_kind', 'The policy requires an unrecognized artifact kind.'],
  ['flaky_budget_exceeded', "Non-critical retry changes exceeded the policy's flaky-test budget."],
  ['advisory_only_run', 'This fork run is advisory only and cannot produce a passing decision.'],
] as const;

describe('decisionReasonExplanation', () => {
  afterEach(() => clearTranslations());

  it.each(MISSING_CODES)('reuses the evidence explanation for %s', (code) => {
    expect(decisionReasonExplanation(code)).toBe(missingReasonExplanation(code));
    expect(decisionReasonExplanation(code)).not.toBeNull();
  });

  it.each(DECISION_REASONS)('explains known decision reason %s', (code, explanation) => {
    expect(decisionReasonExplanation(code)).toBe(explanation);
  });

  it.each(['future_reason', '__proto__', 'constructor', '', '<script>'])(
    'does not invent an explanation for %s',
    (code) => expect(decisionReasonExplanation(code)).toBeNull(),
  );

  it('shares translated evidence text with Decision without a second mapping', () => {
    loadTranslations({
      'runs.evidence.reason.missing-result': 'Thiếu một kết quả kiểm thử dự kiến.',
    });
    expect(missingReasonExplanation('missing_result')).toBe('Thiếu một kết quả kiểm thử dự kiến.');
    expect(decisionReasonExplanation('missing_result')).toBe('Thiếu một kết quả kiểm thử dự kiến.');
  });
});
