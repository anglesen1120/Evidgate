// The Policy tab's two project-level reads, in isolation: what each answer settles to, and that a
// read of an older generation writes nothing. The request side is `policy-data.service.spec.ts`;
// the tab that renders these states is `policy-summary.component.spec.ts`.

import { TestBed } from '@angular/core/testing';
import type {
  ErrorEnvelope,
  PolicyActivationStateDto,
  PolicyRevisionSummaryDto,
} from '@evidgate/sdk';

import { PolicyDataService, type PolicyHead } from '../../../core/data/policy-data.service';
import { ApiRequestError, type ApiErrorKind } from '../../../core/http/api-error';
import { PolicyProjectReads } from './policy-reads';

const PROJECT_ID = '018f0000-0000-7000-8000-000000000011';
const OTHER_PROJECT_ID = '018f0000-0000-7000-8000-000000000012';

const ACTIVATION: PolicyActivationStateDto = {
  project_id: PROJECT_ID,
  policy_revision_id: '018f0000-0000-7000-8000-00000000a004',
  hash: 'sha256:f250bbce48fca2a9a5f93b45236c0f330bb471f5d3f0d1396ddc5ac964928064',
  mode: 'advisory',
  activated_by: '018f0000-0000-7000-8000-0000000000e1',
  activated_at: '2026-10-26T08:22:00Z',
};

const HEAD: PolicyRevisionSummaryDto = {
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
): ApiRequestError {
  return new ApiRequestError({
    kind,
    status,
    code: envelope.error.code,
    message: envelope.error.message,
    requestId: envelope.error.request_id,
    retryable: envelope.error.retryable,
    details: envelope.error.details ?? [],
    retryAfterSeconds: null,
  });
}

const NOT_FOUND = typedFailure(
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

const FORBIDDEN = typedFailure(
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

const RATE_LIMITED = typedFailure(
  {
    error: {
      code: 'rate_limited',
      message: 'the read burst limit for this project was reached',
      request_id: '018f0000-0000-7000-8000-00000000a10e',
      retryable: true,
      details: [],
    },
  },
  'rate_limited',
  429,
);

type Deferred<T> = {
  readonly promise: Promise<T>;
  readonly resolve: (value: T) => void;
  readonly reject: (error: unknown) => void;
};

/**
 * One deferred answer, so a test controls exactly when a read settles. The project's lib target is
 * ES2022, so `Promise.withResolvers` is not available: the executor form is how the resolve/reject
 * handles escape to the test (the same spelling `policy.store.spec.ts` uses).
 */
function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((settle, fail) => {
    resolve = settle;
    reject = fail;
  });
  return { promise, resolve, reject };
}

/** The two project reads the tab performs, each answer under the test's control. */
class FakePolicyDataService {
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
async function flushMicrotasks(): Promise<void> {
  for (let tick = 0; tick < 4; tick += 1) {
    await Promise.resolve();
  }
}

describe('PolicyProjectReads', () => {
  let data: FakePolicyDataService;
  let reads: PolicyProjectReads;
  let applied: (PolicyActivationStateDto | null)[];

  const start = (projectId: string = PROJECT_ID): void => {
    reads.start(projectId, (activation) => applied.push(activation));
  };

  beforeEach(() => {
    data = new FakePolicyDataService();
    applied = [];
    TestBed.configureTestingModule({
      providers: [PolicyProjectReads, { provide: PolicyDataService, useValue: data }],
    });
    reads = TestBed.inject(PolicyProjectReads);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('starts both reads for the project it was given and reports them loading', () => {
    const activation = deferred<PolicyActivationStateDto>();
    const head = deferred<PolicyHead>();
    data.activation = () => activation.promise;
    data.head = () => head.promise;

    start();

    expect(data.activationCalls).toEqual([PROJECT_ID]);
    expect(data.headCalls).toEqual([PROJECT_ID]);
    expect(reads.activation().phase).toBe('loading');
    expect(reads.head().phase).toBe('loading');
  });

  it('serves an advisory activation verbatim and hands it to the seam', async () => {
    start();
    await flushMicrotasks();

    expect(reads.activation()).toEqual({ phase: 'ready', value: ACTIVATION, error: null });
    expect(applied).toEqual([null, ACTIVATION]);
  });

  it('serves an enforcing activation without turning it into the advisory mode', async () => {
    data.activation = () => Promise.resolve({ ...ACTIVATION, mode: 'enforcing' });

    start();
    await flushMicrotasks();

    expect(reads.activation().value?.mode).toBe('enforcing');
    expect(applied.at(-1)?.mode).toBe('enforcing');
  });

  it('settles a 404 activation as absent, not as a failure', async () => {
    data.activation = () => Promise.reject(NOT_FOUND);

    start();
    await flushMicrotasks();

    expect(reads.activation()).toEqual({ phase: 'empty', value: null, error: null });
    expect(applied).toEqual([null, null]);
  });

  it('keeps a 403 activation as its own forbidden state, with the refusal', async () => {
    data.activation = () => Promise.reject(FORBIDDEN);

    start();
    await flushMicrotasks();

    expect(reads.activation().phase).toBe('forbidden');
    expect(reads.activation().error?.kind).toBe('forbidden');
    expect(applied.at(-1)).toBeNull();
  });

  it('turns a 5xx or throttled activation into an error the page can retry', async () => {
    data.activation = () => Promise.reject(RATE_LIMITED);

    start();
    await flushMicrotasks();

    expect(reads.activation().phase).toBe('error');
    expect(reads.activation().error?.code).toBe('rate_limited');
  });

  it('turns a rejection that is not the server answer into an error without copy', async () => {
    data.activation = () => Promise.reject(new Error('bug'));

    start();
    await flushMicrotasks();

    expect(reads.activation()).toEqual({ phase: 'error', value: null, error: null });
  });

  it('serves the head revision of the project', async () => {
    start();
    await flushMicrotasks();

    expect(reads.head()).toEqual({ phase: 'ready', value: HEAD, error: null });
  });

  it('settles a 404 head as no revisions, not as a failure', async () => {
    data.head = () => Promise.reject(NOT_FOUND);

    start();
    await flushMicrotasks();

    expect(reads.head()).toEqual({ phase: 'empty', value: null, error: null });
  });

  it('lets one read settle while the other is still loading', async () => {
    const head = deferred<PolicyHead>();
    data.head = () => head.promise;

    start();
    await flushMicrotasks();

    expect(reads.activation().phase).toBe('ready');
    expect(reads.head().phase).toBe('loading');

    head.resolve({ revision: HEAD, headRevisionId: HEAD.id });
    await flushMicrotasks();
    expect(reads.head().phase).toBe('ready');
  });

  it('ignores an answer of a superseded generation', async () => {
    const first = deferred<PolicyActivationStateDto>();
    const firstHead = deferred<PolicyHead>();
    data.activation = () => first.promise;
    data.head = () => firstHead.promise;
    start();

    data.activation = () => Promise.resolve({ ...ACTIVATION, mode: 'enforcing' });
    data.head = () => Promise.resolve({ revision: HEAD, headRevisionId: HEAD.id });
    start(OTHER_PROJECT_ID);
    await flushMicrotasks();
    first.resolve(ACTIVATION);
    firstHead.resolve({ revision: HEAD, headRevisionId: HEAD.id });
    await flushMicrotasks();

    expect(data.activationCalls).toEqual([PROJECT_ID, OTHER_PROJECT_ID]);
    expect(reads.activation().value?.mode).toBe('enforcing');
  });

  it('writes nothing once the view was cleared', async () => {
    const activation = deferred<PolicyActivationStateDto>();
    data.activation = () => activation.promise;
    start();
    reads.clear();

    activation.resolve(ACTIVATION);
    await flushMicrotasks();

    expect(reads.activation()).toEqual({ phase: 'idle', value: null, error: null });
    expect(reads.head().phase).toBe('idle');
    expect(applied).toEqual([null]);
  });

  it('starts from idle and reads nothing before start', () => {
    expect(reads.activation().phase).toBe('idle');
    expect(reads.head().phase).toBe('idle');
    expect(data.activationCalls).toEqual([]);
    expect(data.headCalls).toEqual([]);
  });
});
