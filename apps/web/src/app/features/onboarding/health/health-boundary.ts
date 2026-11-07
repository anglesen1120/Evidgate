import { signal, type Signal } from '@angular/core';
import type { IngestTokenView } from '@evidgate/sdk';

import { clampTimerDelay } from '../../../shared/time/timer-delay';

/**
 * How long to wait for a boundary `next`, measured from `now`.
 *
 * A delay past the timer ceiling is clamped so the wait still happens, just in several capped
 * steps; a boundary already behind `now` yields a negative delay, which a timer fires at once.
 */
export function boundaryDelayMs(next: number, now: number): number {
  return clampTimerDelay(next - now);
}

/**
 * The clock a credential panel reads, re-armed for the next boundary a status change lands on.
 *
 * A token's status is true only until it expires or its overlap ends, so the panel re-reads the
 * clock at exactly that moment instead of trusting the read that listed the credential. Timers are
 * session-scoped work: signing out has to stop them, because the step can stay mounted under a
 * router outlet while the session ends, and a timer that fired afterwards would repaint a
 * credential panel for a session that no longer exists.
 */
export class HealthBoundaryClock {
  private readonly clock = signal(new Date());
  private timer: ReturnType<typeof setTimeout> | undefined;
  /** The current time, moved forward to each boundary the tokens carry. */
  readonly now: Signal<Date> = this.clock.asReadonly();

  constructor(private readonly tokens: () => readonly IngestTokenView[]) {}

  /** Reads the clock now and arms the next boundary; safe to call on every token change. */
  arm(): void {
    this.stop();
    const now = Date.now();
    this.clock.set(new Date(now));
    const next = nextBoundary(this.tokens(), now);
    if (next === Infinity) return;
    this.timer = setTimeout(() => this.arm(), boundaryDelayMs(next, now));
  }

  /** Stops the timer; the clock keeps the time it last read. */
  stop(): void {
    if (this.timer === undefined) return;
    clearTimeout(this.timer);
    this.timer = undefined;
  }
}

/**
 * The earliest boundary ahead of `now`, or `Infinity` when no credential has one left.
 *
 * An active token borrows until it expires; a rotated one until its overlap ends, which is the last
 * moment it still verifies. A disabled, revoked or expired token has no boundary to wait for.
 */
export function nextBoundary(tokens: readonly IngestTokenView[], now: number): number {
  let next = Infinity;
  for (const token of tokens) {
    const boundary =
      token.status === 'active'
        ? token.expires_at
        : token.status === 'rotated'
          ? token.overlap_ends_at
          : null;
    if (boundary == null) continue;
    const timestamp = Date.parse(boundary);
    if (timestamp > now && timestamp < next) next = timestamp;
  }
  return next;
}
