// The retry policy of the send path: which statuses are worth another attempt, how long
// `Retry-After` asks for, the jittered backoff, and the per-request signal bounded by what is
// left of the one send deadline.
import { isErrorEnvelope } from '@evidgate/sdk';

import { perRequestTimeoutMs, retryBaseMs, retryCapMs } from './senderLimits.js';

/**
 * The per-request timeout, additionally bounded by what is left of the send deadline, so
 * a hung connection can never outlive the deadline. Node unrefs both timers.
 */
export function attemptSignal(deadlineAt: number, now: number): AbortSignal {
  const timeout = AbortSignal.timeout(perRequestTimeoutMs);
  const remaining = deadlineAt - now;
  if (!Number.isFinite(remaining) || remaining <= 0) {
    return timeout;
  }
  return AbortSignal.any([timeout, AbortSignal.timeout(Math.ceil(remaining))]);
}

/**
 * Retryable: 408, 429 and 5xx, unless the response envelope says `retryable: false` (a
 * permanent 5xx must not be retried). Every other status — 401, 403, 404, 409, 413, 422 —
 * is permanent.
 */
export function isRetryable(status: number, error: unknown): boolean {
  const retryable = status === 408 || status === 429 || status >= 500;
  if (!retryable) {
    return false;
  }
  return !(isErrorEnvelope(error) && error.error.retryable === false);
}

/** Delta-seconds or the HTTP-date form `Retry-After` is allowed to take. */
const deltaSecondsPattern = /^[0-9]+$/;
const httpDatePattern = /^[A-Za-z]{3}, \d{2} [A-Za-z]{3} \d{4} \d{2}:\d{2}:\d{2} GMT$/;

export interface RetryAfterOptions {
  /**
   * Accept only those two forms. `Date.parse` also reads text like `-5` or `1.5` as a past
   * date, and a caller for which 0 would mean "retry at once" must not honour one.
   */
  readonly strict?: boolean;
}

/**
 * `Retry-After` as delta-seconds or an HTTP-date. An absent or unparsable value is
 * `undefined` (the caller falls back to jittered backoff); a date in the past is 0.
 * The caller treats this value as a floor under the jittered backoff, so a 0 never means
 * "retry at once".
 */
export function retryAfter(
  response: Response,
  now: number,
  options: RetryAfterOptions = {},
): number | undefined {
  const raw = response.headers.get('retry-after');
  if (raw === null) {
    return undefined;
  }
  const value = raw.trim();
  if (value === '') {
    return undefined;
  }
  if (deltaSecondsPattern.test(value)) {
    return Number(value) * 1000;
  }
  if (options.strict === true && !httpDatePattern.test(value)) {
    return undefined;
  }
  const date = Date.parse(value);
  if (Number.isNaN(date)) {
    return undefined;
  }
  return Math.max(date - now, 0);
}

/** Full-jitter backoff: `random() * min(500 ms x 2^(attempt-1), 30 s)`. */
export function backoffDelay(attempts: number, sample: number): number {
  const ceiling = Math.min(retryBaseMs * 2 ** (attempts - 1), retryCapMs);
  const bounded = Math.min(Math.max(sample, 0), 1);
  return Math.min(Math.floor(bounded * ceiling), retryCapMs);
}
