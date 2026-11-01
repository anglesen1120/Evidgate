// `evidgate gate --wait`: the deadline arithmetic, the jittered backoff and its cap, the
// server-named waits, and the rule that a wait never ends as a pass just because the run did
// not answer in time.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { CliError } from '../errors.js';
import { resetSecrets } from '../redact.js';
import { perRequestTimeoutMs } from '../senderLimits.js';

import { devStubFetch, exchangeBody } from './devExchange.testing.js';
import { pollDecision } from './gatePoll.js';
import {
  cacheDirs,
  capture,
  decided,
  devEnv,
  evaluated,
  fakeClock,
  gateArgv,
  ingestEnv,
  invoke,
  jsonResponse,
  pending,
  pendingOutcome,
  refusal,
  runId,
  stubFetch,
  sum,
  withTokenExchange,
} from './gate.kit.test.js';

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

/** Rejects as soon as the request's abort signal fires: a server that never answers. */
function hangUntilAborted(
  input: Request | string | URL,
  init: RequestInit | undefined,
  reject: (reason: Error) => void,
): void {
  const signal = init?.signal ?? (input instanceof Request ? input.signal : undefined);
  if (signal === undefined) {
    throw new Error('the request carries no abort signal');
  }
  const abort = (): void => {
    reject(new DOMException('the operation was aborted', 'AbortError'));
  };
  if (signal.aborted) {
    abort();
    return;
  }
  signal.addEventListener('abort', abort);
}

/**
 * A response body that delivers no chunk until the request's abort signal fires. The headers
 * are already in, so the read — not the request — is what the deadline has to end.
 */
function stallingBody(signal: AbortSignal | undefined): ReadableStream<Uint8Array> {
  return new ReadableStream<Uint8Array>({
    pull: () =>
      new Promise<never>((_resolve, reject) => {
        const abort = (): void => {
          reject(new DOMException('the operation was aborted', 'AbortError'));
        };
        if (signal === undefined) {
          return;
        }
        if (signal.aborted) {
          abort();
          return;
        }
        signal.addEventListener('abort', abort, { once: true });
      }),
  });
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
    const stub = devStubFetch(() => decided(pending()));
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
    const stub = devStubFetch(() => decided(pending()));
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
    const bodies = [pending(), pending(), evaluated({ decision: 'PASS' })];
    const stub = devStubFetch((_request, index) => decided(bodies[index] ?? bodies[2]));
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
    const bodies = [pending(), pending(), evaluated({ decision: 'PASS' })];
    const stub = devStubFetch((_request, index) => decided(bodies[index] ?? bodies[2]));
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
    const bodies = [pending(), evaluated({ decision: 'FAIL', reasons: ['suite api failed'] })];
    const stub = devStubFetch((_request, index) => decided(bodies[index] ?? bodies[1]));
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
        return decided(pending());
      }
      return decisions === 2
        ? refusal(401, 'token_invalid')
        : decided(evaluated({ decision: 'PASS' }));
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
      return decisions === 1 ? decided(pending()) : refusal(401, 'token_invalid');
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

  it('reports pending when the poll at the deadline is aborted before it answers', async () => {
    const env = await devEnv(dirs);
    let requests = 0;
    // The first poll answers pending; every later one hangs until the per-attempt budget
    // aborts it, which is what the deadline's 1 ms window does on a real network.
    const fetchImpl = withTokenExchange(
      (input, init) =>
        new Promise<Response>((resolve, reject) => {
          requests += 1;
          if (requests === 1) {
            resolve(decided(pending()));
            return;
          }
          hangUntilAborted(input, init, reject);
        }),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId, '--wait', '1s']), env, out, fetchImpl);

    expect(code).toBe(2);
    // The aborted request may be replayed inside the poll's own budget, so the count is a
    // lower bound; what matters is that the pending answer, not the abort, decides the exit.
    expect(requests).toBeGreaterThanOrEqual(2);
    expect(out.stderr.join('')).toBe(`evidgate: gate: run ${runId} is still pending after 1s\n`);
  });

  it('reports pending when the wait timer fires before the clock reaches the deadline', async () => {
    const env = await devEnv(dirs);
    let time = 0;
    const clock = {
      sleeps: [] as number[],
      now: () => time,
      // Every pause lands 1 ms short, so the final poll runs with a real 1 ms budget while
      // the clock still reads 999 against a deadline of 1000.
      sleep: (milliseconds: number) => {
        clock.sleeps.push(milliseconds);
        time += Math.max(milliseconds - 1, 0);
        return Promise.resolve();
      },
    };
    let requests = 0;
    const fetchImpl = withTokenExchange(
      (input, init) =>
        new Promise<Response>((resolve, reject) => {
          requests += 1;
          if (requests === 1) {
            resolve(decided(pending()));
            return;
          }
          hangUntilAborted(input, init, reject);
        }),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId, '--wait', '1s']), env, out, fetchImpl, {
      clock,
    });

    // The wait's own timer ended the final poll, so the pending answer stands even though the
    // clock never reached the deadline it was compared against.
    expect(code).toBe(2);
    expect(clock.now()).toBe(999);
    expect(requests).toBeGreaterThanOrEqual(2);
    expect(out.stderr.join('')).toBe(`evidgate: gate: run ${runId} is still pending after 1s\n`);
  });

  it('exits 3 when a request timeout, not the wait, ends a poll after pending', async () => {
    const env = await devEnv(dirs);
    // A clock the sleeps never move: every attempt still has the whole wait ahead of it, so
    // the wait's own timer is never the one that bounds it.
    const clock = {
      sleeps: [] as number[],
      now: () => 0,
      sleep: (milliseconds: number) => {
        clock.sleeps.push(milliseconds);
        return Promise.resolve();
      },
    };
    // `attemptSignal` builds one timer per bound; holding the request's own keeps a real 30 s
    // timeout out of the run while the wait keeps the whole 31 s to itself.
    const requestTimers: AbortController[] = [];
    const timeout = vi.spyOn(AbortSignal, 'timeout').mockImplementation((milliseconds: number) => {
      const controller = new AbortController();
      if (milliseconds === perRequestTimeoutMs) {
        requestTimers.push(controller);
      }
      return controller.signal;
    });
    let requests = 0;
    const fetchImpl = withTokenExchange((input, init) => {
      requests += 1;
      if (requests === 1) {
        return Promise.resolve(decided(pending()));
      }
      return new Promise<Response>((_resolve, reject) => {
        requestTimers.pop()?.abort(new DOMException('the operation timed out', 'TimeoutError'));
        hangUntilAborted(input, init, reject);
      });
    });
    const out = capture();

    try {
      // Longer than one request's own timeout, so the wait never becomes the shorter bound.
      const argv = gateArgv(['--run', runId, '--wait', '31s']);
      const { code } = await invoke(argv, env, out, fetchImpl, { clock });

      // The request's own timeout ended this poll with 31 s of wait still ahead, so it is the
      // state the wait ended in: exit 3, never the pending answer from the first poll.
      expect(code).toBe(3);
      expect(out.stdout.join('')).toBe('');
      expect(out.stderr.join('')).toContain('error transport: the request timed out');
    } finally {
      timeout.mockRestore();
    }
  });

  it('aborted body after pending', async () => {
    const env = await devEnv(dirs);
    let requests = 0;
    // The first poll answers pending; the poll at the deadline sends its headers and then
    // stalls, so the deadline's 1 ms abort lands inside the body read, after the headers.
    const fetchImpl = withTokenExchange((input, init) => {
      requests += 1;
      if (requests === 1) {
        return Promise.resolve(decided(pending()));
      }
      const signal = init?.signal ?? (input instanceof Request ? input.signal : undefined);
      return Promise.resolve(
        new Response(stallingBody(signal), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        }),
      );
    });
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId, '--wait', '1s']), env, out, fetchImpl);

    // The server began an answer the wait never finished reading, so the run's state is
    // unknown rather than pending: the command reports the failed read and never exits 0.
    expect(code).toBe(3);
    expect(requests).toBe(2);
    expect(out.stdout.join('')).toBe('');
    expect(out.stderr.join('')).toContain(
      'error request_failed: the run decision body could not be read',
    );
  });

  it('final poll 5xx after pending', async () => {
    const env = await devEnv(dirs);
    // The run answers pending once; every later poll is answered by a real 5xx.
    const stub = devStubFetch((_request, index) =>
      index === 0 ? decided(pending()) : refusal(503, 'internal', { retryable: true }),
    );
    const out = capture();

    const { code } = await invoke(
      gateArgv(['--run', runId, '--wait', '1s']),
      env,
      out,
      stub.fetchImpl,
    );

    // The server's last word is an infrastructure failure, so it is the state the wait
    // ended in; the earlier pending answer may not turn it into exit 2 or into a pass.
    expect(code).toBe(3);
    expect(out.stdout.join('')).toBe('');
    expect(out.stderr.join('')).toContain('error internal');
  });

  it('does not let a hanging refresh overshoot the wait deadline', async () => {
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
    const started = Date.now();
    const paths: string[] = [];
    let exchanges = 0;
    let decisions = 0;
    const fetchImpl: typeof fetch = (input) => {
      const request = input instanceof Request ? input : new Request(input);
      const path = new URL(request.url).pathname;
      paths.push(path);
      if (path === '/v1/ci/token') {
        exchanges += 1;
        // Only the deadline can end the second exchange: it never answers, and it ignores
        // the abort signal, so a bound that relied on the transport would hang here.
        if (exchanges > 1) {
          return new Promise<Response>(() => undefined);
        }
        return Promise.resolve(jsonResponse(200, exchangeBody('egt-token-1')));
      }
      decisions += 1;
      if (decisions === 1) {
        return Promise.resolve(decided(pending()));
      }
      // The 401 lands 900 ms into the 2 s wait, so the refresh has 100 ms to answer in.
      time += 900;
      return Promise.resolve(refusal(401, 'token_invalid'));
    };
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId, '--wait', '2s']), env, out, fetchImpl, {
      clock,
    });
    const elapsed = Date.now() - started;

    // Exit 3, never 2: a wait that ends on the credential is not a pending answer the run
    // gave. The proof is the time: the refresh may wait only the 100 ms the wait has left,
    // not the exchange's own budget, so this returns in about that 100 ms rather than
    // hanging on a stub that never answers.
    expect(code).toBe(3);
    expect(out.stdout.join('')).toBe('');
    expect(out.stderr.join('')).toContain(
      'error request_failed: the token refresh outlived the --wait deadline',
    );
    expect(exchanges).toBe(2);
    expect(decisions).toBe(2);
    expect(paths).toStrictEqual([
      '/v1/ci/token',
      `/v1/runs/${runId}/decision`,
      `/v1/runs/${runId}/decision`,
      '/v1/ci/token',
    ]);
    expect(elapsed).toBeLessThan(750);
  });

  it('bounds a hanging Actions-ID token request by the wait deadline', async () => {
    const env = await devEnv(dirs, {
      ACTIONS_ID_TOKEN_REQUEST_URL: 'http://127.0.0.1:8080/id-token',
      ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'request-token-value',
    });
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
    const started = Date.now();
    let idRequests = 0;
    let exchanges = 0;
    let decisions = 0;
    let noteAborted: () => void = () => undefined;
    // Resolves once the refresh's request is aborted: the signal the process waits on.
    const aborted = new Promise<void>((resolve) => {
      noteAborted = resolve;
    });
    const fetchImpl: typeof fetch = (input, init) => {
      const request = input instanceof Request ? input : new Request(input, init);
      const path = new URL(request.url).pathname;
      if (path === '/id-token') {
        idRequests += 1;
        if (idRequests > 1) {
          // The refresh's ID-token request never answers on its own: only the signal, which
          // must carry the wait's deadline and not just the 30 s request timeout, ends it.
          const signal = init?.signal ?? request.signal;
          return new Promise<Response>((_resolve, reject) => {
            signal.addEventListener(
              'abort',
              () => {
                noteAborted();
                reject(new DOMException('the operation was aborted', 'AbortError'));
              },
              { once: true },
            );
          });
        }
        return Promise.resolve(jsonResponse(200, { value: 'id-token-value' }));
      }
      if (path === '/v1/ci/token') {
        exchanges += 1;
        return Promise.resolve(jsonResponse(200, exchangeBody(`egt-token-${exchanges}`)));
      }
      decisions += 1;
      if (decisions === 1) {
        return Promise.resolve(decided(pending()));
      }
      time += 900;
      return Promise.resolve(refusal(401, 'token_invalid'));
    };
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId, '--wait', '2s']), env, out, fetchImpl, {
      clock,
    });
    const elapsed = Date.now() - started;

    // Exit 3 and nothing on stdout: the wait ended on the credential, never on a verdict.
    expect(code).toBe(3);
    expect(out.stdout.join('')).toBe('');
    expect(idRequests).toBe(2);
    expect(exchanges).toBe(1);
    expect(decisions).toBe(2);
    expect(elapsed).toBeLessThan(750);
    // Aborted at the deadline rather than at the 30 s request timeout, so no socket outlives
    // the command. Awaiting the abort itself keeps this deterministic; a missing deadline
    // bound surfaces as this spec timing out, which is the failure it exists to catch.
    await aborted;
  });

  it('exits 3 when no poll ever produced an answer', async () => {
    const env = await devEnv(dirs);
    const fetchImpl = withTokenExchange(
      (input, init) =>
        new Promise<Response>((_resolve, reject) => {
          hangUntilAborted(input, init, reject);
        }),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId, '--wait', '1s']), env, out, fetchImpl);

    expect(code).toBe(3);
    expect(out.stdout.join('')).toBe('');
  });

  it('exits 3 at the deadline when the server keeps answering 5xx', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() => refusal(503, 'internal', { retryable: true }));
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
