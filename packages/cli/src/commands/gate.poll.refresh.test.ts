// `evidgate gate --wait`: a wait can outlive the bearer it started with, so every refresh it
// performs is bounded by the wait's own deadline rather than by the exchange's budget.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';

import { exchangeBody } from './devExchange.testing.js';
import {
  cacheDirs,
  capture,
  decided,
  evaluated,
  gateArgv,
  ingestEnv,
  invoke,
  jsonResponse,
  pending,
  refusal,
  runId,
  stubFetch,
} from './gate.kit.testing.js';

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
});
