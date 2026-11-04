// `evidgate gate --wait`: an abort or a stalled read ends the poll, and whichever bound ended
// it — the wait's deadline or the request's own timeout — is what the exit code reports.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { resetSecrets } from '../redact.js';
import { perRequestTimeoutMs } from '../senderLimits.js';

import { devStubFetch } from './devExchange.testing.js';
import {
  cacheDirs,
  capture,
  decided,
  devEnv,
  gateArgv,
  invoke,
  pending,
  refusal,
  runId,
  withTokenExchange,
} from './gate.kit.test.js';
import { hangUntilAborted, stallingBody } from './gate.poll.testing.js';

describe('gate --wait', () => {
  const dirs = cacheDirs();

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await dirs.reset();
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
});
