import { InjectionToken, type Signal, computed, signal } from '@angular/core';
import type { PolicyActivationStateDto } from '@evidgate/sdk';

import type { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';

/** What the page knows about the project's activation at one moment. */
export type ActivationRead =
  | { readonly kind: 'loading' }
  | { readonly kind: 'active'; readonly state: PolicyActivationStateDto }
  | { readonly kind: 'none' }
  | { readonly kind: 'forbidden' }
  | { readonly kind: 'error'; readonly error: ApiError };

/** Largest delay a browser or Node timer accepts; a longer interval is clamped to it. */
const MAX_WAIT_DELAY_MS = 2 ** 31 - 1;

/**
 * The clock a failed read's `Retry-After` wait is measured against. `createIntentRunner` takes its
 * clock the same way — an option, never a bare `Date.now` — but a read has no intent and no runner,
 * so the page owns the same rule and the same seam: a spec overrides this provider and advances the
 * wait without waiting for it.
 */
export const READ_CLOCK = new InjectionToken<() => number>('evidgate.onboarding.read-clock', {
  providedIn: 'root',
  factory: () => (): number => Date.now(),
});

/**
 * A failed read's `Retry-After` window, as the page renders it: one deadline from the answer's own
 * interval, one second-tick for the visible countdown (never a poll for eligibility — the tick only
 * re-renders the remainder, and never a live region, because the state sheet asks for the interval
 * as text rather than a per-second announcement).
 */
export interface ReadWaitWindow {
  /** Whole seconds left in the open window; `null` when no window is open. */
  readonly seconds: Signal<number | null>;
  /** True while the window is open: both read controls stay disabled. */
  readonly open: Signal<boolean>;
  /** The sentence the panel shows, or `null` while no window is open. */
  readonly text: Signal<string | null>;
  /** Opens the window from an answer's interval; an absent, negative or non-finite one opens none. */
  start(seconds: number | null): void;
  /** Closes the window (the countdown's own deadline, a new attempt, or the page going away). */
  clear(): void;
}

/**
 * Creates the read's wait window. The rule is the runner's own rule for the confirm
 * (`core/http/retry.ts`): an absent interval is no interval, so the control opens at once, and a
 * positive one is a single timer, never a poll.
 */
export function createReadWaitWindow(now: () => number): ReadWaitWindow {
  const deadline = signal<number | null>(null);
  const supplied = signal<number | null>(null);
  const instant = signal(0);
  let timer: ReturnType<typeof setInterval> | null = null;

  const clear = (): void => {
    if (timer !== null) {
      clearInterval(timer);
      timer = null;
    }
    deadline.set(null);
    supplied.set(null);
  };

  const seconds = computed<number | null>(() => {
    const end = deadline();
    if (end === null) return null;
    const left = end - instant();
    return left > 0 ? Math.max(1, Math.ceil(left / 1000)) : null;
  });

  return {
    seconds,
    open: computed(() => seconds() !== null),
    // Neutral about *why* the server closed the read — the page shows it for any answer that names
    // an interval, not only a 429 — the time left, and the interval the server named itself, so a
    // reader can tell a countdown from a guess about a missing interval.
    text: computed<string | null>(() => {
      const remaining = seconds();
      if (remaining === null) return null;
      const named = supplied() ?? remaining;
      return $localize`:@@onboarding.enable.read-wait:The server asked you to wait before reading the activation again: ${remaining}:remaining: s left of the ${named}:supplied: s it named.`;
    }),
    start: (value: number | null): void => {
      clear();
      const interval = value !== null && Number.isFinite(value) && value >= 0 ? value : null;
      if (interval === null || interval === 0) return;
      const startedAt = now();
      supplied.set(interval);
      instant.set(startedAt);
      // Clamped like a runner's delay: a value above the timer maximum would fire at once and turn
      // a hostile interval into no wait at all.
      deadline.set(startedAt + Math.min(interval * 1000, MAX_WAIT_DELAY_MS));
      timer = setInterval(() => {
        const at = now();
        instant.set(at);
        const end = deadline();
        if (end !== null && end <= at) clear();
      }, 1000);
    },
    clear,
  };
}

/**
 * Maps one activation read to what the page renders. A `403` is a refusal rather than a throttled
 * attempt, so it becomes the read-only state whose recovery is the re-read control, and the
 * answer's interval must not close that control for a wait nobody asked the visitor to sit through.
 *
 * Every throw — a cancellation that reached a read the page still owns included — becomes a
 * rendered state with a way back, never an absent activation and never a stuck loading panel.
 */
export async function readActivationState(
  data: OnboardingDataService,
  projectId: string,
  fallbackMessage: string,
): Promise<ActivationRead> {
  try {
    const state = await data.readPolicyActivation(projectId);
    return state === null ? { kind: 'none' } : { kind: 'active', state };
  } catch (error: unknown) {
    // A throw that is not the gateway's typed failure is a bug in the call path, not an absence of
    // activation: it still becomes a rendered state with a way back, never a stuck loading panel.
    const failure: ApiError = isApiRequestError(error)
      ? error.error
      : {
          kind: 'unexpected',
          status: 0,
          code: 'client_error',
          message: fallbackMessage,
          requestId: null,
          retryable: true,
          details: [],
          retryAfterSeconds: null,
        };
    return failure.status === 403 ? { kind: 'forbidden' } : { kind: 'error', error: failure };
  }
}
