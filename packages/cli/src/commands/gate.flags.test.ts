// `evidgate gate` argument handling: every flag is validated before a token is minted or a
// request is sent, a repetition is an error rather than a silently ignored value, and the
// wait accepts exactly the duration grammar up to its own 24 hour ceiling.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';

import {
  cacheDirs,
  capture,
  decided,
  devEnv,
  gateArgv,
  invoke,
  runId,
  stubFetch,
} from './gate.kit.test.js';

describe('gate flags', () => {
  const dirs = cacheDirs();

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await dirs.reset();
  });

  const badWaits = ['0', '0s', '-5s', '1.5m', '1d', 'abc', '', '25h', '86401s'];

  for (const wait of badWaits) {
    it(`refuses --wait ${JSON.stringify(wait)} before any request`, async () => {
      const env = await devEnv(dirs);
      const stub = stubFetch(() => decided({ decision: 'PASS' }));
      const out = capture();

      const { code } = await invoke(
        gateArgv(['--run', runId, '--wait', wait]),
        env,
        out,
        stub.fetchImpl,
      );

      expect(code).toBe(3);
      expect(stub.calls).toHaveLength(0);
      expect(out.stderr.join('')).toContain('--wait');
    });
  }

  it('accepts the 24h ceiling', async () => {
    const env = await devEnv(dirs);
    const stub = stubFetch(() => decided({ decision: 'PASS' }));
    const out = capture();

    const { code } = await invoke(
      gateArgv(['--run', runId, '--wait', '24h']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(0);
    expect(stub.calls).toHaveLength(1);
  });

  it('accepts the documented suffixes', async () => {
    const env = await devEnv(dirs);
    const stub = stubFetch(() => decided({ decision: 'PASS' }));
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
    expect(stub.calls).toHaveLength(4);
  });

  it('refuses a repeated --wait', async () => {
    const env = await devEnv(dirs);
    const stub = stubFetch(() => decided({ decision: 'PASS' }));
    const out = capture();

    const { code } = await invoke(
      gateArgv(['--run', runId, '--wait', '5s', '--wait', '5s']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    expect(out.stderr.join('')).toContain('may be given only once');
  });

  it('refuses a missing --run', async () => {
    const env = await devEnv(dirs);
    const stub = stubFetch(() => decided({ decision: 'PASS' }));
    const out = capture();

    const { code } = await invoke(gateArgv([]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    expect(out.stderr.join('')).toContain('missing required flag --run');
  });

  it('refuses a run id that is not a UUID', async () => {
    const env = await devEnv(dirs);
    const stub = stubFetch(() => decided({ decision: 'PASS' }));
    const out = capture();

    const { code } = await invoke(
      gateArgv(['--run', '../../etc/passwd']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    expect(out.stderr.join('')).toContain('--run must be a UUID');
  });

  it('refuses a repeated --run', async () => {
    const env = await devEnv(dirs);
    const stub = stubFetch(() => decided({ decision: 'PASS' }));
    const out = capture();

    const { code } = await invoke(
      gateArgv(['--run', runId, '--run', runId]),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    expect(out.stderr.join('')).toContain('may be given only once');
  });
});
