// Largest delay a browser or Node timer accepts. A longer `Retry-After`-style interval fires at
// once in browsers and warns in Node, which would turn a hostile or malformed wait into no wait at
// all, so every wait clamps to it instead.

/** The largest delay a timer accepts: 2^31 - 1 ms. */
export const MAX_TIMER_DELAY_MS = 2 ** 31 - 1;

/** A wait in milliseconds, clamped to the largest delay a timer accepts; a negative one stays. */
export function clampTimerDelay(delayMs: number): number {
  return Math.min(delayMs, MAX_TIMER_DELAY_MS);
}
