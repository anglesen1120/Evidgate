// The bounded retry every idempotent command shares: only replay-safe requests retry a
// transport failure, only 408/429/5xx (and, for the unkeyed exchange, 429/503) are
// retried at all, a `Retry-After` — delta-seconds or an HTTP-date — is a floor under the
// jittered backoff and is capped, and the attempts and the whole-call budget both end the
// loop with the last real result. The seams are injected, so every wait is asserted
// instead of slept.
import { describe, expect, it } from 'vitest';

import {
  callApi,
  maxCallAttempts,
  maxRetryAfterMs,
  type CallApiDeps,
  type CallApiOptions,
} from './callApi.js';

/** The injected seams plus what the spec needs to assert what was waited. */
interface FakeClock {
  readonly deps: Required<CallApiDeps>;
  readonly sleeps: number[];
  readonly at: () => number;
}

/** A clock whose `sleep` only advances it: no test waits in real time. */
function fakeClock(startAt = 1_700_000_000_000): FakeClock {
  let time = startAt;
  const sleeps: number[] = [];
  const deps = {
    sleep: (milliseconds: number): Promise<void> => {
      sleeps.push(milliseconds);
      time += milliseconds;
      return Promise.resolve();
    },
    // A zero sample turns full jitter off, so the server's `Retry-After` is the whole wait.
    random: (): number => 0,
    now: (): number => time,
  };
  return { deps, sleeps, at: (): number => time };
}

/** A `503` (or any status) with the error envelope the sender also classifies. */
function envelopeResponse(status: number, retryable = true, headers = {}): Response {
  return new Response(
    JSON.stringify({
      error: { code: 'unavailable', message: 'not now', request_id: 'req-1', retryable },
    }),
    { status, headers: { 'content-type': 'application/json', ...headers } },
  );
}

function okResponse(): Response {
  return new Response(JSON.stringify({ ok: true }), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  });
}

/**
 * The operation under test: answers each call from `responses` in order and repeats the
 * last one, recording how many requests were made.
 */
function respondWith(...responses: ReadonlyArray<Response | undefined>) {
  const calls: number[] = [];
  return {
    calls,
    operation: async (): Promise<{ error: unknown; response?: Response }> => {
      calls.push(calls.length + 1);
      const response = responses[Math.min(calls.length - 1, responses.length - 1)];
      if (response === undefined) {
        return { error: new Error('connection refused') };
      }
      // The SDK parses a non-2xx body into `error`, which is how an error envelope
      // reaches `isRetryable`; the stub does the same.
      const error = response.ok
        ? undefined
        : await response
            .clone()
            .json()
            .catch(() => undefined);
      return { error, response };
    },
  };
}

function optionsFor(deps: CallApiDeps, extra: Partial<CallApiOptions> = {}): CallApiOptions {
  return { policy: 'replay-safe' as const, deps, ...extra };
}

describe('callApi', () => {
  it('a_5xx_then_200_returns_the_second_result', async () => {
    const clock = fakeClock();
    const stub = respondWith(envelopeResponse(503), okResponse());

    const result = await callApi(stub.operation, optionsFor(clock.deps));

    expect(result.response?.status).toBe(200);
    expect(stub.calls).toHaveLength(2);
    // The jitter sample is 0 and no `Retry-After` was named, so the wait is 0.
    expect(clock.sleeps).toEqual([0]);
  });

  it('a_permanent_4xx_is_not_retried', async () => {
    const clock = fakeClock();
    const stub = respondWith(envelopeResponse(400, false), okResponse());

    const result = await callApi(stub.operation, optionsFor(clock.deps));

    expect(result.response?.status).toBe(400);
    expect(stub.calls).toHaveLength(1);
    expect(clock.sleeps).toEqual([]);
  });

  it('a_408_is_not_retried', async () => {
    const clock = fakeClock();
    const stub = respondWith(envelopeResponse(408, false), okResponse());

    const result = await callApi(stub.operation, optionsFor(clock.deps));

    // 408 is a 4xx: only 429 and 5xx are retried, whatever the shared event classifier says.
    expect(result.response?.status).toBe(408);
    expect(stub.calls).toHaveLength(1);
    expect(clock.sleeps).toEqual([]);
  });

  it('a_wait_that_reaches_the_deadline_grants_no_further_request', async () => {
    const clock = fakeClock();
    // The wait fits exactly: `now + wait` is not beyond the deadline, so it is slept — and
    // the deadline check before the next request is what stops the retry.
    const stub = respondWith(envelopeResponse(429, true, { 'retry-after': '1' }), okResponse());

    const result = await callApi(stub.operation, optionsFor(clock.deps, { budgetMs: 1_000 }));

    expect(result.response?.status).toBe(429);
    expect(clock.sleeps).toEqual([1_000]);
    expect(stub.calls).toHaveLength(1);
  });

  it('a_429_waits_the_delta_seconds_retry_after', async () => {
    const clock = fakeClock();
    const stub = respondWith(envelopeResponse(429, true, { 'retry-after': '2' }), okResponse());

    const result = await callApi(stub.operation, optionsFor(clock.deps));

    expect(result.response?.status).toBe(200);
    expect(clock.sleeps).toEqual([2_000]);
  });

  it('a_429_waits_until_the_http_date_retry_after', async () => {
    const clock = fakeClock();
    const at = clock.at();
    const stub = respondWith(
      envelopeResponse(429, true, { 'retry-after': new Date(at + 3_000).toUTCString() }),
      okResponse(),
    );

    await callApi(stub.operation, optionsFor(clock.deps));

    // An HTTP-date carries whole seconds, so the wait is the date minus the injected clock.
    expect(clock.sleeps).toHaveLength(1);
    expect(clock.sleeps[0]).toBeGreaterThanOrEqual(2_000);
    expect(clock.sleeps[0]).toBeLessThanOrEqual(3_000);
  });

  it('a_retry_after_beyond_the_cap_is_clamped', async () => {
    const clock = fakeClock();
    const stub = respondWith(envelopeResponse(429, true, { 'retry-after': '9999' }), okResponse());

    await callApi(stub.operation, optionsFor(clock.deps, { policy: 'pre-processing' }));

    expect(clock.sleeps).toEqual([maxRetryAfterMs]);
  });

  it('a_malformed_retry_after_falls_back_to_the_configured_floor', async () => {
    for (const header of ['-5', '1.5', 'garbage']) {
      const clock = fakeClock();
      const stub = respondWith(
        envelopeResponse(429, true, { 'retry-after': header }),
        okResponse(),
      );

      await callApi(stub.operation, optionsFor(clock.deps, { fallbackRetryAfterMs: 1_000 }));

      expect(stub.calls, header).toHaveLength(2);
      // `Date.parse` reads these as a past date (0 ms); a malformed header must not turn
      // the retry into an immediate one, so the floor is the whole wait.
      expect(clock.sleeps, header).toEqual([1_000]);
    }
  });

  it('a_wait_that_outlives_the_budget_ends_the_retries', async () => {
    const clock = fakeClock();
    const stub = respondWith(envelopeResponse(503, true, { 'retry-after': '30' }), okResponse());

    const result = await callApi(stub.operation, optionsFor(clock.deps, { budgetMs: 1_000 }));

    expect(result.response?.status).toBe(503);
    expect(stub.calls).toHaveLength(1);
    expect(clock.sleeps).toEqual([]);
  });

  it('exhausted_attempts_return_the_last_error', async () => {
    const clock = fakeClock();
    const stub = respondWith(envelopeResponse(503));

    const result = await callApi(stub.operation, optionsFor(clock.deps));

    expect(result.response?.status).toBe(503);
    expect(stub.calls).toHaveLength(maxCallAttempts);
    expect(clock.sleeps).toHaveLength(maxCallAttempts - 1);
  });

  it('a_larger_max_attempts_is_clamped', async () => {
    const clock = fakeClock();
    const stub = respondWith(envelopeResponse(503));

    await callApi(stub.operation, optionsFor(clock.deps, { maxAttempts: 10 }));

    expect(stub.calls).toHaveLength(maxCallAttempts);
  });

  it('a_transport_failure_is_retried_when_the_request_is_replay_safe', async () => {
    const clock = fakeClock();
    const stub = respondWith(undefined, okResponse());

    const result = await callApi(stub.operation, optionsFor(clock.deps));

    expect(result.response?.status).toBe(200);
    expect(stub.calls).toHaveLength(2);
  });

  it('a_transport_failure_is_final_for_the_unkeyed_exchange', async () => {
    const clock = fakeClock();
    const stub = respondWith(undefined, okResponse());

    const result = await callApi(
      stub.operation,
      optionsFor(clock.deps, { policy: 'pre-processing' }),
    );

    expect(result.response).toBeUndefined();
    expect(stub.calls).toHaveLength(1);
  });

  it('the_exchange_policy_retries_429_and_503_only', async () => {
    const clock = fakeClock();
    const cases: ReadonlyArray<readonly [number, number]> = [
      [429, 2],
      [503, 2],
      [500, 1],
      [502, 1],
      [408, 1],
      [401, 1],
    ];
    for (const [status, expected] of cases) {
      const stub = respondWith(envelopeResponse(status), okResponse());
      await callApi(stub.operation, optionsFor(clock.deps, { policy: 'pre-processing' }));
      expect(stub.calls, String(status)).toHaveLength(expected);
    }
  });

  it('the_none_policy_makes_exactly_one_request', async () => {
    const clock = fakeClock();
    const stub = respondWith(envelopeResponse(503), okResponse());

    const result = await callApi(stub.operation, optionsFor(clock.deps, { policy: 'none' }));

    expect(result.response?.status).toBe(503);
    expect(stub.calls).toHaveLength(1);
  });

  it('an_envelope_that_declares_itself_permanent_is_not_retried', async () => {
    const clock = fakeClock();
    const stub = respondWith(envelopeResponse(503, false), okResponse());

    const result = await callApi(stub.operation, optionsFor(clock.deps));

    expect(result.response?.status).toBe(503);
    expect(stub.calls).toHaveLength(1);
  });

  it('a_named_retry_after_is_a_floor_under_the_jittered_backoff', async () => {
    const clock = fakeClock();
    // A zero `Retry-After` cannot make the retry immediate: the backoff still applies.
    const stub = respondWith(envelopeResponse(429, true, { 'retry-after': '0' }), okResponse());
    const deps = { ...clock.deps, random: (): number => 1 };

    await callApi(stub.operation, { policy: 'replay-safe', deps });

    expect(clock.sleeps).toEqual([500]);
  });
});
