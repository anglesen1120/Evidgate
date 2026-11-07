import { MAX_TIMER_DELAY_MS, clampTimerDelay } from './timer-delay';

describe('clampTimerDelay', () => {
  it('pins the ceiling at the largest delay a timer accepts', () => {
    expect(MAX_TIMER_DELAY_MS).toBe(2 ** 31 - 1);
  });

  it('passes a wait the platform accepts through unchanged', () => {
    expect(clampTimerDelay(0)).toBe(0);
    expect(clampTimerDelay(1_000)).toBe(1_000);
    expect(clampTimerDelay(MAX_TIMER_DELAY_MS)).toBe(MAX_TIMER_DELAY_MS);
  });

  it('clamps one millisecond past the ceiling to the ceiling itself', () => {
    expect(clampTimerDelay(MAX_TIMER_DELAY_MS + 1)).toBe(MAX_TIMER_DELAY_MS);
    expect(clampTimerDelay(Number.MAX_SAFE_INTEGER)).toBe(MAX_TIMER_DELAY_MS);
  });

  it('keeps a negative wait negative so a boundary already passed fires at once', () => {
    expect(clampTimerDelay(-1)).toBe(-1);
  });
});
