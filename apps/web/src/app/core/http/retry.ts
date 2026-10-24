// 09.1.1-T8.2 (00 §9.2; ADR-0008 rule 2; state sheet "429 Retry-After indicator"): the headless
// half of the 412/429 patterns. One user action owns one `Intent` — one `Idempotency-Key` — and
// an explicit retry of that action reuses it, so the server replays the request instead of
// creating a second resource (never a duplicate mutation). Nothing here retries on its own:
// the caller renders the error pattern and asks `retryEligible` whether an explicit retry may
// run now.
//
// Eligibility is only `ApiError.retryable` from the mapper plus the server's `Retry-After`, never a
// kind the runner hard-codes: a 422, a 412 or a 403 stays ineligible, and a 429/503 becomes
// eligible after its interval. [DECISION 2] an absent or zero interval is eligible immediately —
// the runner never guesses a countdown — and a positive interval is one `setTimeout` per failure,
// never a ticking poll and never an automatic retry.
//
// `reset()` abandons an intent explicitly (after a 412 dialog reload or an explicit cancel), and
// `run()` does the same for a non-retryable failure, since the caller resubmitted a corrected
// action; either way the next attempt mints a new key. `reset()` does not cancel an in-flight
// request — the runner has no `AbortSignal`; it only stops that attempt from writing any signal
// when it settles.
//
// No DI and no RxJS: signals work outside an injection context, and the clock is injectable only so
// the failure instant of a `Retry-After` wait is deterministic in tests.

import { computed, signal } from '@angular/core';
import type { Signal } from '@angular/core';

import { isApiRequestError } from './api-error';
import type { ApiError } from './api-error';
import { createIntent } from './intent';
import type { Intent } from './intent';

/** Lifecycle of one user action. */
export type IntentRunStatus = 'idle' | 'running' | 'succeeded' | 'failed';

/** Optional seams of {@link createIntentRunner}; `now` defaults to `Date.now`. */
export interface IntentRunnerOptions {
  /**
   * Clock used to timestamp a failure's `Retry-After` deadline and to tell whether that deadline
   * has passed. The wait itself is one timer, not a poll. Defaults to `Date.now`.
   */
  readonly now?: () => number;
}

/**
 * The state and the explicit actions of one user action (a mutation, or any request whose result
 * the page renders). Read-only signals for rendering; `run()`/`retry()`/`reset()` for intent.
 * From a retryable failure `run()` retries the same intent; from a non-retryable one (422,
 * 412, 403, …) it starts a new action with a new key, because the caller fixed the payload.
 */
export interface IntentRunner<T> {
  readonly status: Signal<IntentRunStatus>;
  /** Result of the last successful attempt of the current intent; `undefined` otherwise. */
  readonly result: Signal<T | undefined>;
  /**
   * Typed failure of the action; `null` after a success, an `AbortError`, `reset()`, or as soon
   * as a new attempt starts.
   */
  readonly error: Signal<ApiError | null>;
  /** The `Intent` whose `Idempotency-Key` the next attempt uses; `null` until the first `run()`. */
  readonly intent: Signal<Intent | null>;
  /** `true` when `retry()` would start an attempt now. */
  readonly retryEligible: Signal<boolean>;
  /**
   * Server `Retry-After` of the last failure, in whole seconds; `null` when absent, negative,
   * non-finite, or when a new attempt starts.
   */
  readonly retryAfterSeconds: Signal<number | null>;
  /**
   * Start the user action. From `idle`/`succeeded`, from a non-retryable failure (422, 412,
   * 403, …) or from a programming error, this mints a NEW intent: the caller resubmitted a
   * corrected action. An intent left by an `AbortError` is reused (see `createIntentRunner`).
   * From a retryable failure it behaves like `retry()`: same key, honouring the `Retry-After`
   * wait, so a 429 still waiting stays a no-op.
   */
  run(): Promise<void>;
  /** Explicit user retry of the SAME intent; a no-op unless {@link retryEligible}. */
  retry(): Promise<void>;
  /** Abandon the intent: the next `run()` mints a new key. Does not cancel an in-flight request. */
  reset(): void;
}

/**
 * True for a cancellation, duck-typed the way `api-gateway.ts` does it: the adapter rejects with a
 * `DOMException` named `AbortError`, and per-realm classes make `instanceof` unreliable. The abort
 * reason is never read.
 */
function isAbortError(value: unknown): boolean {
  return (
    typeof value === 'object' && value !== null && 'name' in value && value.name === 'AbortError'
  );
}

/** Largest delay a browser or Node timer accepts; a longer server interval is clamped to it. */
const MAX_TIMER_DELAY_MS = 2 ** 31 - 1;

/**
 * A server `Retry-After` usable as a wait: a finite, non-negative number of seconds. A negative
 * or non-finite value is not an interval, so it is treated like an absent one ([DECISION 2]).
 */
function usableRetryAfter(seconds: number | null): number | null {
  return seconds !== null && Number.isFinite(seconds) && seconds >= 0 ? seconds : null;
}

/**
 * Create the runner for one operation. `operation` receives the intent so it can send
 * `intentHeaders(intent)`; it must reject with `ApiRequestError` for a server/transport failure
 * (the runner reads its `error`), and anything else is treated as a programming error.
 *
 * After an `AbortError`, and after a retryable failure (network, timeout, 429, 503), the intent is
 * kept, because the request may already have reached the server. A caller that changes the payload before resubmitting MUST call `reset()` first:
 * reusing the key for a different body can be answered with a 409 `idempotency_conflict`.
 *
 * The runner has no `dispose()`. An owner that unmounts while a `Retry-After` timer is pending
 * calls `reset()` (e.g. from `ngOnDestroy`) so the timer is cleared.
 */
export function createIntentRunner<T>(
  operation: (intent: Intent) => Promise<T>,
  options: IntentRunnerOptions = {},
): IntentRunner<T> {
  const now = options.now ?? ((): number => Date.now());

  const statusState = signal<IntentRunStatus>('idle');
  const resultState = signal<T | undefined>(undefined);
  const errorState = signal<ApiError | null>(null);
  const intentState = signal<Intent | null>(null);
  const retryAfterState = signal<number | null>(null);
  /** Epoch ms at which a positive `Retry-After` wait ends; `null` when a retry may run now. */
  const waitDeadlineState = signal<number | null>(null);

  /** Attempt id: a settlement only writes signals while its attempt is still current. */
  let attemptEpoch = 0;
  let retryTimer: ReturnType<typeof setTimeout> | null = null;

  /** Cancel the pending wait, if any, and open the retry window. */
  function clearRetryWait(): void {
    if (retryTimer !== null) {
      clearTimeout(retryTimer);
      retryTimer = null;
    }
    waitDeadlineState.set(null);
  }

  /**
   * One timer for this failure: the retry opens when it fires, nothing polls, and no attempt is
   * started from here. An absent or non-positive interval is open immediately [DECISION 2].
   */
  function startRetryWait(seconds: number | null): void {
    if (seconds === null || seconds <= 0) {
      return;
    }
    // A delay above the timer maximum fires immediately in browsers and warns in Node, which
    // would turn a hostile or malformed interval into no wait at all; clamp it instead.
    const delayMs = Math.min(seconds * 1000, MAX_TIMER_DELAY_MS);
    // The interval runs from the failure instant; the timer only wakes the signal at its end.
    waitDeadlineState.set(now() + delayMs);
    retryTimer = setTimeout(() => {
      retryTimer = null;
      waitDeadlineState.set(null);
    }, delayMs);
  }

  /** False unless the last attempt failed retryably and its `Retry-After` wait is over. */
  const retryEligible = computed(() => {
    if (statusState() !== 'failed') {
      return false;
    }
    const failure = errorState();
    if (failure === null || !failure.retryable) {
      return false;
    }
    const deadline = waitDeadlineState();
    return deadline === null || deadline <= now();
  });

  /**
   * Run `operation` for one intent. The status is `running` before the first await, so a second
   * call cannot start a concurrent attempt or mint a second key.
   */
  async function attempt(active: Intent): Promise<void> {
    clearRetryWait();
    const epoch = ++attemptEpoch;
    // A failure and a result describe the previous attempt; neither is current while this one
    // runs, so nothing stale is rendered as the outcome of this action.
    errorState.set(null);
    resultState.set(undefined);
    retryAfterState.set(null);
    statusState.set('running');
    try {
      const value = await operation(active);
      if (epoch !== attemptEpoch) {
        return;
      }
      resultState.set(value);
      statusState.set('succeeded');
      // One key per user action: the completed action's key is never reused.
      intentState.set(null);
    } catch (caught) {
      if (isAbortError(caught)) {
        if (epoch === attemptEpoch) {
          // A cancellation is not a failure and the request may have reached the server, so
          // the intent (and its key) is kept for the next run(). A caller whose next attempt
          // changes the payload must reset() first, or the reused key risks a 409
          // idempotency_conflict.
          errorState.set(null);
          statusState.set('idle');
        }
        return;
      }
      if (isApiRequestError(caught)) {
        if (epoch !== attemptEpoch) {
          return;
        }
        const retryAfterSeconds = usableRetryAfter(caught.error.retryAfterSeconds);
        errorState.set(caught.error);
        retryAfterState.set(retryAfterSeconds);
        statusState.set('failed');
        // The intent is kept: a failed action is only abandoned by reset(), or by run() after a
        // non-retryable failure.
        startRetryWait(retryAfterSeconds);
        return;
      }
      if (epoch === attemptEpoch) {
        // A programming error is not an `ApiError`; do not dress it as one, and do not swallow it.
        errorState.set(null);
        statusState.set('failed');
      }
      throw caught;
    }
  }

  async function run(): Promise<void> {
    if (statusState() === 'running') {
      return;
    }
    if (statusState() === 'failed') {
      const failure = errorState();
      if (failure !== null && failure.retryable) {
        // Same intent, same key; retry() also enforces the `Retry-After` wait.
        await retry();
        return;
      }
      // 422/412/403 or a programming error: the caller corrected the action and resubmits it,
      // so the failed intent is abandoned here and a new key is minted below.
      reset();
    }
    // After success/reset the signal is null (fresh key); after an abort it still holds the
    // abandoned attempt's intent, which is reused because that request may have been delivered.
    const active = intentState() ?? createIntent();
    intentState.set(active);
    await attempt(active);
  }

  async function retry(): Promise<void> {
    if (!retryEligible()) {
      return;
    }
    const active = intentState();
    if (active === null) {
      return;
    }
    await attempt(active);
  }

  function reset(): void {
    // Invalidate any in-flight attempt: it is not cancelled, but its settlement no longer
    // writes the state of the intent that was just abandoned.
    attemptEpoch += 1;
    clearRetryWait();
    statusState.set('idle');
    resultState.set(undefined);
    errorState.set(null);
    intentState.set(null);
    retryAfterState.set(null);
  }

  return {
    status: statusState.asReadonly(),
    result: resultState.asReadonly(),
    error: errorState.asReadonly(),
    intent: intentState.asReadonly(),
    retryEligible,
    retryAfterSeconds: retryAfterState.asReadonly(),
    run,
    retry,
    reset,
  };
}
