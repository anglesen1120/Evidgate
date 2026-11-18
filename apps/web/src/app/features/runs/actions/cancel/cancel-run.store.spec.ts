import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { RunStateDto } from '@evidgate/sdk';
import ready from '../../../../../../../../fixtures/web-api/run-detail/input.json';
import { ApiRequestError, type ApiError } from '../../../../core/http/api-error';
import { SessionStore } from '../../../../core/session/session.store';
import { NarrowMutationPolicy } from '../../../../shared/ui/narrow/narrow-mutation-policy';
import { RunDataService } from '../../data/run-data.service';
import { RunDetailStore } from '../../detail/run-detail.store';
import { deferred } from '../../detail/run-detail-store.fixtures';
import { CancelRunStore, CANCEL_POLL_INTERVAL_MS, CANCEL_POLL_ATTEMPTS } from './cancel-run.store';

const collecting: RunStateDto = { ...ready.body, state: 'collecting' } as RunStateDto;
const failure = (patch: Partial<ApiError> = {}): ApiRequestError =>
  new ApiRequestError({
    kind: 'unavailable',
    status: 503,
    code: 'unavailable',
    message: 'Try again later.',
    requestId: 'cancel-request',
    retryable: true,
    retryAfterSeconds: null,
    details: [],
    ...patch,
  });

function setup() {
  const run = signal<RunStateDto | null>(collecting);
  const allowed = signal(true);
  const desktop = signal(true);
  const org = signal('org-1');
  const cancelRun = vi.fn<RunDataService['cancelRun']>().mockResolvedValue(undefined);
  const refreshRun = vi.fn<(id: string) => Promise<void>>().mockImplementation(() => {
    run.set({ ...collecting, state: 'canceled', version: collecting.version + 1 });
    return Promise.resolve();
  });
  const newIdempotencyKey = vi.fn().mockReturnValueOnce('intent-1').mockReturnValue('intent-2');
  TestBed.configureTestingModule({
    providers: [
      CancelRunStore,
      { provide: RunDataService, useValue: { cancelRun, newIdempotencyKey } },
      { provide: RunDetailStore, useValue: { run, refreshRun } },
      {
        provide: SessionStore,
        useValue: {
          has: () => allowed(),
          currentOrg: () => ({ id: org() }),
          currentProject: () => ({ id: 'project-1' }),
        },
      },
      {
        provide: NarrowMutationPolicy,
        useValue: {
          canMutate: desktop,
          allowsMutation: () => desktop(),
        },
      },
    ],
  });
  return {
    store: TestBed.inject(CancelRunStore),
    run,
    allowed,
    desktop,
    org,
    cancelRun,
    refreshRun,
    newIdempotencyKey,
  };
}

describe('CancelRunStore', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
  });

  it('does not submit without permission, with the allowed control submitting', async () => {
    const h = setup();
    h.allowed.set(false);
    h.store.open();
    await h.store.confirm(undefined);
    expect(h.cancelRun).not.toHaveBeenCalled();
    expect(h.store.opened()).toBe(false);
    h.allowed.set(true);
    h.store.open();
    await h.store.confirm(undefined);
    expect(h.cancelRun).toHaveBeenCalledTimes(1);
  });

  it.each(['complete', 'incomplete', 'canceled'] as const)(
    'refuses the already-terminal %s state',
    async (state) => {
      const h = setup();
      h.run.set({ ...collecting, state });
      expect(h.store.canCancel()).toBe(false);
      h.store.open();
      await h.store.confirm(undefined);
      expect(h.cancelRun).not.toHaveBeenCalled();
      h.run.set(collecting);
      h.store.open();
      await h.store.confirm(undefined);
      expect(h.cancelRun).toHaveBeenCalledTimes(1);
    },
  );

  it('records acceptance before a poll supplies the terminal state, never optimistically changing the run', async () => {
    const h = setup();
    h.refreshRun.mockResolvedValueOnce(undefined);
    h.store.open();
    expect(h.cancelRun).not.toHaveBeenCalled();
    await h.store.confirm('Waiting for a new build');
    expect(h.store.phase()).toBe('polling');
    expect(h.run()?.state).toBe('collecting');
    await vi.advanceTimersByTimeAsync(CANCEL_POLL_INTERVAL_MS);
    expect(h.store.phase()).toBe('canceled');
    expect(h.run()?.state).toBe('canceled');
    await vi.advanceTimersByTimeAsync(CANCEL_POLL_INTERVAL_MS * 2);
    expect(h.refreshRun).toHaveBeenCalledTimes(2);
  });

  it('retries the same frozen reason with one key, while a new dialog intent gets a fresh key', async () => {
    const h = setup();
    h.cancelRun.mockRejectedValueOnce(failure());
    h.store.open();
    await h.store.confirm('Keep this reason');
    await h.store.confirm('Edited after first send');
    expect(h.cancelRun.mock.calls.map((call) => [call[1], call[2]])).toEqual([
      ['Keep this reason', 'intent-1'],
      ['Keep this reason', 'intent-1'],
    ]);
    h.run.set({ ...collecting, run_id: 'next-run' });
    h.store.dismiss();
    h.store.open();
    await h.store.confirm(undefined);
    expect(h.cancelRun.mock.calls[2]?.[2]).toBe('intent-2');
  });

  it('waits Retry-After and never automatically resubmits the POST', async () => {
    const h = setup();
    h.cancelRun.mockRejectedValueOnce(
      failure({ kind: 'rate_limited', status: 429, retryAfterSeconds: 3 }),
    );
    h.store.open();
    await h.store.confirm(undefined);
    await h.store.confirm(undefined);
    expect(h.cancelRun).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(3000);
    expect(h.cancelRun).toHaveBeenCalledTimes(1);
    await h.store.confirm(undefined);
    expect(h.cancelRun).toHaveBeenCalledTimes(2);
  });

  it.each([
    'forbidden',
    'not_found',
    'conflict',
    'idempotency_conflict',
    'validation',
    'payload_too_large',
  ] as const)(
    'retains a typed %s refusal without offering an unsafe same-intent retry',
    async (kind) => {
      const h = setup();
      const error = failure({ kind, retryable: false }).error;
      h.cancelRun.mockRejectedValue(new ApiRequestError(error));
      h.store.open();
      await h.store.confirm(undefined);
      expect(h.store.error()).toEqual(error);
      expect(h.store.retryReady()).toBe(false);
      await h.store.confirm(undefined);
      expect(h.cancelRun).toHaveBeenCalledTimes(1);
    },
  );

  it('stops polling at the bound and refreshes only state, never sending another cancel', async () => {
    const h = setup();
    h.refreshRun.mockResolvedValue(undefined);
    h.store.open();
    await h.store.confirm(undefined);
    await vi.advanceTimersByTimeAsync(CANCEL_POLL_INTERVAL_MS * CANCEL_POLL_ATTEMPTS);
    expect(h.refreshRun).toHaveBeenCalledTimes(CANCEL_POLL_ATTEMPTS);
    expect(h.store.phase()).toBe('waiting');
    await h.store.checkState();
    expect(h.refreshRun).toHaveBeenCalledTimes(CANCEL_POLL_ATTEMPTS + 1);
    expect(h.cancelRun).toHaveBeenCalledTimes(1);
  });

  it('retains recorded acceptance on a failed poll and retries only the read', async () => {
    const h = setup();
    h.refreshRun.mockRejectedValueOnce(failure());
    h.store.open();
    await h.store.confirm(undefined);
    expect(h.store.phase()).toBe('poll-error');
    await h.store.checkState();
    expect(h.store.phase()).toBe('canceled');
    expect(h.cancelRun).toHaveBeenCalledTimes(1);
  });

  it('honors a concurrent non-canceled terminal result without claiming it was canceled', async () => {
    const h = setup();
    h.refreshRun.mockImplementationOnce(() => {
      h.run.set({ ...collecting, state: 'complete' });
      return Promise.resolve();
    });
    h.store.open();
    await h.store.confirm(undefined);
    expect(h.store.phase()).toBe('terminal');
    expect(h.run()?.state).toBe('complete');
  });

  it('rechecks narrow viewport and changed scope before sending', async () => {
    const h = setup();
    h.store.open();
    h.desktop.set(false);
    await h.store.confirm(undefined);
    expect(h.cancelRun).not.toHaveBeenCalled();
    h.desktop.set(true);
    h.org.set('org-2');
    await h.store.confirm(undefined);
    expect(h.cancelRun).not.toHaveBeenCalled();
  });

  it('keeps a pending submission open and continues accepted polling after dismissal', async () => {
    const h = setup();
    const pending = deferred<void>();
    h.cancelRun.mockReturnValue(pending.promise);
    h.refreshRun.mockResolvedValue(undefined);
    h.store.open();
    const request = h.store.confirm('Retain this intent');
    h.store.dismiss();
    expect(h.store.opened()).toBe(true);
    expect(h.store.phase()).toBe('submitting');
    pending.resolve();
    await request;
    h.store.dismiss();
    expect(h.store.opened()).toBe(false);
    await vi.advanceTimersByTimeAsync(CANCEL_POLL_INTERVAL_MS);
    expect(h.store.phase()).toBe('polling');
    expect(h.refreshRun).toHaveBeenCalledTimes(2);
    expect(h.cancelRun).toHaveBeenCalledTimes(1);
  });

  it('stops scheduled polling on destroy and aborts an in-flight submit', async () => {
    const h = setup();
    h.refreshRun.mockResolvedValue(undefined);
    h.store.open();
    await h.store.confirm(undefined);
    TestBed.resetTestingModule();
    await vi.advanceTimersByTimeAsync(CANCEL_POLL_INTERVAL_MS * 2);
    expect(h.refreshRun).toHaveBeenCalledTimes(1);
    const next = setup();
    const pending = deferred<void>();
    next.cancelRun.mockReturnValue(pending.promise);
    next.store.open();
    const request = next.store.confirm(undefined);
    const signal = next.cancelRun.mock.calls[0]?.[3];
    TestBed.resetTestingModule();
    expect(signal?.aborted).toBe(true);
    pending.resolve();
    await request;
  });
});
