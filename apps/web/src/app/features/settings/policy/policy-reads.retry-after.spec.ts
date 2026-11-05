// The one wait the two reads share: they settle independently, so a `Retry-After` armed by one
// must survive the other's failure — an answer without a usable interval leaves it alone, a
// shorter interval never shortens it, and a longer one extends it. One timer carries the wait and
// never starts a read of its own.

import { TestBed } from '@angular/core/testing';
import type { PolicyActivationStateDto } from '@evidgate/sdk';

import { PolicyDataService, type PolicyHead } from '../../../core/data/policy-data.service';
import { PolicyProjectReads } from './policy-reads';
import {
  PROJECT_ID,
  type Deferred,
  deferred,
  FakePolicyDataService,
  flushMicrotasks,
  throttled,
  unavailable,
} from './policy-reads.test-support';

describe('PolicyProjectReads retry wait', () => {
  let data: FakePolicyDataService;
  let reads: PolicyProjectReads;
  let activation: Deferred<PolicyActivationStateDto>;
  let head: Deferred<PolicyHead>;

  const start = (): void => {
    reads.start(PROJECT_ID, () => undefined);
  };

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(0);
    data = new FakePolicyDataService();
    activation = deferred<PolicyActivationStateDto>();
    head = deferred<PolicyHead>();
    data.activation = () => activation.promise;
    data.head = () => head.promise;
    TestBed.configureTestingModule({
      providers: [PolicyProjectReads, { provide: PolicyDataService, useValue: data }],
    });
    reads = TestBed.inject(PolicyProjectReads);
  });

  afterEach(() => {
    vi.useRealTimers();
    TestBed.resetTestingModule();
  });

  it('keeps the wait an activation failure armed when the head fails without an interval', async () => {
    start();
    activation.reject(throttled(30));
    await flushMicrotasks();
    expect(reads.waiting()).toBe(true);

    head.reject(unavailable());
    await flushMicrotasks();

    expect(reads.head().phase).toBe('error');
    expect(reads.retryAfterSeconds()).toBe(30);
    expect(reads.waiting()).toBe(true);

    vi.advanceTimersByTime(29_999);
    reads.retry();
    expect(data.activationCalls).toEqual([PROJECT_ID]);
    expect(data.headCalls).toEqual([PROJECT_ID]);

    // The control opens only when the interval ends, and the timer itself reads nothing.
    vi.advanceTimersByTime(1);
    expect(reads.waiting()).toBe(false);
    reads.retry();
    expect(data.activationCalls).toEqual([PROJECT_ID, PROJECT_ID]);
    expect(data.headCalls).toEqual([PROJECT_ID, PROJECT_ID]);
  });

  it('never shortens a wait when a later failure serves a shorter interval', async () => {
    start();
    activation.reject(throttled(60));
    await flushMicrotasks();
    head.reject(unavailable(10));
    await flushMicrotasks();

    expect(reads.retryAfterSeconds()).toBe(60);
    vi.advanceTimersByTime(10_000);
    expect(reads.waiting()).toBe(true);
    reads.retry();
    expect(data.activationCalls).toEqual([PROJECT_ID]);

    vi.advanceTimersByTime(49_999);
    expect(reads.waiting()).toBe(true);
    vi.advanceTimersByTime(1);
    expect(reads.waiting()).toBe(false);
  });

  it('extends a wait when a later failure serves a longer interval', async () => {
    start();
    activation.reject(throttled(10));
    await flushMicrotasks();
    head.reject(unavailable(60));
    await flushMicrotasks();

    expect(reads.retryAfterSeconds()).toBe(60);
    // The replaced timer would have ended the wait here.
    vi.advanceTimersByTime(10_000);
    expect(reads.waiting()).toBe(true);
    reads.retry();
    expect(data.activationCalls).toEqual([PROJECT_ID]);

    vi.advanceTimersByTime(49_999);
    expect(reads.waiting()).toBe(true);
    vi.advanceTimersByTime(1);
    expect(reads.waiting()).toBe(false);
  });
});
