// 04.2.4-T1.2: the upload retry layer (04.2.4 §5 retry rule, §8; task §9 rows
// `retry_after_honoured` and `deadline_respected`). The policy is the 04.2.3 sender's
// (`backoffDelay`, `retryAfter`, `attemptSignal`, `isRetryable`), so this spec only proves
// the upload's own wrapping: the deadline, the permanent-failure short-circuit, the
// `Retry-After` floor, the attempt cap and the `EVIDGATE_UPLOAD_DEADLINE` grammar.
import { describe, expect, it } from 'vitest';

import { CliError } from './errors.js';
import {
  deadlineExceeded,
  defaultUploadDeadlineMs,
  maxUploadAttempts,
  maxUploadDeadlineMs,
  resolveUploadDeadlineMs,
  retryAttempts,
  type Attempt,
} from './uploadRetry.js';

/** A virtual clock: `sleep` advances what `now` reads, so no case waits. */
function clock(): {
  readonly now: () => number;
  readonly sleep: (ms: number) => Promise<void>;
  readonly sleeps: number[];
  readonly at: () => number;
} {
  let current = 1_000;
  const sleeps: number[] = [];
  return {
    now: () => current,
    sleep: (ms: number) => {
      sleeps.push(ms);
      current += ms;
      return Promise.resolve();
    },
    sleeps,
    at: () => current,
  };
}

describe('EVIDGATE_UPLOAD_DEADLINE', () => {
  it('defaults_to_thirty_minutes', () => {
    expect(resolveUploadDeadlineMs({})).toBe(defaultUploadDeadlineMs);
    expect(defaultUploadDeadlineMs).toBe(1_800_000);
    expect(resolveUploadDeadlineMs({ EVIDGATE_UPLOAD_DEADLINE: '  ' })).toBe(
      defaultUploadDeadlineMs,
    );
  });

  it('accepts_seconds_and_suffixes', () => {
    expect(resolveUploadDeadlineMs({ EVIDGATE_UPLOAD_DEADLINE: '90' })).toBe(90_000);
    expect(resolveUploadDeadlineMs({ EVIDGATE_UPLOAD_DEADLINE: '45s' })).toBe(45_000);
    expect(resolveUploadDeadlineMs({ EVIDGATE_UPLOAD_DEADLINE: '30m' })).toBe(1_800_000);
    expect(resolveUploadDeadlineMs({ EVIDGATE_UPLOAD_DEADLINE: '2h' })).toBe(7_200_000);
    expect(resolveUploadDeadlineMs({ EVIDGATE_UPLOAD_DEADLINE: '24h' })).toBe(maxUploadDeadlineMs);
  });

  it('refuses_a_bad_value_without_echoing_it', () => {
    for (const value of ['0', '0s', '-5', '1.5', '10d', '25h', 'forever', '30mm']) {
      let thrown: unknown;
      try {
        resolveUploadDeadlineMs({ EVIDGATE_UPLOAD_DEADLINE: value });
      } catch (error) {
        thrown = error;
      }
      expect(thrown).toBeInstanceOf(CliError);
      const error = thrown as CliError;
      expect(error.code).toBe('invalid_config');
      expect(error.message).toContain('EVIDGATE_UPLOAD_DEADLINE');
      expect(error.message).not.toContain(value);
    }
  });
});

describe('retryAttempts', () => {
  it('honours_retry_after_as_a_floor', async () => {
    const virtual = clock();
    const attempts: number[] = [];
    const value = await retryAttempts(
      (attemptNo): Attempt<string> => {
        attempts.push(attemptNo);
        if (attemptNo === 1) {
          return { ok: false, error: new Error('busy'), retryable: true, retryAfterMs: 5_000 };
        }
        return { ok: true, value: 'done' };
      },
      {
        deadlineAt: virtual.now() + 60_000,
        deps: { sleep: virtual.sleep, now: virtual.now, random: () => 0 },
        describe: () => 'busy',
      },
    );

    expect(value).toBe('done');
    expect(attempts).toEqual([1, 2]);
    // Full jitter at random 0 is 0 ms, so the server's `Retry-After` is the whole wait.
    expect(virtual.sleeps).toEqual([5_000]);
  });

  it('uses_the_shared_jittered_backoff', async () => {
    const virtual = clock();
    await retryAttempts(
      (attemptNo): Attempt<number> =>
        attemptNo < 3
          ? { ok: false, error: new Error('503'), retryable: true }
          : { ok: true, value: attemptNo },
      {
        deadlineAt: virtual.now() + 600_000,
        deps: { sleep: virtual.sleep, now: virtual.now, random: () => 1 },
        describe: () => '503',
      },
    );

    // `backoffDelay` is the sender's own: base 500 ms doubled per attempt with full jitter,
    // capped at 30 s. At random 1 the full base is taken.
    expect(virtual.sleeps).toEqual([500, 1_000]);
  });

  it('refuses_to_sleep_past_the_deadline', async () => {
    const virtual = clock();
    const started = virtual.now();
    let thrown: unknown;
    try {
      await retryAttempts(
        (): Attempt<never> => ({
          ok: false,
          error: new CliError('unavailable', { code: 'unavailable', requestId: 'req-9' }),
          retryable: true,
          retryAfterMs: 10_000,
        }),
        {
          deadlineAt: started + 5_000,
          deps: { sleep: virtual.sleep, now: virtual.now, random: () => 0 },
          describe: () => 'unavailable (HTTP 503) request_id=req-9',
        },
      );
    } catch (error) {
      thrown = error;
    }

    const error = thrown as CliError;
    expect(error.code).toBe('deadline_exceeded');
    // The last error is described, never the URL or the file.
    expect(error.message).toContain('unavailable (HTTP 503) request_id=req-9');
    expect(virtual.sleeps).toEqual([]);
  });

  it('never_retries_a_permanent_failure', async () => {
    const virtual = clock();
    const attempts: number[] = [];
    const permanent = new CliError('the artifact is not pending', {
      code: 'artifact_state_conflict',
    });
    let thrown: unknown;
    try {
      await retryAttempts(
        (attemptNo): Attempt<never> => {
          attempts.push(attemptNo);
          return { ok: false, error: permanent, retryable: false };
        },
        {
          deadlineAt: virtual.now() + 60_000,
          deps: { sleep: virtual.sleep, now: virtual.now, random: () => 0.5 },
          describe: () => 'conflict',
        },
      );
    } catch (error) {
      thrown = error;
    }

    expect(thrown).toBe(permanent);
    expect(attempts).toEqual([1]);
    expect(virtual.sleeps).toEqual([]);
  });

  it('caps_the_attempt_count_and_returns_the_last_error', async () => {
    const virtual = clock();
    let calls = 0;
    const last = new Error('still failing');
    let thrown: unknown;
    try {
      await retryAttempts(
        (): Attempt<never> => {
          calls += 1;
          return { ok: false, error: last, retryable: true };
        },
        {
          deadlineAt: virtual.now() + 86_400_000,
          deps: { sleep: virtual.sleep, now: virtual.now, random: () => 0 },
          describe: () => 'still failing',
        },
      );
    } catch (error) {
      thrown = error;
    }

    expect(thrown).toBe(last);
    expect(calls).toBe(maxUploadAttempts);
    expect(virtual.sleeps).toHaveLength(maxUploadAttempts - 1);
  });

  it('deadline_exceeded_names_only_the_described_code', () => {
    const error = deadlineExceeded('upload_put_failed (HTTP 412)');
    expect(error.code).toBe('deadline_exceeded');
    expect(error.message).toContain('upload_put_failed (HTTP 412)');
    expect(error.hint).toContain('EVIDGATE_UPLOAD_DEADLINE');
  });
});
