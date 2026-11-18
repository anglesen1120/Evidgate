import { afterEach, describe, expect, it, vi } from 'vitest';
import { MAX_TIMER_DELAY_MS } from '../../shared/time/timer-delay';
import { createRetryAfterWait } from './retry';

describe('createRetryAfterWait', () => {
  afterEach(() => vi.useRealTimers());

  it('blocks until the failure deadline without issuing any retry', () => {
    vi.useFakeTimers();
    const wait = createRetryAfterWait();
    expect(wait.ready()).toBe(true);
    wait.start(2);
    expect(wait.ready()).toBe(false);
    vi.advanceTimersByTime(1999);
    expect(wait.ready()).toBe(false);
    vi.advanceTimersByTime(1);
    expect(wait.ready()).toBe(true);
  });

  it.each([null, 0, -1, Number.NaN, Number.POSITIVE_INFINITY])(
    'does not invent a wait for %s seconds',
    (seconds) => {
      vi.useFakeTimers();
      const wait = createRetryAfterWait();
      wait.start(seconds);
      expect(wait.ready()).toBe(true);
      expect(vi.getTimerCount()).toBe(0);
    },
  );

  it('replaces an earlier deadline and clears the owned timer when disposed', () => {
    vi.useFakeTimers();
    const wait = createRetryAfterWait();
    wait.start(1);
    wait.start(3);
    expect(vi.getTimerCount()).toBe(1);
    vi.advanceTimersByTime(1000);
    expect(wait.ready()).toBe(false);
    wait.clear();
    expect(wait.ready()).toBe(true);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('clamps an oversized timer instead of opening the retry window immediately', () => {
    vi.useFakeTimers();
    const wait = createRetryAfterWait();
    wait.start(MAX_TIMER_DELAY_MS);
    vi.advanceTimersByTime(MAX_TIMER_DELAY_MS - 1);
    expect(wait.ready()).toBe(false);
    vi.advanceTimersByTime(1);
    expect(wait.ready()).toBe(true);
  });
});
