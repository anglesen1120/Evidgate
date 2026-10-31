// The one bounded-retry call the idempotent commands share. `runs create`,
// `runs finalize` and `manifests upload` send an `Idempotency-Key`, so replaying them is
// safe by construction: every attempt re-issues the caller's operation with the SAME key
// and the SAME body bytes, and the server replays the stored answer. `auth/exchange.ts`
// carries no key — openapi `/ci/token` is "Idempotency-Key exempt: `jti` single-use is the
// arbiter" — so it may only retry the answers that provably happen before the exchange
// runs (429 `rate_limited`, 503 `unavailable`), never a transport failure or a 500/502:
// those may follow a token that was already minted, and the single-use `jti` would then
// refuse the replay.
//
// The policy is deliberately small: at most `maxAttempts` requests and ONE wall-clock
// budget for the whole call, with the sender's own full-jitter backoff, `Retry-After`
// parser and per-request timeout (`sender.ts`, the 04.2.3 single convention) rather than a
// second spelling of them. `Retry-After` is a floor under the jitter — a delta-seconds or
// an HTTP-date value — capped at `maxRetryAfterMs`, and a wait that would outlive the
// budget ends the retries instead of sleeping. Every other 4xx is permanent. Nothing is
// logged here: the caller maps the final outcome with `serverErrorFromResponse` /
// `transportError`, which redact every registered secret, so no attempt can echo a token.
import { setTimeout as delay } from 'node:timers/promises';

import { attemptSignal, backoffDelay, isRetryable, retryAfter } from './sender.js';

/** Requests per call, the first included. Small on purpose: this is a CLI, not a queue. */
export const maxCallAttempts = 3;
/** One budget for the whole call (attempts and waits); the per-request timeout is 30 s. */
export const defaultCallBudgetMs = 60_000;
/** A caller may raise the budget, but never past this ceiling. */
export const maxCallBudgetMs = 300_000;
/** A server-named wait is honoured up to this; a longer one ends the retries. */
export const maxRetryAfterMs = 60_000;

/**
 * What the call may retry.
 * - `replay-safe`: the request carries an `Idempotency-Key` (or is a GET), so a transport
 *   failure, 429 and 5xx are all retried, unless the 00 §9.2 envelope says
 *   `retryable: false`. Every other 4xx — 408 included — is final.
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
  const named = namedRetryAfter(response, now);
  const floor = named === undefined ? fallbackRetryAfterMs : named;
  return Math.max(backoffDelay(attempt, sample), floor);
}

/** The two `Retry-After` forms openapi allows (00 §9.1): delta-seconds or an HTTP-date. */
const deltaSecondsPattern = /^[0-9]+$/;
const httpDatePattern = /^[A-Za-z]{3}, \d{2} [A-Za-z]{3} \d{4} \d{2}:\d{2}:\d{2} GMT$/;

/**
 * The server's named wait, capped at `maxRetryAfterMs`, or `undefined` when it named none.
 * Only those two forms are honoured: the shared parser defers to `Date.parse`, which also
 * reads text like `-5` or `1.5` as a past date, and a malformed header must fall back to
 * the caller's floor instead of silently becoming "retry at once".
 */
function namedRetryAfter(response: Response, now: number): number | undefined {
  const raw = response.headers.get('retry-after')?.trim();
  if (raw === undefined || (!deltaSecondsPattern.test(raw) && !httpDatePattern.test(raw))) {
    return undefined;
  }
  const parsed = retryAfter(response, now);
  return parsed === undefined ? undefined : Math.min(parsed, maxRetryAfterMs);
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
