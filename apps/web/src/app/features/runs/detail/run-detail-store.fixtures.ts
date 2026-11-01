import type { RunAttemptPage, RunStateDto } from '@evidgate/sdk';
import page1 from '../../../../../../../fixtures/web-api/run-attempts-page-1/input.json';
import page2 from '../../../../../../../fixtures/web-api/run-attempts-page-2/input.json';
import ready from '../../../../../../../fixtures/web-api/run-detail/input.json';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';

/** Shared by both run-detail store specs; the fixture-contract spec validates `run`. */
export const run = ready.body as RunStateDto;
export const first = page1.body as RunAttemptPage;
export const second = page2.body as RunAttemptPage;
/** One pending body: `status` set, every evaluation-derived member null. */
export function pendingBody(overrides: Record<string, unknown> = {}) {
  return {
    status: 'pending',
    decision: null,
    evaluation_id: null,
    evidence_revision: null,
    completeness: null,
    outcome: null,
    missing: null,
    reasons: null,
    policy_hash: null,
    manifest_hash: null,
    evaluator_version: null,
    mode: null,
    url: null,
    ...overrides,
  };
}
/** One evaluated live body, member for member. */
export function liveBody(overrides: Record<string, unknown> = {}) {
  return pendingBody({
    status: 'evaluated',
    decision: 'FAIL',
    evaluation_id: 'evaluation-1',
    evidence_revision: 4,
    completeness: 'incomplete',
    outcome: 'has_failures',
    missing: ['missing_shard'],
    reasons: ['critical_not_executed'],
    policy_hash: 'sha256:policy',
    manifest_hash: 'sha256:manifest',
    evaluator_version: '1.4.0',
    mode: 'live',
    ...overrides,
  });
}
export function refused(
  kind: 'not_found' | 'forbidden' | 'unauthenticated' | 'unavailable',
  status?: number,
): ApiRequestError {
  const defaultStatus =
    kind === 'not_found'
      ? 404
      : kind === 'unauthenticated'
        ? 401
        : kind === 'unavailable'
          ? 503
          : 403;
  const error: ApiError = {
    kind,
    status: status ?? defaultStatus,
    code: kind,
    message: 'Resource unavailable.',
    requestId: 'request-detail',
    retryable: kind === 'unavailable',
    details: [],
    retryAfterSeconds: null,
  };
  return new ApiRequestError(error);
}
export function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((yes, no) => {
    resolve = yes;
    reject = no;
  });
  return { promise, resolve, reject };
}
