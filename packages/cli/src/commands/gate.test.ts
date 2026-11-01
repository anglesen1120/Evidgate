// `evidgate gate` on the binary: the verdict-to-exit-code mapping, the human output and its
// redaction, the fail-closed reading of an unreadable body, and every transport answer that
// must fail the build instead of being replayed into a verdict.
import { createServer } from 'node:http';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { ExitCode } from '../exitCodes.js';
import { resetSecrets } from '../redact.js';

import { exitCodeForDecision, type GateDecisionValue } from './gateDecision.js';
import {
  cacheDirs,
  capture,
  decided,
  devEnv,
  devStubFetch,
  devToken,
  exchangeBody,
  gateArgv,
  invoke,
  mintedToken,
  refusal,
  runId,
} from './gate.kit.test.js';

describe('gate', () => {
  const dirs = cacheDirs();

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await dirs.reset();
  });

  // Only a literal PASS may exit 0; every other verdict has its own code.
  it('maps each verdict to a literal exit code', () => {
    const table: ReadonlyArray<readonly [GateDecisionValue | null, number]> = [
      ['PASS', 0],
      ['FAIL', 1],
      ['INCONCLUSIVE', 2],
      [null, 2],
    ];

    for (const [decision, expected] of table) {
      expect(exitCodeForDecision(decision)).toBe(expected);
    }
  });

  it('keeps the process contract 0/1/2/3', () => {
    expect(ExitCode).toStrictEqual({ Ok: 0, Fail: 1, Inconclusive: 2, Error: 3 });
    expect(exitCodeForDecision('PASS')).toBe(0);
    expect(exitCodeForDecision('FAIL')).toBe(1);
    expect(exitCodeForDecision('INCONCLUSIVE')).toBe(2);
  });

  it('exits 0 only for PASS and prints the verdict with its reasons', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() =>
      decided({ decision: 'PASS', reasons: ['all suites passed', 'flaky retry absorbed'] }),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(0);
    expect(out.stdout.join('')).toBe(
      `gate: PASS (run ${runId})\n  - all suites passed\n  - flaky retry absorbed\n`,
    );
    expect(out.stderr.join('')).toBe('');
    expect(stub.calls).toHaveLength(1);
    const call = stub.calls[0];
    expect(call?.request.method).toBe('GET');
    // `createApiClient` puts the SDK's `/v1` server prefix on the base URL.
    expect(new URL(call?.request.url ?? '').pathname).toBe(`/v1/runs/${runId}/decision`);
    expect(call?.request.headers.get('authorization')).toBe(`Bearer ${mintedToken}`);
    expect(new URL(call?.request.url ?? '').search).toBe('');
    // The dev token is not sent raw: one exchange mints the bearer the poll carries.
    expect(stub.exchanges).toHaveLength(1);
    const exchange = stub.exchanges[0];
    expect(exchange?.request.method).toBe('POST');
    expect(new URL(exchange?.request.url ?? '').pathname).toBe('/v1/ci/token');
    expect(JSON.parse((await exchange?.request.text()) ?? '{}')).toStrictEqual({
      ingest_token: devToken,
    });
  });

  it('exits 1 for FAIL', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() => decided({ decision: 'FAIL', reasons: ['suite web failed'] }));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(1);
    expect(out.stdout.join('')).toBe(`gate: FAIL (run ${runId})\n  - suite web failed\n`);
    expect(stub.calls).toHaveLength(1);
  });

  it('caps a server reason to one escaped line', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() =>
      decided({ decision: 'FAIL', reasons: [`${'a'.repeat(300)}\nforged: line`] }),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(1);
    // One verdict line, one reason line (capped at 200 code points), and the closing newline.
    expect(out.stdout.join('').split('\n')).toHaveLength(3);
    expect(out.stdout.join('')).toBe(`gate: FAIL (run ${runId})\n  - ${'a'.repeat(200)}\n`);
  });

  it('redacts a token that straddles the reason cap before capping', async () => {
    const env = await devEnv(dirs);
    // The token starts at code point 191, so a cap applied before redaction would print its
    // first ten characters; the redactor only matches the whole registered value.
    const stub = devStubFetch(() =>
      decided({
        decision: 'FAIL',
        reasons: [`${'x'.repeat(190)}dev-token-value`],
      }),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(1);
    // `[REDACTED]` fits inside the 200-code-point cap, so the whole marker survives.
    expect(out.stdout.join('')).toBe(
      `gate: FAIL (run ${runId})\n  - ${'x'.repeat(190)}[REDACTED]\n`,
    );
    expect(out.stdout.join('')).not.toContain('dev-');
  });

  it('collapses reasons beyond the printed cap', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() =>
      decided({ decision: 'FAIL', reasons: Array.from({ length: 60 }, (_, i) => `r${i}`) }),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(1);
    const lines = out.stdout.join('').split('\n');
    // The verdict, 50 reason lines, the `(k more)` line, and the closing newline.
    expect(lines).toHaveLength(53);
    expect(lines[1]).toBe('  - r0');
    expect(lines[50]).toBe('  - r49');
    expect(lines[51]).toBe('  … (10 more)');
  });

  it('exits 2 for INCONCLUSIVE', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() => decided({ decision: 'INCONCLUSIVE' }));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(2);
    expect(out.stdout.join('')).toBe(`gate: INCONCLUSIVE (run ${runId})\n`);
    expect(stub.calls).toHaveLength(1);
  });

  describe('unreadable responses', () => {
    const cases: ReadonlyArray<readonly [string, () => Response]> = [
      ['a lower-case decision', () => decided({ decision: 'pass' })],
      ['an empty decision', () => decided({ decision: '' })],
      ['an unknown decision', () => decided({ decision: 'MAYBE' })],
      ['a numeric decision', () => decided({ decision: 5 })],
      ['a missing decision member', () => decided({})],
      ['a null body', () => decided(null)],
      ['an array body', () => decided([])],
      ['a string body', () => decided('PASS')],
      ['non-string reasons', () => decided({ decision: 'PASS', reasons: [1] })],
      [
        'invalid JSON',
        () =>
          new Response('{"decision":', {
            status: 200,
            headers: { 'content-type': 'application/json' },
          }),
      ],
      ['an empty body', () => new Response(null, { status: 200 })],
    ];

    for (const [label, respond] of cases) {
      it(`exits 3 for ${label}`, async () => {
        const env = await devEnv(dirs);
        const stub = devStubFetch(respond);
        const out = capture();

        const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

        expect(code).toBe(3);
        // A malformed answer is a contract violation, not a slow server: no retry.
        expect(stub.calls).toHaveLength(1);
        expect(out.stderr.join('')).toContain('was not valid');
        expect(out.stdout.join('')).toBe('');
      });
    }
  });

  it('refuses an oversized decision body instead of reporting a verdict', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(
      () =>
        new Response(
          new ReadableStream({
            start(controller) {
              // One chunk past the 256 KiB cap of a body that is otherwise valid JSON.
              controller.enqueue(new TextEncoder().encode('a'.repeat(257 * 1024)));
              controller.close();
            },
          }),
          { status: 200, headers: { 'content-type': 'application/json' } },
        ),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(1);
    expect(out.stdout.join('')).toBe('');
    expect(out.stderr.join('')).toContain('error request_failed: the run decision body exceeded');
  });

  it('exits 3 when the body stream fails after its headers', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(
      () =>
        new Response(
          new ReadableStream({
            start(controller) {
              controller.enqueue(new TextEncoder().encode('{"decision":"PASS"'));
              controller.error(new Error('connection reset'));
            },
          }),
          { status: 200, headers: { 'content-type': 'application/json' } },
        ),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    // A partially read body can never be a verdict: PASS must not reach the exit code.
    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(1);
    expect(out.stdout.join('')).toBe('');
    expect(out.stderr.join('')).toContain('error request_failed: the run decision body');
  });

  it('exits 3 on a transport failure after the bounded retries', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() => {
      throw new TypeError('fetch failed');
    });
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    // `callApi` replays a GET up to three times before the failure surfaces.
    expect(stub.calls).toHaveLength(3);
    expect(out.stderr.join('')).toContain('error transport');
  });

  it('does not replay a refused redirect', async () => {
    const env = await devEnv(dirs);
    // The shape Node's fetch rejects `redirect: 'error'` with; the target was never contacted.
    const stub = devStubFetch(() => {
      throw new TypeError('fetch failed', { cause: new Error('unexpected redirect') });
    });
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(1);
    expect(out.stdout.join('')).toBe('');
    expect(out.stderr.join('')).toContain(
      'error request_failed: the decision request was answered with a redirect',
    );
  });

  it('treats a real refused redirect as final', async () => {
    const requests: string[] = [];
    const server = createServer((request, response) => {
      const path = request.url ?? '';
      requests.push(path);
      // The dev bearer arrives exchanged, so the token call is answered and the decision
      // request is the one that is redirected.
      if (path === '/v1/ci/token') {
        response.writeHead(200, { 'content-type': 'application/json' });
        response.end(JSON.stringify(exchangeBody(mintedToken)));
        return;
      }
      response.writeHead(302, { location: 'https://elsewhere.test/decision' });
      response.end();
    });
    await new Promise<void>((resolve) => {
      server.listen(0, '127.0.0.1', resolve);
    });
    try {
      const address = server.address();
      const port = typeof address === 'object' && address !== null ? address.port : 0;
      const env = await devEnv(dirs, { EVIDGATE_API_URL: `http://127.0.0.1:${port}` });
      const out = capture();

      const { code } = await invoke(gateArgv(['--run', runId]), env, out, fetch);

      expect(code).toBe(3);
      // Node's own rejection shape is what the classification has to catch, so a real 302
      // proves the replay never happens.
      expect(requests).toStrictEqual(['/v1/ci/token', `/v1/runs/${runId}/decision`]);
      expect(out.stderr.join('')).toContain('redirect, which is refused');
    } finally {
      await new Promise<void>((resolve) => {
        server.close(() => {
          resolve();
        });
      });
    }
  });

  it('stops reading an oversized error body at the cap', async () => {
    const env = await devEnv(dirs);
    const chunk = new Uint8Array(8 * 1024).fill(0x78);
    let pulled = 0;
    let cancelled = 0;
    // 200 KiB of unparseable text in 8 KiB chunks, counted, so removing the cap shows up.
    const stream = (): ReadableStream<Uint8Array> => {
      let enqueued = 0;
      return new ReadableStream<Uint8Array>({
        pull(controller) {
          if (enqueued === 25) {
            controller.close();
            return;
          }
          enqueued += 1;
          pulled += 1;
          controller.enqueue(chunk);
        },
        cancel() {
          cancelled += 1;
        },
      });
    };
    const stub = devStubFetch(
      () =>
        new Response(stream(), {
          status: 500,
          headers: { 'content-type': 'application/json' },
        }),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(out.stderr.join('')).toContain('error request_failed: the run decision request failed');
    expect(stub.calls).toHaveLength(3);
    // Each attempt stops just past the 64 KiB cap (8 chunks, one overflow chunk, plus the one
    // the source buffers eagerly) and cancels the rest of the body.
    expect(cancelled).toBe(3);
    expect(pulled).toBeLessThanOrEqual(3 * 10);
  });

  it('exits 3 on 403 without retrying', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() => refusal(403, 'forbidden'));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(1);
    expect(out.stderr.join('')).toContain('error forbidden');
  });

  it('exits 3 on 404 without retrying', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() => refusal(404, 'not_found'));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(1);
    expect(out.stderr.join('')).toContain('error not_found');
  });

  it('exits 3 on a persistent 5xx after the bounded retries', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() => refusal(500, 'internal', { retryable: true }));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(3);
    expect(out.stderr.join('')).toContain('error internal');
  });

  it('exits 3 on a 5xx the envelope marks not retryable, without retrying', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() => refusal(503, 'internal'));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(1);
    expect(out.stderr.join('')).toContain('error internal');
  });

  it('never prints the bearer token, even when the server echoes it', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() =>
      refusal(422, 'request_failed', { message: 'credential dev-token-value was rejected' }),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(`${out.stdout.join('')}${out.stderr.join('')}`).not.toContain('dev-token-value');
  });
});
