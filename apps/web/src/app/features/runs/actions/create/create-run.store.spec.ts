import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiRequestError } from '../../../../core/http/api-error';
import { RunDataService } from '../../data/run-data.service';
import type { CreatedRun } from '../../data/run-actions.types';
import { CreateRunStore } from './create-run.store';

const INPUT = { manifest_id: '018f0000-0000-7000-8000-000000000011' };
const CREATED: CreatedRun = {
  run_id: '018f0000-0000-7000-8000-000000000021',
  state: 'queued',
  unverified_ref: false,
};

describe('CreateRunStore Retry-After', () => {
  const createRun = vi.fn<RunDataService['createRun']>();
  const newIdempotencyKey = vi.fn(() => crypto.randomUUID());
  beforeEach(() => {
    vi.useFakeTimers();
    createRun.mockReset();
    newIdempotencyKey.mockClear();
    TestBed.configureTestingModule({
      providers: [
        CreateRunStore,
        { provide: RunDataService, useValue: { createRun, newIdempotencyKey } },
      ],
    });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
  });

  function refuse(kind: 'rate_limited' | 'unavailable', seconds: number | null): void {
    createRun
      .mockRejectedValueOnce(
        new ApiRequestError({
          kind,
          status: kind === 'rate_limited' ? 429 : 503,
          code: kind,
          message: 'Try again after the server wait.',
          requestId: 'req-wait',
          retryable: true,
          retryAfterSeconds: seconds,
          details: [],
        }),
      )
      .mockResolvedValue(CREATED);
  }

  it.each(['rate_limited', 'unavailable'] as const)(
    'blocks every POST before a %s deadline and then reuses the same key',
    async (kind) => {
      refuse(kind, 2);
      const store = TestBed.inject(CreateRunStore);
      const signal = new AbortController().signal;
      await store.create('project-1', INPUT, signal);
      expect(store.retryReady()).toBe(false);
      store.clearError();
      expect(store.error()?.kind).toBe(kind);
      await store.create('project-1', INPUT, signal);
      await store.create('project-1', { ...INPUT, release_id: INPUT.manifest_id }, signal);
      vi.advanceTimersByTime(1999);
      await store.create('project-1', INPUT, signal);
      expect(createRun).toHaveBeenCalledTimes(1);
      expect(newIdempotencyKey).toHaveBeenCalledTimes(1);
      vi.advanceTimersByTime(1);
      expect(store.retryReady()).toBe(true);
      expect(await store.create('project-1', INPUT, signal)).toEqual(CREATED);
      expect(createRun).toHaveBeenCalledTimes(2);
      expect(createRun.mock.calls[1]?.[2]).toBe(createRun.mock.calls[0]?.[2]);
    },
  );

  it.each([null, 0])('allows an explicit retry immediately for a %s interval', async (seconds) => {
    refuse('rate_limited', seconds);
    const store = TestBed.inject(CreateRunStore);
    const signal = new AbortController().signal;
    await store.create('project-1', INPUT, signal);
    expect(store.retryReady()).toBe(true);
    expect(await store.create('project-1', INPUT, signal)).toEqual(CREATED);
    expect(createRun.mock.calls[1]?.[2]).toBe(createRun.mock.calls[0]?.[2]);
  });

  it('clears the pending wait when the dialog-owned store is destroyed', async () => {
    refuse('unavailable', 60);
    const store = TestBed.inject(CreateRunStore);
    await store.create('project-1', INPUT, new AbortController().signal);
    expect(vi.getTimerCount()).toBe(1);
    TestBed.resetTestingModule();
    expect(vi.getTimerCount()).toBe(0);
  });
});
