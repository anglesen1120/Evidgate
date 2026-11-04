// `evidgate gate --wait`: a wait can outlive the bearer it started with, so every refresh it
// performs is bounded by the wait's own deadline rather than by the exchange's budget.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';

import { exchangeBody } from './devExchange.testing.js';
import {
  cacheDirs,
  capture,
  decided,
  devEnv,
  evaluated,
  gateArgv,
  ingestEnv,
  invoke,
  jsonResponse,
  pending,
  refusal,
  runId,
  stubFetch,
} from './gate.kit.test.js';

describe('gate --wait', () => {
  const dirs = cacheDirs();

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await dirs.reset();
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
});
