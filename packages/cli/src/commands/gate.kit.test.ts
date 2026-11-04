// The shared harness is exercised here, so a broken seam fails in this file rather than as a
// puzzling assertion in a command spec: the virtual clock's arithmetic and the dev-token
// exchange's bearer.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';

import { devStubFetch, devToken, mintedToken } from './devExchange.testing.js';
import {
  cacheDirs,
  capture,
  decided,
  devEnv,
  evaluated,
  fakeClock,
  gateArgv,
  invoke,
  runId,
} from './gate.kit.testing.js';

describe('fake clock', () => {
  it('advances now by exactly what it slept and records every pause', async () => {
    const clock = fakeClock();
    expect(clock.now()).toBe(0);

    await clock.sleep(1_000);
    await clock.sleep(250);

    expect(clock.sleeps).toStrictEqual([1_000, 250]);
    expect(clock.now()).toBe(1_250);
  });

  it('does not move when a zero pause is requested', async () => {
    const clock = fakeClock();
    await clock.sleep(0);
    expect(clock.sleeps).toStrictEqual([0]);
    expect(clock.now()).toBe(0);
  });
});

// The seam every dev-token spec relies on: `devStubFetch` answers the exchange with
// `mintedToken` and the command's own request carries that bearer, not the dev credential.
describe('dev token exchange', () => {
  const dirs = cacheDirs();

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await dirs.reset();
  });

  it('hands the command the minted token, never the dev credential', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() => decided(evaluated({ decision: 'PASS' })));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(0);
    expect(stub.exchanges).toHaveLength(1);
    const exchange = stub.exchanges[0];
    expect(JSON.parse((await exchange?.request.clone().text()) ?? '{}')).toStrictEqual({
      ingest_token: devToken,
    });
    expect(stub.calls[0]?.request.headers.get('authorization')).toBe(`Bearer ${mintedToken}`);
    expect(out.stdout.join('')).not.toContain(devToken);
    expect(out.stdout.join('')).not.toContain(mintedToken);
  });
});
