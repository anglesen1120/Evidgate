// `evidgate gate` argument handling: every flag is validated before a token is minted or a
// request is sent, a repetition is an error rather than a silently ignored value, and the
// wait accepts exactly the duration grammar up to its own 24 hour ceiling.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';

import { devStubFetch } from './devExchange.testing.js';
import {
  cacheDirs,
  capture,
  decided,
  devEnv,
  gateArgv,
  invoke,
  runId,
  type StubCall,
} from './gate.kit.test.js';

describe('gate flags', () => {
  const dirs = cacheDirs();

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await dirs.reset();
  });

  /**
   * The dev surface's stub: a dev token is an ingest credential, so the run exchanges it
   * before its first poll. `devStubFetch` answers that exchange and keeps `calls` to the
   * polls, so a refusal that made no request is visible as an empty `calls` AND `exchanges`.
   */
  function passingStub(): {
    readonly fetchImpl: typeof fetch;
    readonly calls: StubCall[];
    readonly exchanges: StubCall[];
  } {
    return devStubFetch(() => decided({ decision: 'PASS' }));
  }

  const badWaits = ['0', '0s', '-5s', '1.5m', '1d', 'abc', '', '25h', '86401s'];

  for (const wait of badWaits) {
    it(`refuses --wait ${JSON.stringify(wait)} before any request`, async () => {
      const env = await devEnv(dirs);
      const stub = passingStub();
      const out = capture();

      const { code } = await invoke(
        gateArgv(['--run', runId, '--wait', wait]),
        env,
        out,
        stub.fetchImpl,
      );

      expect(code).toBe(3);
      expect(stub.calls).toHaveLength(0);
      expect(stub.exchanges).toHaveLength(0);
      expect(out.stderr.join('')).toContain('--wait');
    });
  }

  it('accepts the 24h ceiling', async () => {
    const env = await devEnv(dirs);
    const stub = passingStub();
    const out = capture();

    const { code } = await invoke(
      gateArgv(['--run', runId, '--wait', '24h']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(0);
    // The exchange, then the one poll: a valid ceiling reaches both.
    expect(stub.exchanges).toHaveLength(1);
    expect(stub.calls.map((call) => new URL(call.request.url).pathname)).toStrictEqual([
      `/v1/runs/${runId}/decision`,
    ]);
  });

  it('accepts the documented suffixes', async () => {
    const env = await devEnv(dirs);
    const stub = passingStub();
    const out = capture();

    for (const wait of ['90', '90s', '2m', '1h']) {
      const { code } = await invoke(
        gateArgv(['--run', runId, '--wait', wait]),
        env,
        out,
        stub.fetchImpl,
      );
      expect(code).toBe(0);
    }
    // Each accepted spelling exchanges the dev credential once and asks once; the far-future
    // `expires_at` is not a fresh credential on the virtual clock, so no cache is reused.
    expect(stub.exchanges).toHaveLength(4);
    expect(stub.calls.map((call) => new URL(call.request.url).pathname)).toStrictEqual([
      `/v1/runs/${runId}/decision`,
      `/v1/runs/${runId}/decision`,
      `/v1/runs/${runId}/decision`,
      `/v1/runs/${runId}/decision`,
    ]);
  });

  it('refuses a repeated --wait', async () => {
    const env = await devEnv(dirs);
    const stub = passingStub();
    const out = capture();

    const { code } = await invoke(
      gateArgv(['--run', runId, '--wait', '5s', '--wait', '5s']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    expect(stub.exchanges).toHaveLength(0);
    expect(out.stderr.join('')).toContain('may be given only once');
  });

  it('refuses a missing --run', async () => {
    const env = await devEnv(dirs);
    const stub = passingStub();
    const out = capture();

    const { code } = await invoke(gateArgv([]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    expect(stub.exchanges).toHaveLength(0);
    expect(out.stderr.join('')).toContain('missing required flag --run');
  });

  it('refuses a run id that is not a UUID', async () => {
    const env = await devEnv(dirs);
    const stub = passingStub();
    const out = capture();

    const { code } = await invoke(
      gateArgv(['--run', '../../etc/passwd']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    expect(stub.exchanges).toHaveLength(0);
    expect(out.stderr.join('')).toContain('--run must be a UUID');
  });

  it('refuses a repeated --run', async () => {
    const env = await devEnv(dirs);
    const stub = passingStub();
    const out = capture();

    const { code } = await invoke(
      gateArgv(['--run', runId, '--run', runId]),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    expect(stub.exchanges).toHaveLength(0);
    expect(out.stderr.join('')).toContain('may be given only once');
  });
});
