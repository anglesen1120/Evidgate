// State sheet: docs/design/web/state-sheet.md#429-retry-after-indicator
// 09.1.1-T8.2 (00 §9.2; ADR-0008 rule 2): the idempotent-retry helper is headless, so this spec
// drives it directly — no TestBed, no HTTP. The contract it pins is the T8.2 acceptance one:
// every explicit retry of the same user action carries the same `Idempotency-Key`, a new action
// never reuses an old key, and a failure that the mapper marked non-retryable (or whose
// `Retry-After` has not elapsed) is never retried — not by `retry()`, not by `run()`, and never
// on its own.
import { ApiRequestError } from './api-error';
import type { ApiError } from './api-error';
import { createIntentRunner } from './retry';

/** A mapper-shaped failure; `network` defaults to the retryable no-response case. */
function apiError(overrides: Partial<ApiError> = {}): ApiError {
  return {
    kind: 'network',
    status: 0,
    code: 'network',
    message: 'The request did not reach the server.',
    requestId: null,
    retryable: true,
    details: [],
    retryAfterSeconds: null,
    ...overrides,
  };
}

/** A 429 as the mapper reports it; `null` is the absent `Retry-After` of [DECISION 2]. */
function rateLimited(retryAfterSeconds: number | null = 30): ApiError {
  return apiError({
    kind: 'rate_limited',
    status: 429,
    code: 'rate_limited',
    retryable: true,
    retryAfterSeconds,
  });
}

/** The typed failure the data layer rejects with. */
function failure(error: ApiError): ApiRequestError {
  return new ApiRequestError(error);
}

/** A cancellation, which the fetch adapter rethrows instead of mapping to an `ApiError`. */
function abortError(): Error {
  const error = new Error('The request was aborted.');
  error.name = 'AbortError';
  return error;
}

describe('createIntentRunner', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('starts idle with no intent, result, error or retry', () => {
    const runner = createIntentRunner(() => Promise.resolve('value'));

    expect(runner.status()).toBe('idle');
    expect(runner.result()).toBeUndefined();
    expect(runner.error()).toBeNull();
    expect(runner.intent()).toBeNull();
    expect(runner.retryEligible()).toBe(false);
    expect(runner.retryAfterSeconds()).toBeNull();
  });

  it('mints one intent per user action and clears it after success', async () => {
    const calls: string[] = [];
    const runner = createIntentRunner((intent) => {
      calls.push(intent.idempotencyKey);
      return Promise.resolve('saved');
    });

    await runner.run();

    expect(calls).toHaveLength(1);
    expect(runner.status()).toBe('succeeded');
    expect(runner.result()).toBe('saved');
    expect(runner.error()).toBeNull();
    expect(runner.intent()).toBeNull();

    await runner.run();

    expect(calls).toHaveLength(2);
    expect(calls[1]).not.toBe(calls[0]);
  });

  it('retry reuses the key for every explicit retry of one action', async () => {
    const calls: string[] = [];
    let attempt = 0;
    const runner = createIntentRunner((intent) => {
      calls.push(intent.idempotencyKey);
      attempt += 1;
      return attempt < 3 ? Promise.reject(failure(apiError())) : Promise.resolve('ok');
    });

    await runner.run();
    expect(runner.status()).toBe('failed');
    expect(runner.intent()?.idempotencyKey).toBe(calls[0]);

    // A failed intent is not abandoned: run() behaves like retry() and keeps the key.
    await runner.run();
    // retry() of the same failed intent sends the same key a third time.
    await runner.retry();

    expect(calls).toHaveLength(3);
    expect(new Set(calls).size).toBe(1);
    expect(calls[1]).toBe(calls[0]);
    expect(calls[2]).toBe(calls[0]);
    expect(runner.status()).toBe('succeeded');
    expect(runner.intent()).toBeNull();
  });

  it('never reuses a key after reset(), and the next action mints a fresh one', async () => {
    const calls: string[] = [];
    const runner = createIntentRunner((intent) => {
      calls.push(intent.idempotencyKey);
      return Promise.reject(failure(apiError()));
    });

    await runner.run();
    expect(runner.status()).toBe('failed');
    expect(runner.retryEligible()).toBe(true);

    runner.reset();
    expect(runner.status()).toBe('idle');
    expect(runner.intent()).toBeNull();
    expect(runner.error()).toBeNull();

    await runner.run();

    expect(calls).toHaveLength(2);
    expect(calls[1]).not.toBe(calls[0]);
  });

  it('never shares a key between two runners', async () => {
    const calls: string[] = [];
    const first = createIntentRunner((intent) => {
      calls.push(intent.idempotencyKey);
      return Promise.resolve(1);
    });
    const second = createIntentRunner((intent) => {
      calls.push(intent.idempotencyKey);
      return Promise.resolve(2);
    });

    await Promise.all([first.run(), second.run()]);

    expect(calls).toHaveLength(2);
    expect(calls[0]).not.toBe(calls[1]);
  });

  it('keeps the intent when the attempt is aborted, so the next run reuses its key', async () => {
    const calls: string[] = [];
    let attempt = 0;
    const runner = createIntentRunner((intent) => {
      calls.push(intent.idempotencyKey);
      attempt += 1;
      return attempt === 1 ? Promise.reject(abortError()) : Promise.resolve('ok');
    });

    await runner.run();

    expect(runner.status()).toBe('idle');
    expect(runner.error()).toBeNull();
    expect(runner.retryEligible()).toBe(false);
    expect(runner.intent()?.idempotencyKey).toBe(calls[0]);

    await runner.run();

    expect(calls).toHaveLength(2);
    expect(calls[1]).toBe(calls[0]);
    expect(runner.status()).toBe('succeeded');
  });

  it('starts one attempt for concurrent run() and retry() calls', async () => {
    const calls: string[] = [];
    let release: (value: string) => void = () => undefined;
    const pending = new Promise<string>((resolve) => {
      release = resolve;
    });
    const runner = createIntentRunner((intent) => {
      calls.push(intent.idempotencyKey);
      return pending;
    });

    const first = runner.run();
    const second = runner.run();
    await runner.retry();

    expect(calls).toHaveLength(1);
    expect(runner.status()).toBe('running');
    expect(runner.retryEligible()).toBe(false);

    release('done');
    await Promise.all([first, second]);

    expect(calls).toHaveLength(1);
    expect(runner.status()).toBe('succeeded');
    expect(runner.result()).toBe('done');
  });

  it('clears the previous result while a new attempt runs', async () => {
    let release: (value: string) => void = () => undefined;
    const pending = new Promise<string>((resolve) => {
      release = resolve;
    });
    let attempt = 0;
    const runner = createIntentRunner(() => {
      attempt += 1;
      return attempt === 1 ? Promise.resolve('first') : pending;
    });

    await runner.run();
    expect(runner.result()).toBe('first');

    const second = runner.run();
    expect(runner.status()).toBe('running');
    expect(runner.result()).toBeUndefined();

    release('second');
    await second;
    expect(runner.result()).toBe('second');
  });

  it('honours Retry-After with exactly one timer and no automatic retry', async () => {
    vi.useFakeTimers();
    const calls: string[] = [];
    let attempt = 0;
    const runner = createIntentRunner((intent) => {
      calls.push(intent.idempotencyKey);
      attempt += 1;
      if (attempt === 1) {
        return Promise.reject(failure(rateLimited()));
      }
      return Promise.resolve('ok');
    });

    await runner.run();

    expect(runner.status()).toBe('failed');
    expect(runner.error()?.kind).toBe('rate_limited');
    expect(runner.retryAfterSeconds()).toBe(30);
    expect(runner.retryEligible()).toBe(false);
    expect(vi.getTimerCount()).toBe(1);
    expect(calls).toHaveLength(1);

    // Neither entry point starts an attempt inside the server's interval, and neither adds a
    // second timer or an automatic retry.
    await runner.retry();
    await runner.run();
    expect(calls).toHaveLength(1);
    expect(vi.getTimerCount()).toBe(1);

    await vi.advanceTimersByTimeAsync(29_999);
    expect(runner.retryEligible()).toBe(false);

    await vi.advanceTimersByTimeAsync(1);
    expect(runner.retryEligible()).toBe(true);

    await runner.retry();

    expect(calls).toHaveLength(2);
    expect(calls[1]).toBe(calls[0]);
    expect(runner.status()).toBe('succeeded');
    expect(runner.retryAfterSeconds()).toBeNull();
  });

  it('opens the Retry-After window on the injected clock', async () => {
    vi.useFakeTimers();
    let clock = 0;
    let calls = 0;
    const runner = createIntentRunner(
      () => {
        calls += 1;
        return calls === 1 ? Promise.reject(failure(rateLimited())) : Promise.resolve('ok');
      },
      { now: () => clock },
    );

    await runner.run();
    expect(vi.getTimerCount()).toBe(1);

    // The timer has not fired; only the injected clock passed the deadline the failure stamped.
    // `retryEligible` was not read while waiting, so this first evaluation proves it consults
    // `now()` rather than trusting the timer alone.
    clock = 30_000;
    expect(runner.retryEligible()).toBe(true);

    await runner.retry();
    expect(calls).toBe(2);
    expect(runner.status()).toBe('succeeded');
  });

  it('treats a retryable failure without Retry-After as eligible immediately', async () => {
    vi.useFakeTimers();
    const calls: string[] = [];
    let attempt = 0;
    const runner = createIntentRunner((intent) => {
      calls.push(intent.idempotencyKey);
      attempt += 1;
      if (attempt === 1) {
        return Promise.reject(failure(rateLimited(null)));
      }
      return Promise.resolve('ok');
    });

    await runner.run();

    expect(runner.retryAfterSeconds()).toBeNull();
    expect(vi.getTimerCount()).toBe(0);
    expect(runner.retryEligible()).toBe(true);

    await runner.retry();

    expect(calls).toHaveLength(2);
    expect(calls[1]).toBe(calls[0]);
    expect(runner.status()).toBe('succeeded');
  });

  it('treats a zero Retry-After as eligible immediately without a timer', async () => {
    vi.useFakeTimers();
    const unavailable = apiError({
      kind: 'unavailable',
      status: 503,
      retryable: true,
      retryAfterSeconds: 0,
    });
    const runner = createIntentRunner(() => Promise.reject(failure(unavailable)));

    await runner.run();

    expect(runner.retryAfterSeconds()).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
    expect(runner.retryEligible()).toBe(true);
  });

  it('treats a non-finite or negative Retry-After as no wait', async () => {
    vi.useFakeTimers();

    for (const seconds of [Number.NaN, -7]) {
      let calls = 0;
      const unavailable = apiError({
        kind: 'unavailable',
        status: 503,
        retryable: true,
        retryAfterSeconds: seconds,
      });
      const runner = createIntentRunner(() => {
        calls += 1;
        return Promise.reject(failure(unavailable));
      });

      await runner.run();

      expect(runner.retryAfterSeconds()).toBeNull();
      expect(runner.retryEligible()).toBe(true);
      expect(vi.getTimerCount()).toBe(0);
      expect(calls).toBe(1);
    }
  });

  it('clamps a huge Retry-After to the largest timer delay', async () => {
    vi.useFakeTimers();
    let attempt = 0;
    const farFuture = apiError({
      kind: 'unavailable',
      status: 503,
      retryable: true,
      retryAfterSeconds: Number.MAX_SAFE_INTEGER,
    });
    const runner = createIntentRunner(() => {
      attempt += 1;
      return attempt === 1 ? Promise.reject(failure(farFuture)) : Promise.resolve('ok');
    });

    await runner.run();

    expect(runner.retryAfterSeconds()).toBe(Number.MAX_SAFE_INTEGER);
    expect(runner.retryEligible()).toBe(false);
    expect(vi.getTimerCount()).toBe(1);

    // The clamped delay is 2^31-1 ms; one millisecond earlier the retry is still closed.
    await vi.advanceTimersByTimeAsync(2 ** 31 - 2);
    expect(runner.retryEligible()).toBe(false);

    await vi.advanceTimersByTimeAsync(1);
    expect(runner.retryEligible()).toBe(true);

    await runner.retry();
    expect(attempt).toBe(2);
    expect(runner.status()).toBe('succeeded');
  });

  it('never makes a non-retryable 422 validation failure eligible', async () => {
    vi.useFakeTimers();
    let calls = 0;
    const validation = apiError({
      kind: 'validation',
      status: 422,
      code: 'validation_failed',
      retryable: false,
    });
    const runner = createIntentRunner(() => {
      calls += 1;
      return Promise.reject(failure(validation));
    });

    await runner.run();
    const intent = runner.intent();

    expect(runner.status()).toBe('failed');
    expect(runner.retryEligible()).toBe(false);
    expect(vi.getTimerCount()).toBe(0);

    await runner.retry();

    expect(calls).toBe(1);
    expect(runner.intent()).toBe(intent);
    expect(runner.error()?.kind).toBe('validation');
  });

  it('run() after a 422 starts a new intent with a new key', async () => {
    const keys: string[] = [];
    let attempt = 0;
    const validation = apiError({ kind: 'validation', status: 422, retryable: false });
    const runner = createIntentRunner((intent) => {
      keys.push(intent.idempotencyKey);
      attempt += 1;
      return attempt === 1 ? Promise.reject(failure(validation)) : Promise.resolve('ok');
    });

    await runner.run();
    expect(keys).toHaveLength(1);

    // The caller fixed the field and submitted again: a new action, hence a new key.
    await runner.run();

    expect(keys).toHaveLength(2);
    expect(keys[1]).not.toBe(keys[0]);
    expect(runner.status()).toBe('succeeded');
    expect(runner.intent()).toBeNull();
  });

  it('never retries a 412 precondition failure (the dialog reloads instead)', async () => {
    vi.useFakeTimers();
    let calls = 0;
    const stale = apiError({
      kind: 'precondition_failed',
      status: 412,
      code: 'precondition_failed',
      retryable: false,
    });
    const runner = createIntentRunner(() => {
      calls += 1;
      return Promise.reject(failure(stale));
    });

    await runner.run();

    expect(runner.status()).toBe('failed');
    expect(runner.error()?.kind).toBe('precondition_failed');
    expect(runner.retryEligible()).toBe(false);

    await runner.retry();

    expect(calls).toBe(1);
    expect(runner.status()).toBe('failed');
  });

  it('run() after a 412 starts a new intent with a new key', async () => {
    const keys: string[] = [];
    let attempt = 0;
    const stale = apiError({
      kind: 'precondition_failed',
      status: 412,
      code: 'precondition_failed',
      retryable: false,
    });
    const runner = createIntentRunner((intent) => {
      keys.push(intent.idempotencyKey);
      attempt += 1;
      return attempt === 1 ? Promise.reject(failure(stale)) : Promise.resolve('ok');
    });

    await runner.run();
    expect(keys).toHaveLength(1);

    // After reloading the current revision the caller submits again: a new action, new key.
    await runner.run();

    expect(keys).toHaveLength(2);
    expect(keys[1]).not.toBe(keys[0]);
    expect(runner.status()).toBe('succeeded');
  });

  it('reset() clears the Retry-After timer and every signal', async () => {
    vi.useFakeTimers();
    const runner = createIntentRunner(() => Promise.reject(failure(rateLimited())));

    await runner.run();
    expect(vi.getTimerCount()).toBe(1);

    runner.reset();

    expect(vi.getTimerCount()).toBe(0);
    expect(runner.status()).toBe('idle');
    expect(runner.result()).toBeUndefined();
    expect(runner.error()).toBeNull();
    expect(runner.intent()).toBeNull();
    expect(runner.retryAfterSeconds()).toBeNull();
    expect(runner.retryEligible()).toBe(false);
  });

  it('reset() does not cancel an in-flight attempt but ignores its late outcome', async () => {
    let release: (value: string) => void = () => undefined;
    const pending = new Promise<string>((resolve) => {
      release = resolve;
    });
    const runner = createIntentRunner(() => pending);

    const inFlight = runner.run();
    expect(runner.status()).toBe('running');

    runner.reset();
    expect(runner.status()).toBe('idle');

    release('late');
    await inFlight;

    expect(runner.status()).toBe('idle');
    expect(runner.result()).toBeUndefined();
  });

  it('rethrows a programming error from run() and records no typed failure', async () => {
    const runner = createIntentRunner(() => Promise.reject(new Error('boom')));

    await expect(runner.run()).rejects.toThrow('boom');

    expect(runner.status()).toBe('failed');
    expect(runner.error()).toBeNull();
    expect(runner.retryEligible()).toBe(false);
    expect(runner.intent()).not.toBeNull();
  });

  it('rethrows a programming error from retry() and records no typed failure', async () => {
    let attempt = 0;
    const runner = createIntentRunner(() => {
      attempt += 1;
      if (attempt === 1) {
        return Promise.reject(failure(apiError()));
      }
      return Promise.reject(new Error('boom'));
    });

    await runner.run();
    expect(runner.retryEligible()).toBe(true);

    await expect(runner.retry()).rejects.toThrow('boom');

    expect(runner.status()).toBe('failed');
    expect(runner.error()).toBeNull();
    expect(runner.retryEligible()).toBe(false);
  });
});
