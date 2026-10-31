// `evidgate gate` on the binary: the verdict-to-exit-code mapping, the one-request and
// `--wait` forms against a stub transport, the fail-closed readings of an unreadable or
// unknown body, the `--json` line, and the rule that no token ever reaches a writer.
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { ExitCode } from '../exitCodes.js';
import { main } from '../main.js';
import { resetSecrets, type Io } from '../redact.js';

import { exitCodeForDecision, type GateDecisionValue } from './gateDecision.js';

/** The run the gate asks about (openapi `RunId`, `format: uuid`). */
const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
/** The project slug the command acts on. */
const project = 'proj-1';
/** The dev bearer the `EVIDGATE_DEV_TOKEN` path returns (never on disk, never printed). */
const devToken = 'dev-token-value';

interface Captured {
  readonly io: Io;
  readonly stdout: string[];
  readonly stderr: string[];
}

function capture(): Captured {
  const stdout: string[] = [];
  const stderr: string[] = [];
  return {
    io: { stdout: (text) => stdout.push(text), stderr: (text) => stderr.push(text) },
    stdout,
    stderr,
  };
}

interface StubCall {
  readonly request: Request;
}

/** A `fetch` stub that records every request and answers from `handler`. */
function stubFetch(handler: (request: Request, index: number) => Response): {
  readonly fetchImpl: typeof fetch;
  readonly calls: StubCall[];
} {
  const calls: StubCall[] = [];
  const fetchImpl: typeof fetch = (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    calls.push({ request });
    return Promise.resolve(handler(request, calls.length - 1));
  };
  return { fetchImpl, calls };
}

/** A virtual clock: sleeps are recorded and advance the same `now` the command reads. */
interface Clock {
  readonly sleeps: number[];
  readonly now: () => number;
  readonly sleep: (milliseconds: number) => Promise<void>;
}

function fakeClock(): Clock {
  let time = 0;
  const sleeps: number[] = [];
  return {
    sleeps,
    now: () => time,
    sleep: (milliseconds: number) => {
      sleeps.push(milliseconds);
      time += milliseconds;
      return Promise.resolve();
    },
  };
}

interface Invocation {
  readonly code: number;
  readonly clock: Clock;
}

/** `main()` with the stub transport and the virtual clock; no real time, no jitter. */
async function invoke(
  argv: readonly string[],
  env: Readonly<Record<string, string | undefined>>,
  out: Captured,
  fetchImpl: typeof fetch,
  clock: Clock = fakeClock(),
): Promise<Invocation> {
  const code = await main(argv, env, out.io, {
    fetch: fetchImpl,
    sleep: clock.sleep,
    now: clock.now,
    random: () => 0,
  });
  return { code, clock };
}

function jsonResponse(status: number, body: unknown, headers: Record<string, string> = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json', ...headers },
  });
}

/** A 200 with the given decision body. */
function decided(body: unknown): Response {
  return jsonResponse(200, body);
}

/**
 * The 00 §9.2 error envelope. `retryable` is part of the guarded shape, so it is always
 * written: `false` (a permanent answer) unless a test asks for a transient one.
 */
function refusal(
  status: number,
  code: string,
  extra: { readonly message?: string; readonly retryable?: boolean } = {},
): Response {
  return jsonResponse(status, {
    error: {
      code,
      message: extra.message ?? 'the decision was refused',
      request_id: 'req-7',
      retryable: extra.retryable ?? false,
    },
  });
}

function gateArgv(flags: ReadonlyArray<string>): string[] {
  return ['gate', ...flags];
}

function sum(sleeps: readonly number[]): number {
  return sleeps.reduce((total, value) => total + value, 0);
}

describe('gate', () => {
  const dirs: string[] = [];

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  /** The M1 dev path: no exchange, no cache, no network beyond the stub. */
  async function devEnv(extra: Record<string, string> = {}) {
    const cacheDir = await mkdtemp(join(tmpdir(), 'evidgate-gate-'));
    dirs.push(cacheDir);
    return {
      EVIDGATE_API_URL: 'http://127.0.0.1:8080',
      EVIDGATE_PROJECT: project,
      EVIDGATE_ENV: 'dev',
      EVIDGATE_DEV_TOKEN: devToken,
      EVIDGATE_CACHE_DIR: cacheDir,
      ...extra,
    };
  }

  // The exit code of every verdict comes from one mapping function; only PASS is 0.
  describe('exit-code mapping', () => {
    const table: ReadonlyArray<readonly [GateDecisionValue | null, ExitCode]> = [
      ['PASS', ExitCode.Ok],
      ['FAIL', ExitCode.Fail],
      ['INCONCLUSIVE', ExitCode.Inconclusive],
      [null, ExitCode.Inconclusive],
    ];

    for (const [decision, expected] of table) {
      it(`maps ${String(decision)} to ${expected}`, () => {
        expect(exitCodeForDecision(decision)).toBe(expected);
      });
    }

    it('keeps the process contract 0/1/2/3', () => {
      expect(ExitCode).toStrictEqual({ Ok: 0, Fail: 1, Inconclusive: 2, Error: 3 });
    });
  });

  it('exits 0 only for PASS and prints the verdict with its reasons', async () => {
    const env = await devEnv();
    const stub = stubFetch(() =>
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
    expect(call?.request.headers.get('authorization')).toBe(`Bearer ${devToken}`);
    expect(new URL(call?.request.url ?? '').search).toBe('');
  });

  it('exits 1 for FAIL', async () => {
    const env = await devEnv();
    const stub = stubFetch(() => decided({ decision: 'FAIL', reasons: ['suite web failed'] }));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(1);
    expect(out.stdout.join('')).toBe(`gate: FAIL (run ${runId})\n  - suite web failed\n`);
    expect(stub.calls).toHaveLength(1);
  });

  it('caps a server reason to one escaped line', async () => {
    const env = await devEnv();
    const stub = stubFetch(() =>
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
    const env = await devEnv();
    // The token starts at code point 191, so a cap applied before redaction would print its
    // first ten characters; the redactor only matches the whole registered value.
    const stub = stubFetch(() =>
      decided({ decision: 'FAIL', reasons: [`${'x'.repeat(190)}${devToken}`] }),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(1);
    // `[REDACTED]` fits inside the 200-code-point cap, so the whole marker survives.
    expect(out.stdout.join('')).toBe(
      `gate: FAIL (run ${runId})\n  - ${'x'.repeat(190)}[REDACTED]\n`,
    );
    expect(out.stdout.join('')).not.toContain(devToken.slice(0, 4));
  });

  it('collapses reasons beyond the printed cap', async () => {
    const env = await devEnv();
    const stub = stubFetch(() =>
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

  it('redacts a token inside a printed json member without shortening it', async () => {
    const env = await devEnv();
    const stub = stubFetch(() =>
      decided({ decision: 'PASS', url: `${'x'.repeat(190)}${devToken}` }),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId, '--json']), env, out, stub.fetchImpl);

    expect(code).toBe(0);
    const line = out.stdout.join('').trimEnd();
    expect(line).toContain(`${'x'.repeat(190)}[REDACTED]`);
    expect(line).not.toContain(devToken);
    expect(JSON.parse(line)).toMatchObject({ url: `${'x'.repeat(190)}[REDACTED]` });
  });

  it('treats a 2xx whose body cannot be read as transient', async () => {
    const env = await devEnv();
    // A stream that fails mid-read: the client reports an error next to an `ok` response,
    // which must not be parsed as a decision body.
    const brokenBody = () =>
      new Response(
        new ReadableStream({
          start(controller) {
            controller.error(new Error('connection reset'));
          },
        }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      );
    const stub = stubFetch(brokenBody);
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(out.stdout.join('')).toBe('');
    expect(out.stderr[0]).toContain('error transport');
  });

  it('exits 2 for INCONCLUSIVE', async () => {
    const env = await devEnv();
    const stub = stubFetch(() => decided({ decision: 'INCONCLUSIVE' }));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(2);
    expect(out.stdout.join('')).toBe(`gate: INCONCLUSIVE (run ${runId})\n`);
    expect(stub.calls).toHaveLength(1);
  });

  it('exits 2 for a pending run after a single request when --wait is absent', async () => {
    const env = await devEnv();
    const stub = stubFetch(() => decided({ decision: null }));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(2);
    expect(stub.calls).toHaveLength(1);
    expect(out.stdout.join('')).toBe('');
    expect(out.stderr.join('')).toBe(`gate: run ${runId} has no decision yet (pending)\n`);
  });

  it('exits 2 when the run is still pending at the deadline, inside the wait', async () => {
    const env = await devEnv();
    const stub = stubFetch(() => decided({ decision: null }));
    const out = capture();

    const { code, clock } = await invoke(
      gateArgv(['--run', runId, '--wait', '10s']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(2);
    // A 1 s floor never spins: one request per slept second, and never past the deadline.
    expect(stub.calls).toHaveLength(10);
    expect(clock.sleeps).toHaveLength(10);
    expect(clock.sleeps.every((milliseconds) => milliseconds === 1_000)).toBe(true);
    expect(sum(clock.sleeps)).toBeLessThanOrEqual(10_000);
    expect(out.stdout.join('')).toBe('');
    expect(out.stderr.join('')).toBe(`gate: run ${runId} is still pending after 10s\n`);
  });

  it('polls to a PASS that arrives mid-wait and exits 0', async () => {
    const env = await devEnv();
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
    const env = await devEnv();
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

  it('exits 3 on a transport failure after the bounded retries', async () => {
    const env = await devEnv();
    const stub = stubFetch(() => {
      throw new TypeError('fetch failed');
    });
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    // `callApi` replays a GET up to three times before the failure surfaces.
    expect(stub.calls).toHaveLength(3);
    expect(out.stderr.join('')).toContain('error transport');
  });

  it('exits 3 on 401 without retrying', async () => {
    const env = await devEnv();
    const stub = stubFetch(() => refusal(401, 'token_invalid'));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(1);
    expect(out.stderr.join('')).toContain('error token_invalid');
  });

  it('exits 3 on 403 without retrying', async () => {
    const env = await devEnv();
    const stub = stubFetch(() => refusal(403, 'forbidden'));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(1);
    expect(out.stderr.join('')).toContain('error forbidden');
  });

  it('exits 3 on 404 without retrying', async () => {
    const env = await devEnv();
    const stub = stubFetch(() => refusal(404, 'not_found'));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(1);
    expect(out.stderr.join('')).toContain('error not_found');
  });

  it('exits 3 on a persistent 5xx after the bounded retries', async () => {
    const env = await devEnv();
    const stub = stubFetch(() => refusal(500, 'internal', { retryable: true }));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(3);
    expect(out.stderr.join('')).toContain('error internal');
  });

  it('exits 3 on a 5xx the envelope marks not retryable, without retrying', async () => {
    const env = await devEnv();
    const stub = stubFetch(() =>
      jsonResponse(503, {
        error: { code: 'internal', message: 'broken', request_id: 'req-9', retryable: false },
      }),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(1);
    expect(out.stderr.join('')).toContain('error internal');
  });

  it('honours Retry-After on 429 and keeps polling', async () => {
    const env = await devEnv();
    // A Retry-After the answer carries; the poll must not sleep less than that.
    const stub = stubFetch((_request, index) =>
      index < 3
        ? jsonResponse(
            429,
            {
              error: {
                code: 'rate_limited',
                message: 'slow down',
                request_id: 'req-8',
                retryable: true,
              },
            },
            { 'retry-after': '2' },
          )
        : decided({ decision: 'PASS' }),
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
    const env = await devEnv();
    const stub = stubFetch((_request, index) =>
      index < 3
        ? jsonResponse(
            429,
            {
              error: {
                code: 'rate_limited',
                message: 'slow down',
                request_id: 'req-9',
                retryable: true,
              },
            },
            { 'retry-after': '30' },
          )
        : decided({ decision: 'PASS' }),
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
    const env = await devEnv();
    const stub = stubFetch((_request, index) =>
      index < 3
        ? jsonResponse(
            429,
            {
              error: {
                code: 'rate_limited',
                message: 'slow down',
                request_id: 'req-10',
                retryable: true,
              },
            },
            { 'retry-after': '3600' },
          )
        : decided({ decision: 'PASS' }),
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
        const env = await devEnv();
        const stub = stubFetch(respond);
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

  describe('--json', () => {
    const keys = ['decision', 'completeness', 'evaluation_id', 'mode', 'url'];

    for (const decision of ['PASS', 'FAIL', 'INCONCLUSIVE'] as const) {
      it(`prints the five members in order for ${decision}`, async () => {
        const env = await devEnv();
        const stub = stubFetch(() => decided({ decision }));
        const out = capture();

        const { code } = await invoke(
          gateArgv(['--run', runId, '--json']),
          env,
          out,
          stub.fetchImpl,
        );

        expect(code).toBe(exitCodeForDecision(decision));
        const text = out.stdout.join('');
        expect(text.split('\n')).toHaveLength(2);
        const parsed = JSON.parse(text) as Record<string, unknown>;
        expect(Object.keys(parsed)).toStrictEqual(keys);
        expect(parsed).toStrictEqual({
          decision,
          completeness: null,
          evaluation_id: null,
          mode: null,
          url: null,
        });
      });
    }

    it('keeps the line parseable when a member looks like a header', async () => {
      const env = await devEnv();
      // `Authorization: …` is the writer's header rule; redacting it inside the member, before
      // encoding, must not eat the closing quotes or braces of the object.
      const stub = stubFetch(() =>
        decided({ decision: 'PASS', mode: 'Authorization: x', url: 'https://api.example/r' }),
      );
      const out = capture();

      const { code } = await invoke(gateArgv(['--run', runId, '--json']), env, out, stub.fetchImpl);

      expect(code).toBe(0);
      const text = out.stdout.join('');
      expect(text.split('\n')).toHaveLength(2);
      const parsed = JSON.parse(text) as Record<string, unknown>;
      expect(Object.keys(parsed)).toStrictEqual(keys);
      expect(parsed).toStrictEqual({
        decision: 'PASS',
        completeness: null,
        evaluation_id: null,
        mode: '[REDACTED]',
        url: 'https://api.example/r',
      });
    });

    it('prints decision null for a pending timeout', async () => {
      const env = await devEnv();
      const stub = stubFetch(() => decided({ decision: null }));
      const out = capture();

      const { code } = await invoke(
        gateArgv(['--run', runId, '--wait', '2s', '--json']),
        env,
        out,
        stub.fetchImpl,
      );

      expect(code).toBe(2);
      const parsed = JSON.parse(out.stdout.join('')) as Record<string, unknown>;
      expect(Object.keys(parsed)).toStrictEqual(keys);
      expect(parsed).toStrictEqual({
        decision: null,
        completeness: null,
        evaluation_id: null,
        mode: null,
        url: null,
      });
      expect(out.stderr.join('')).toContain('still pending');
    });

    it('prints the optional members only when the server sends them as strings', async () => {
      const env = await devEnv();
      const stub = stubFetch(() =>
        decided({
          decision: 'FAIL',
          completeness: 'complete',
          evaluation_id: 'eval-1',
          mode: 'strict',
          url: 'https://evidgate.test/runs/1',
          mode_unused: 5,
        }),
      );
      const out = capture();

      const { code } = await invoke(gateArgv(['--run', runId, '--json']), env, out, stub.fetchImpl);

      expect(code).toBe(1);
      const parsed = JSON.parse(out.stdout.join('')) as Record<string, unknown>;
      expect(parsed).toStrictEqual({
        decision: 'FAIL',
        completeness: 'complete',
        evaluation_id: 'eval-1',
        mode: 'strict',
        url: 'https://evidgate.test/runs/1',
      });
    });

    it('prints null for an optional member the server sends in another type', async () => {
      const env = await devEnv();
      const stub = stubFetch(() =>
        decided({ decision: 'PASS', completeness: 3, evaluation_id: {}, mode: null, url: [] }),
      );
      const out = capture();

      await invoke(gateArgv(['--run', runId, '--json']), env, out, stub.fetchImpl);

      const parsed = JSON.parse(out.stdout.join('')) as Record<string, unknown>;
      expect(parsed).toStrictEqual({
        decision: 'PASS',
        completeness: null,
        evaluation_id: null,
        mode: null,
        url: null,
      });
    });
  });

  it('never prints the bearer token, even when the server echoes it', async () => {
    const env = await devEnv();
    const stub = stubFetch(() =>
      refusal(422, 'request_failed', { message: `credential ${devToken} was rejected` }),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    expect(`${out.stdout.join('')}${out.stderr.join('')}`).not.toContain(devToken);
  });

  describe('flag validation before any request', () => {
    const badWaits = ['0', '0s', '-5s', '1.5m', '1d', 'abc', '', '25h', '86401s'];

    for (const wait of badWaits) {
      it(`refuses --wait ${JSON.stringify(wait)}`, async () => {
        const env = await devEnv();
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
      const env = await devEnv();
      const stub = stubFetch(() => decided({ decision: 'PASS' }));
      const out = capture();

      const { code } = await invoke(
        gateArgv(['--run', runId, '--wait', '24h']),
        env,
        out,
        stub.fetchImpl,
      );

      expect(code).toBe(0);
    });

    it('refuses a missing --run', async () => {
      const env = await devEnv();
      const stub = stubFetch(() => decided({ decision: 'PASS' }));
      const out = capture();

      const { code } = await invoke(gateArgv([]), env, out, stub.fetchImpl);

      expect(code).toBe(3);
      expect(stub.calls).toHaveLength(0);
      expect(out.stderr.join('')).toContain('missing required flag --run');
    });

    it('refuses a run id that is not a UUID', async () => {
      const env = await devEnv();
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
      const env = await devEnv();
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
});
