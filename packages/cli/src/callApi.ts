// The one bounded-retry path the idempotent commands share: replaying is safe only when the
// request carries an `Idempotency-Key`, and the unkeyed token exchange may retry only the
// answers the server gives before it mints a single-use token.
import { setTimeout as delay } from 'node:timers/promises';

import { attemptSignal, backoffDelay, isRetryable, retryAfter } from './sender.js';

/** Requests per call, the first included. Small on purpose: this is a CLI, not a queue. */
export const maxCallAttempts = 3;
/** One budget for the whole call (attempts and waits); the per-request timeout is 30 s. */
export const defaultCallBudgetMs = 60_000;
/** A caller may raise the budget, but never past this ceiling. */
export const maxCallBudgetMs = 300_000;
/** A server-named wait is clamped to this cap, so a longer one waits this much. */
export const maxRetryAfterMs = 60_000;

/**
 * What the call may retry.
 * - `replay-safe`: the request carries an `Idempotency-Key` (or is a GET), so a transport
 *   failure, 429 and 5xx are all retried, unless the envelope says `retryable: false`.
 *   Every other 4xx — 408 included — is final.
 * - `pre-processing`: the unkeyed token exchange. Only 429 and 503 are retried — both are
 *   answers the server produces before it mints a token (openapi `/ci/token`).
 * - `none`: exactly one request.
 */
export type RetryPolicy = 'replay-safe' | 'pre-processing' | 'none';

/** The seams a spec injects so retries are deterministic: no real clock, no real sleep. */
export interface CallApiDeps {
  /** Virtual sleep; a spec advances its own clock here instead of waiting. */
  readonly sleep?: (milliseconds: number) => Promise<void>;
  /** Uniform sample in `[0, 1)` for full jitter. */
  readonly random?: () => number;
  /** Clock in milliseconds; it drives the one budget and an HTTP-date `Retry-After`. */
  readonly now?: () => number;
}

export interface CallApiOptions {
  readonly policy: RetryPolicy;
  readonly deps?: CallApiDeps;
  /** Total requests, the first included. Clamped to `maxCallAttempts`. */
  readonly maxAttempts?: number;
  /** Wall-clock budget for the whole call. Clamped to `[1, maxCallBudgetMs]`. */
  readonly budgetMs?: number;
  /**
   * The wait when the server names no usable `Retry-After`; 0 means "the jittered backoff
   * alone". The token exchange uses the 1 s floor contracts/cli.md §5 documents.
   */
  readonly fallbackRetryAfterMs?: number;
}

/**
 * The part of an SDK call result this module reads. The generated client returns the data
 * and error halves of one union plus an optional `response` (absent for a transport
 * failure or a request that could not be built), so the caller keeps its own narrowing.
 */
export interface ApiResult {
  readonly error: unknown;
  readonly response?: Response | undefined;
}

/**
 * Runs `operation` until it answers 2xx, fails permanently, or the attempts/budget run
 * out, then returns that last result unchanged: the caller's own error mapping decides the
 * message and the exit code, so an exhausted budget still exits non-zero with the server's
 * `error.code` (or `transport` for a network failure).
 */
export async function callApi<TResult extends ApiResult>(
  operation: (signal: AbortSignal) => Promise<TResult>,
  options: CallApiOptions,
): Promise<TResult> {
  const now = options.deps?.now ?? Date.now;
  const random = options.deps?.random ?? Math.random;
  const sleep = options.deps?.sleep ?? ((milliseconds: number) => delay(milliseconds));
  const fallbackRetryAfterMs = Math.max(options.fallbackRetryAfterMs ?? 0, 0);
  const attempts = clampAttempts(options.maxAttempts);
  const deadlineAt = now() + clampBudget(options.budgetMs);

  let last: TResult | undefined;
  for (let attempt = 1; ; attempt += 1) {
    // A wait that ended at (or past) the deadline leaves no budget for another request:
    // `attemptSignal` would otherwise hand the expired retry its own fresh 30 s timeout.
    if (last !== undefined && now() >= deadlineAt) {
      return last;
    }
    // The signal bounds one attempt by both its own timeout and what is left of the budget.
    const result = await operation(attemptSignal(deadlineAt, now()));
    last = result;
    const response = result.response;
    if (response !== undefined && response.ok) {
      return result;
    }
    // Exhausted attempts and unreachable budgets both end here, with the real failure.
    if (attempt >= attempts) {
      return result;
    }
    const wait = retryWait(
      result,
      response,
      attempt,
      now(),
      random(),
      options.policy,
      fallbackRetryAfterMs,
    );
    if (wait === undefined) {
      return result;
    }
    // Never sleep past the budget: the caller sees the real failure instead of a timeout.
    if (now() + wait > deadlineAt) {
      return result;
    }
    await sleep(wait);
  }
}

/** The wait before the next attempt, or `undefined` when this answer is not retryable. */
function retryWait(
  result: ApiResult,
  response: Response | undefined,
  attempt: number,
  now: number,
  sample: number,
  policy: RetryPolicy,
  fallbackRetryAfterMs: number,
): number | undefined {
  if (policy === 'none') {
    return undefined;
  }
  if (response === undefined) {
    // No response at all: a network failure or a timeout. Only a replay-safe request may
    // assume the server did not act on it.
    return policy === 'replay-safe' ? backoffDelay(attempt, sample) : undefined;
  }
  // The accepted set, spelled here rather than inherited whole from the event sender:
  // 408 is a 4xx and stays final, and a non-standard status above 599 is not a 5xx.
  const transient = response.status === 429 || (response.status >= 500 && response.status <= 599);
  const retryable =
    policy === 'pre-processing'
      ? response.status === 429 || response.status === 503
      : transient && isRetryable(response.status, result.error);
  if (!retryable) {
    return undefined;
  }
  const named = retryAfter(response, now, { strict: true });
  const floor = named === undefined ? fallbackRetryAfterMs : Math.min(named, maxRetryAfterMs);
  return Math.max(backoffDelay(attempt, sample), floor);
}

function clampAttempts(requested: number | undefined): number {
  if (requested === undefined || !Number.isFinite(requested)) {
    return maxCallAttempts;
  }
  return Math.min(Math.max(Math.floor(requested), 1), maxCallAttempts);
}

function clampBudget(requested: number | undefined): number {
  if (requested === undefined || !Number.isFinite(requested)) {
    return defaultCallBudgetMs;
  }
  return Math.min(Math.max(Math.floor(requested), 1), maxCallBudgetMs);
}
