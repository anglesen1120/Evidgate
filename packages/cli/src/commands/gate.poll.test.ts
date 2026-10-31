// `evidgate gate --wait`: the deadline arithmetic, the jittered backoff and its cap, the
// server-named waits, and the rule that a wait never ends as a pass just because the run did
// not answer in time.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';

import type { GateOutcome } from './gateDecision.js';
import { pollDecision } from './gatePoll.js';
import {
  cacheDirs,
  capture,
  decided,
  devEnv,
  exchangeBody,
  fakeClock,
  gateArgv,
  ingestEnv,
  invoke,
  jsonResponse,
  refusal,
  runId,
  stubFetch,
  sum,
} from './gate.kit.test.js';

const pending: GateOutcome = {
  decision: null,
  completeness: null,
  evaluationId: null,
  mode: null,
  url: null,
  reasons: [],
};

function rateLimited(retryAfterSeconds: string): Response {
  return jsonResponse(
    429,
    {
      error: {
        code: 'rate_limited',
        message: 'slow down',
        request_id: 'req-8',
        retryable: true,
      },
    },
    { 'retry-after': retryAfterSeconds },
  );
}

describe('gate --wait', () => {
  const dirs = cacheDirs();

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await dirs.reset();
  });

  it('exits 2 for a pending run after a single poll when --wait is absent', async () => {
    const env = await devEnv(dirs);
    const stub = stubFetch(() => decided({ decision: null }));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(2);
    expect(stub.calls).toHaveLength(1);
    expect(out.stdout.join('')).toBe('');
    expect(out.stderr.join('')).toBe(
      `evidgate: gate: run ${runId} has no decision yet (pending)\n`,
    );
  });

  it('exits 2 when the run is still pending at the deadline, after a final poll', async () => {
    const env = await devEnv(dirs);
    const stub = stubFetch(() => decided({ decision: null }));
    const out = capture();

    const { code, clock } = await invoke(
      gateArgv(['--run', runId, '--wait', '10s']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(2);
    // A 1 s floor never spins: one poll per slept second, plus the poll at the deadline.
    expect(stub.calls).toHaveLength(11);
    expect(clock.sleeps).toHaveLength(10);
    expect(clock.sleeps.every((milliseconds) => milliseconds === 1_000)).toBe(true);
    expect(sum(clock.sleeps)).toBe(10_000);
    expect(clock.now()).toBe(10_000);
    expect(out.stdout.join('')).toBe('');
    expect(out.stderr.join('')).toBe(`evidgate: gate: run ${runId} is still pending after 10s\n`);
  });

  it('counts a decision that arrives at the deadline', async () => {
    const env = await devEnv(dirs);
    const bodies = [{ decision: null }, { decision: null }, { decision: 'PASS' }];
    const stub = stubFetch((_request, index) => decided(bodies[index] ?? bodies[2]));
    const out = capture();

    const { code, clock } = await invoke(
      gateArgv(['--run', runId, '--wait', '2s']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(0);
    // The last poll starts on the deadline, so its answer is the one that decided the run.
    expect(stub.calls).toHaveLength(3);
    expect(clock.now()).toBe(2_000);
    expect(out.stdout.join('')).toBe(`gate: PASS (run ${runId})\n`);
  });

  it('polls to a PASS that arrives mid-wait and exits 0', async () => {
    const env = await devEnv(dirs);
    const bodies = [{ decision: null }, { decision: null }, { decision: 'PASS' }];
    const stub = stubFetch((_request, index) => decided(bodies[index] ?? bodies[2]));
    const out = capture();

    const { code, clock } = await invoke(
      gateArgv(['--run', runId, '--wait', '30s']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(0);
    expect(stub.calls).toHaveLength(3);
    expect(sum(clock.sleeps)).toBe(2_000);
    expect(out.stdout.join('')).toBe(`gate: PASS (run ${runId})\n`);
    expect(out.stderr.join('')).toBe('');
  });

  it('polls to a FAIL that arrives mid-wait and exits 1', async () => {
    const env = await devEnv(dirs);
    const bodies = [{ decision: null }, { decision: 'FAIL', reasons: ['suite api failed'] }];
    const stub = stubFetch((_request, index) => decided(bodies[index] ?? bodies[1]));
    const out = capture();

    const { code, clock } = await invoke(
      gateArgv(['--run', runId, '--wait', '1m']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(1);
    expect(stub.calls).toHaveLength(2);
    expect(sum(clock.sleeps)).toBe(1_000);
    expect(out.stdout.join('')).toBe(`gate: FAIL (run ${runId})\n  - suite api failed\n`);
  });

  it('mints a new token when the wait outlives the one it started with', async () => {
    const env = await ingestEnv(dirs);
    let exchanges = 0;
    let decisions = 0;
    const stub = stubFetch((request) => {
      if (request.url.endsWith('/v1/ci/token')) {
        exchanges += 1;
        return jsonResponse(200, exchangeBody(`egt-token-${exchanges}`));
      }
      decisions += 1;
      if (decisions === 1) {
        return decided({ decision: null });
      }
      return decisions === 2 ? refusal(401, 'token_invalid') : decided({ decision: 'PASS' });
    });
    const out = capture();

    const { code, clock } = await invoke(
      gateArgv(['--run', runId, '--wait', '5s']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(0);
    expect(exchanges).toBe(2);
    expect(clock.sleeps).toStrictEqual([1_000]);
    const paths = stub.calls.map((call) => new URL(call.request.url).pathname);
    expect(paths).toStrictEqual([
      '/v1/ci/token',
      `/v1/runs/${runId}/decision`,
      `/v1/runs/${runId}/decision`,
      '/v1/ci/token',
      `/v1/runs/${runId}/decision`,
    ]);
    expect(stub.calls.at(-1)?.request.headers.get('authorization')).toBe('Bearer egt-token-2');
    expect(out.stdout.join('')).toBe(`gate: PASS (run ${runId})\n`);
  });

  it('does not replay after the refresh outlives the wait', async () => {
    const env = await ingestEnv(dirs);
    let time = 0;
    const clock = {
      sleeps: [] as number[],
      now: () => time,
      sleep: (milliseconds: number) => {
        clock.sleeps.push(milliseconds);
        time += milliseconds;
        return Promise.resolve();
      },
    };
    let exchanges = 0;
    let decisions = 0;
    const stub = stubFetch((request) => {
      if (request.url.endsWith('/v1/ci/token')) {
        exchanges += 1;
        // The second exchange alone outlives the 2 s wait.
        if (exchanges > 1) {
          time += 2_000;
        }
        return jsonResponse(200, exchangeBody(`egt-token-${exchanges}`));
      }
      decisions += 1;
      return decisions === 1 ? decided({ decision: null }) : refusal(401, 'token_invalid');
    });
    const out = capture();

    const { code } = await invoke(
      gateArgv(['--run', runId, '--wait', '2s']),
      env,
      out,
      stub.fetchImpl,
      { clock },
    );

    expect(code).toBe(3);
    expect(out.stdout.join('')).toBe('');
    expect(out.stderr.join('')).toContain(
      'error request_failed: the token refresh outlived the --wait deadline',
    );
    // Exchange, pending poll, 401 poll, refresh: the replay never starts after the deadline.
    expect(stub.calls.map((call) => new URL(call.request.url).pathname)).toStrictEqual([
      '/v1/ci/token',
      `/v1/runs/${runId}/decision`,
      `/v1/runs/${runId}/decision`,
      '/v1/ci/token',
    ]);
  });

  it('exits 3 at the deadline when the server keeps answering 5xx', async () => {
    const env = await devEnv(dirs);
    const stub = stubFetch(() => refusal(503, 'internal', { retryable: true }));
    const out = capture();

    const { code, clock } = await invoke(
      gateArgv(['--run', runId, '--wait', '5s']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(3);
    expect(out.stdout.join('')).toBe('');
    expect(out.stderr.join('')).toContain('error internal');
    // Nothing may sleep past the deadline, whatever the server keeps answering.
    expect(Math.max(...clock.sleeps)).toBeLessThanOrEqual(5_000);
    expect(sum(clock.sleeps)).toBeLessThanOrEqual(5_000);
  });

  it('honours Retry-After on 429 and keeps polling', async () => {
    const env = await devEnv(dirs);
    const stub = stubFetch((_request, index) =>
      index < 3 ? rateLimited('2') : decided({ decision: 'PASS' }),
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
    const stub = stubFetch((_request, index) =>
      index < 3 ? rateLimited('30') : decided({ decision: 'PASS' }),
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
    const stub = stubFetch((_request, index) =>
      index < 3 ? rateLimited('3600') : decided({ decision: 'PASS' }),
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
        return Promise.resolve({ kind: 'body', outcome: pending });
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
});
