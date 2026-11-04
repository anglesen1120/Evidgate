// `evidgate gate --wait`: the jittered backoff, the server-named wait it obeys, and the cap
// that stops either of them from outrunning the wait.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { CliError } from '../errors.js';
import { resetSecrets } from '../redact.js';

import { devStubFetch } from './devExchange.testing.js';
import {
  cacheDirs,
  capture,
  decided,
  devEnv,
  evaluated,
  fakeClock,
  gateArgv,
  invoke,
  pendingOutcome,
  runId,
} from './gate.kit.test.js';
import { rateLimited } from './gate.poll.testing.js';
import { pollDecision } from './gatePoll.js';

describe('gate --wait', () => {
  const dirs = cacheDirs();

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await dirs.reset();
  });

  it('honours Retry-After on 429 and keeps polling', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch((_request, index) =>
      index < 3 ? rateLimited('2') : decided(evaluated({ decision: 'PASS' })),
    );
    const out = capture();

    const { code, clock } = await invoke(
      gateArgv(['--run', runId, '--wait', '1m']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(0);
    // Three attempts inside `callApi`, then one more poll after the server-named wait.
    expect(stub.calls).toHaveLength(4);
    const lastSleep = clock.sleeps.at(-1) ?? 0;
    expect(lastSleep).toBeGreaterThanOrEqual(2_000);
    expect(lastSleep).toBeLessThanOrEqual(10_000);
  });

  it('obeys a Retry-After longer than the poll cap', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch((_request, index) =>
      index < 3 ? rateLimited('30') : decided(evaluated({ decision: 'PASS' })),
    );
    const out = capture();

    const { code, clock } = await invoke(
      gateArgv(['--run', runId, '--wait', '5m']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(0);
    // The pause before the poll that decided is the server's 30 s, not the 10 s jitter cap.
    expect(clock.sleeps.at(-1)).toBe(30_000);
  });

  it('caps Retry-After at the callApi ceiling, not the poll cap', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch((_request, index) =>
      index < 3 ? rateLimited('3600') : decided(evaluated({ decision: 'PASS' })),
    );
    const out = capture();

    const { code, clock } = await invoke(
      gateArgv(['--run', runId, '--wait', '5m']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(0);
    expect(clock.sleeps.at(-1)).toBe(60_000);
  });

  it('rises with the attempt count and then plateaus at the 10 s cap', async () => {
    const clock = fakeClock();
    let polls = 0;

    const outcome = await pollDecision({
      waitMs: 10 * 60_000,
      deps: { now: clock.now, sleep: clock.sleep, random: () => 0.9 },
      next: () => {
        polls += 1;
        return Promise.resolve({ kind: 'body', outcome: pendingOutcome });
      },
    });

    expect(outcome.decision).toBeNull();
    // One poll before each sleep, plus the poll that starts on the deadline.
    expect(polls).toBe(clock.sleeps.length + 1);
    const capped = clock.sleeps.filter((milliseconds) => milliseconds === 10_000);
    expect(capped.length).toBeGreaterThanOrEqual(3);
    // The last pause is clamped to what the wait has left, so only the ones before it are capped.
    expect(clock.sleeps.slice(-4, -1)).toStrictEqual([10_000, 10_000, 10_000]);
    expect((clock.sleeps.at(-1) ?? 0) <= 10_000).toBe(true);
    // Never shorter, and strictly longer at least once before the cap is reached; the final
    // pause is exempt because it is clamped to the wait that is left.
    const unclamped = clock.sleeps.slice(0, -1);
    for (let index = 1; index < unclamped.length; index += 1) {
      expect(unclamped[index]).toBeGreaterThanOrEqual(unclamped[index - 1] ?? 0);
    }
    expect(new Set(clock.sleeps).size).toBeGreaterThanOrEqual(3);
    expect(clock.sleeps.every((milliseconds) => milliseconds <= 10_000)).toBe(true);
  });

  it('throws the last failure when the request, not the deadline, ended the poll', async () => {
    const clock = fakeClock();
    const failure = new CliError('the run decision request failed (HTTP 503)', {
      code: 'request_failed',
    });
    let polls = 0;

    // A final poll its own request timeout ended carries no deadline provenance, so the
    // earlier pending answer may not turn it into a pending exit.
    await expect(
      pollDecision({
        waitMs: 1_000,
        deps: { now: clock.now, sleep: clock.sleep, random: () => 0 },
        next: () => {
          polls += 1;
          return Promise.resolve(
            polls === 1
              ? { kind: 'body', outcome: pendingOutcome }
              : { kind: 'transient', error: failure },
          );
        },
      }),
    ).rejects.toBe(failure);
  });
});
