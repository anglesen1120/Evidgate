// The harness the two project-reads specs share: one project, the typed failures the adapter
// builds from a server envelope, and a data service whose two answers the test controls.

import type {
  ErrorEnvelope,
  PolicyActivationStateDto,
  PolicyRevisionSummaryDto,
} from '@evidgate/sdk';

import type { PolicyHead } from '../../../core/data/policy-data.service';
import { ApiRequestError, type ApiErrorKind } from '../../../core/http/api-error';

export const PROJECT_ID = '018f0000-0000-7000-8000-000000000011';
export const OTHER_PROJECT_ID = '018f0000-0000-7000-8000-000000000012';

export const ACTIVATION: PolicyActivationStateDto = {
  project_id: PROJECT_ID,
  policy_revision_id: '018f0000-0000-7000-8000-00000000a004',
  hash: 'sha256:f250bbce48fca2a9a5f93b45236c0f330bb471f5d3f0d1396ddc5ac964928064',
  mode: 'advisory',
  activated_by: '018f0000-0000-7000-8000-0000000000e1',
  activated_at: '2026-10-26T08:22:00Z',
};

export const HEAD: PolicyRevisionSummaryDto = {
  id: '018f0000-0000-7000-8000-00000000a003',
  revision_no: 3,
  hash: 'sha256:d05c091a94bfe1c59087f5a38ef026b200d2edcbee0fab69a8d080fb461e377e',
  state: 'active',
  created_by: '018f0000-0000-7000-8000-0000000000e1',
  created_at: '2026-10-08T06:45:00Z',
  version: 2,
};

/** The typed failure the adapter builds from one envelope. */
function typedFailure(
  envelope: ErrorEnvelope,
  kind: ApiErrorKind,
  status: number,
  retryAfterSeconds: number | null = null,
): ApiRequestError {
  return new ApiRequestError({
    kind,
    status,
    code: envelope.error.code,
    message: envelope.error.message,
    requestId: envelope.error.request_id,
    retryable: envelope.error.retryable,
    details: envelope.error.details ?? [],
    retryAfterSeconds,
  });
}

export const NOT_FOUND = typedFailure(
  {
    error: {
      code: 'not_found',
      message: 'the requested resource does not exist',
      request_id: '018f0000-0000-7000-8000-00000000a10c',
      retryable: false,
      details: [],
    },
  },
  'not_found',
  404,
);

export const FORBIDDEN = typedFailure(
  {
    error: {
      code: 'forbidden',
      message: 'the principal is not allowed to perform this operation',
      request_id: '018f0000-0000-7000-8000-00000000a10d',
      retryable: false,
      details: [],
    },
  },
  'forbidden',
  403,
);

const THROTTLE_ENVELOPE: ErrorEnvelope = {
  error: {
    code: 'rate_limited',
    message: 'the read burst limit for this project was reached',
    request_id: '018f0000-0000-7000-8000-00000000a10e',
    retryable: true,
    details: [],
  },
};

const BUSY_ENVELOPE: ErrorEnvelope = {
  error: {
    code: 'unavailable',
    message: 'a dependency is busy',
    request_id: '018f0000-0000-7000-8000-00000000a10f',
    retryable: true,
    details: [],
  },
};

/** The 429 as the server sends it: with its own `Retry-After`, which closes the retry. */
export function throttled(retryAfterSeconds: number | null = null): ApiRequestError {
  return typedFailure(THROTTLE_ENVELOPE, 'rate_limited', 429, retryAfterSeconds);
}

/** The same 429 without an interval: it leaves the retry open. */
export const RATE_LIMITED = throttled();

/** The same 429 closing the retry for the test's interval. */
export const THROTTLED = throttled(30);

/** The 503, with the server's interval when it sends one. */
export function unavailable(retryAfterSeconds: number | null = null): ApiRequestError {
  return typedFailure(BUSY_ENVELOPE, 'unavailable', 503, retryAfterSeconds);
}

/** The 503 without an interval. */
export const UNAVAILABLE = unavailable();

export type Deferred<T> = {
  readonly promise: Promise<T>;
  readonly resolve: (value: T) => void;
  readonly reject: (error: unknown) => void;
};

/**
 * One deferred answer, so a test controls exactly when a read settles. The project's lib target is
 * ES2022, so `Promise.withResolvers` is not available: the executor form is how the resolve/reject
 * handles escape to the test.
 */
export function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((settle, fail) => {
    resolve = settle;
    reject = fail;
  });
  return { promise, resolve, reject };
}

/** The two project reads the tab performs, each answer under the test's control. */
export class FakePolicyDataService {
  readonly activationCalls: string[] = [];
  readonly headCalls: string[] = [];
  activation: () => Promise<PolicyActivationStateDto> = () => Promise.resolve(ACTIVATION);
  head: () => Promise<PolicyHead> = () =>
    Promise.resolve({ revision: HEAD, headRevisionId: HEAD.id });

  getActivation(projectId: string): Promise<PolicyActivationStateDto> {
    this.activationCalls.push(projectId);
    return this.activation();
  }

  getHead(projectId: string): Promise<PolicyHead> {
    this.headCalls.push(projectId);
    return this.head();
  }
}

/**
 * Both reads are fire-and-forget, so a test has to let the queued microtasks run before it can see
 * what an answer wrote. Four ticks cover the `settledRead` hop plus the read's own `await`.
 */
export async function flushMicrotasks(): Promise<void> {
  for (let tick = 0; tick < 4; tick += 1) {
    await Promise.resolve();
  }
}
