// The Policy tab's two project-level reads, in isolation: what each answer settles to, that a read
// of an older generation writes nothing, that a head refresh after a publish discards the answer it
// superseded, and that a server `Retry-After` closes the retry until its interval ends. Two
// failures sharing one wait are `policy-reads.retry-after.spec.ts`, the harness both specs use is
// `policy-reads.test-support.ts`, the request side is `policy-data.service.spec.ts`, and the tab
// that renders these states is `policy-summary.component.spec.ts`.

import { TestBed } from '@angular/core/testing';
import type { PolicyActivationStateDto, PolicyRevisionSummaryDto } from '@evidgate/sdk';

import { PolicyDataService, type PolicyHead } from '../../../core/data/policy-data.service';
import { PolicyProjectReads } from './policy-reads';
import {
  ACTIVATION,
  FORBIDDEN,
  HEAD,
  NOT_FOUND,
  OTHER_PROJECT_ID,
  PROJECT_ID,
  RATE_LIMITED,
  THROTTLED,
  UNAVAILABLE,
  deferred,
  FakePolicyDataService,
  flushMicrotasks,
} from './policy-reads.test-support';

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

  it('reports busy while either read is in flight', async () => {
    const head = deferred<PolicyHead>();
    data.head = () => head.promise;

    start();
    expect(reads.busy()).toBe(true);
    await flushMicrotasks();
    expect(reads.busy()).toBe(true);

    head.resolve({ revision: HEAD, headRevisionId: HEAD.id });
    await flushMicrotasks();
    expect(reads.busy()).toBe(false);
  });

  it('closes the retry until the server Retry-After has passed, and retries nothing itself', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(0);
    data.activation = () => Promise.reject(THROTTLED);
    try {
      start();
      await flushMicrotasks();

      expect(reads.activation().phase).toBe('error');
      expect(reads.retryAfterSeconds()).toBe(30);
      expect(reads.waiting()).toBe(true);
      reads.retry();
      expect(data.activationCalls).toEqual([PROJECT_ID]);

      vi.advanceTimersByTime(29_999);
      expect(reads.waiting()).toBe(true);
      reads.retry();
      expect(data.activationCalls).toEqual([PROJECT_ID]);

      // The interval ends: the control opens, but no read is started from the timer.
      vi.advanceTimersByTime(1);
      expect(reads.waiting()).toBe(false);
      expect(data.activationCalls).toEqual([PROJECT_ID]);

      data.activation = () => Promise.resolve(ACTIVATION);
      reads.retry();
      expect(data.activationCalls).toEqual([PROJECT_ID, PROJECT_ID]);
    } finally {
      vi.useRealTimers();
    }
  });

  it('opens the retry at once for a failure that carries no interval', async () => {
    data.activation = () => Promise.reject(UNAVAILABLE);
    start();
    await flushMicrotasks();

    expect(reads.activation().phase).toBe('error');
    expect(reads.retryAfterSeconds()).toBeNull();
    expect(reads.waiting()).toBe(false);

    data.activation = () => Promise.resolve(ACTIVATION);
    reads.retry();
    await flushMicrotasks();
    expect(reads.activation().phase).toBe('ready');
  });

  it('ignores a retry while a read is in flight', () => {
    const activation = deferred<PolicyActivationStateDto>();
    data.activation = () => activation.promise;
    start();
    reads.retry();

    expect(data.activationCalls).toEqual([PROJECT_ID]);
    expect(data.headCalls).toEqual([PROJECT_ID]);
  });

  it('re-reads only the head of the project the view belongs to', async () => {
    start();
    await flushMicrotasks();

    reads.refreshHead(PROJECT_ID);
    await flushMicrotasks();

    expect(data.activationCalls).toEqual([PROJECT_ID]);
    expect(data.headCalls).toEqual([PROJECT_ID, PROJECT_ID]);
    expect(reads.head().phase).toBe('ready');
  });

  it('changes nothing when a publish answered for another project', async () => {
    start();
    await flushMicrotasks();

    reads.refreshHead(OTHER_PROJECT_ID);
    await flushMicrotasks();

    expect(data.headCalls).toEqual([PROJECT_ID]);
  });

  it('discards the head answer a publish superseded, and keeps a pending activation', async () => {
    const activation = deferred<PolicyActivationStateDto>();
    const staleHead = deferred<PolicyHead>();
    data.activation = () => activation.promise;
    data.head = () => staleHead.promise;
    start();

    const published: PolicyRevisionSummaryDto = { ...HEAD, revision_no: 4, version: 3 };
    data.head = () => Promise.resolve({ revision: published, headRevisionId: published.id });
    reads.refreshHead(PROJECT_ID);
    await flushMicrotasks();
    expect(reads.head().value).toBe(published);

    staleHead.resolve({ revision: HEAD, headRevisionId: HEAD.id });
    activation.resolve(ACTIVATION);
    await flushMicrotasks();

    // The stale head wrote nothing; the activation read of the same start still landed.
    expect(reads.head().value).toBe(published);
    expect(reads.activation().value).toBe(ACTIVATION);
  });
});
