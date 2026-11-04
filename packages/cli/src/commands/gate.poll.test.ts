// `evidgate gate --wait`: the deadline arithmetic and the exit code a wait ends in, and the
// rule that a wait never ends as a pass just because the run did not answer in time.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';

import { devStubFetch } from './devExchange.testing.js';
import {
  cacheDirs,
  capture,
  decided,
  devEnv,
  evaluated,
  gateArgv,
  invoke,
  pending,
  refusal,
  runId,
  sum,
  withTokenExchange,
} from './gate.kit.test.js';
import { hangUntilAborted } from './gate.poll.testing.js';

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
});
