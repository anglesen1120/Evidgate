// Every retrying CLI command must make the retry visible exactly where it is allowed:
// `runs create` and `runs finalize` carry an `Idempotency-Key`, so a 503 is retried with
// the SAME key and the SAME body bytes and a 429 waits the server's `Retry-After`; a 4xx
// other than 429 is final, and an exhausted budget still exits non-zero with the server's
// own `error.code`. The seams are injected, so no spec waits in real time.
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { main } from '../main.js';
import { resetSecrets, type Io } from '../redact.js';
import { defaultSpoolMaxBytes, ProducerSpool } from '../spool.js';

/** The project slug both commands scope their request to. */
const project = 'proj-1';
/** A valid `manifest_id` (openapi `format: uuid`). */
const manifestId = '9b2f4c1e-2a3b-4c5d-8e9f-0a1b2c3d4e5f';
/** A valid `manifest_hash` (`sha256:` + 64 lowercase hex). */
const manifestHash = `sha256:${'a'.repeat(64)}`;
/** The `run_id` the server returns (openapi `RunCreated`). */
const createdRunId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
/** The run a finalize slot belongs to. */
const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
/** The manifest's deterministic producer id for shard 1. */
const producerId = 'shard-1';

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
  readonly body: Buffer;
}

function jsonResponse(
  status: number,
  body: unknown,
  headers: Record<string, string> = {},
): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json', ...headers },
  });
}

/** The 00 §9.2 envelope a refused request answers with; `retryable` follows `status`. */
function refusal(status: number, code: string, headers: Record<string, string> = {}): Response {
  return jsonResponse(
    status,
    {
      error: {
        code,
        message: 'refused',
        request_id: 'req-1',
        // A burst 429 and a transient 5xx are retryable; a quota 429 and every other
        // 4xx say so and are final.
        retryable: status === 429 || status >= 500,
      },
    },
    headers,
  );
}

/** The recorded raw body of `call`, as text. */
function bodyText(call: StubCall | undefined): string {
  return call?.body.toString('utf8') ?? '';
}

/** The dev token both dev envs seed: an ingest credential, never a bearer. */
const devToken = 'dev-token-value';
/**
 * The `ci_producer` token the dev exchange mints. It is deliberately a different string from
 * `devToken`, so a command that sent the dev token raw would fail the bearer assertions.
 */
const mintedToken = 'egt_minted_ci_token';
/** The exchange route a dev run posts to before its own request. */
const exchangePath = '/v1/ci/token';

interface DevStub {
  readonly fetchImpl: typeof fetch;
  /** The command's own requests; the dev token's exchange is never one of them. */
  readonly calls: StubCall[];
  /** The `POST /v1/ci/token` requests the run made, in order. */
  readonly exchanges: StubCall[];
}

/**
 * A stub for a dev-token run. `EVIDGATE_DEV_TOKEN` is an ingest credential: `getToken`
 * exchanges it at `POST /v1/ci/token` and caches the result, so the command runs on the
 * returned `ci_producer` token. The stub answers that exchange with `mintedToken` and hands
 * every other request to `handler`.
 */
function devStubFetch(handler: (request: Request) => Response | Promise<Response>): DevStub {
  const calls: StubCall[] = [];
  const exchanges: StubCall[] = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const call: StubCall = { request, body: Buffer.from(await request.clone().arrayBuffer()) };
    if (new URL(request.url).pathname === exchangePath) {
      exchanges.push(call);
      return jsonResponse(200, {
        token: mintedToken,
        expires_at: '2030-01-01T00:00:00Z',
        project_id: '3f2504e0-4f89-41d3-9a0c-0305e82c3301',
        permission_set: [],
      });
    }
    calls.push(call);
    return handler(request);
  };
  return { fetchImpl, calls, exchanges };
}

/** The wait seams: a sleep that records its argument and never blocks. */
function fakeDeps(fetchImpl: typeof fetch): {
  readonly deps: {
    fetch: typeof fetch;
    sleep: (milliseconds: number) => Promise<void>;
    random: () => number;
    now: () => number;
  };
  readonly sleeps: number[];
} {
  const sleeps: number[] = [];
  return {
    sleeps,
    deps: {
      fetch: fetchImpl,
      sleep: (milliseconds: number) => {
        sleeps.push(milliseconds);
        return Promise.resolve();
      },
      random: () => 0,
      // A frozen clock keeps the whole-call budget out of the way of the attempt count.
      now: () => 1_700_000_000_000,
    },
  };
}

describe('runs create retries', () => {
  const dirs: string[] = [];

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  async function devEnv(extra: Record<string, string> = {}) {
    const dir = await mkdtemp(join(tmpdir(), 'evidgate-cli-'));
    dirs.push(dir);
    return {
      EVIDGATE_API_URL: 'http://127.0.0.1:8080',
      EVIDGATE_PROJECT: project,
      EVIDGATE_ENV: 'dev',
      EVIDGATE_DEV_TOKEN: devToken,
      EVIDGATE_CACHE_DIR: dir,
      ...extra,
    };
  }

  const argv = [
    'runs',
    'create',
    '--manifest-id',
    manifestId,
    '--manifest-hash',
    manifestHash,
    '--idempotency-key',
    'retry-key-1',
  ];

  it('a_503_is_retried_with_the_same_key_and_body', async () => {
    const env = await devEnv();
    let attempt = 0;
    const stub = devStubFetch(() => {
      attempt += 1;
      return attempt === 1
        ? refusal(503, 'unavailable', { 'retry-after': '1' })
        : jsonResponse(201, { run_id: createdRunId, state: 'queued', unverified_ref: false });
    });
    const fake = fakeDeps(stub.fetchImpl);
    const out = capture();

    await expect(main(argv, env, out.io, fake.deps)).resolves.toBe(0);

    // The dev token is exchanged once, before the command's own attempts.
    expect(stub.exchanges).toHaveLength(1);
    expect(stub.calls).toHaveLength(2);
    expect(fake.sleeps).toEqual([1_000]);
    const [first, second] = stub.calls;
    // The command runs on the exchanged token: the raw dev token only ever travels in the
    // exchange body, so a regression that sent it as the bearer fails here.
    expect(stub.calls[0]?.request.headers.get('authorization')).toBe(`Bearer ${mintedToken}`);
    expect(JSON.parse(bodyText(stub.exchanges[0]))).toStrictEqual({ ingest_token: devToken });
    expect(second?.request.headers.get('idempotency-key')).toBe('retry-key-1');
    expect(second?.request.headers.get('idempotency-key')).toBe(
      first?.request.headers.get('idempotency-key'),
    );
    expect(bodyText(second)).toBe(bodyText(first));
    expect(out.stdout.join('')).toContain(`run_id=${createdRunId}`);
    expect(out.stderr.join('')).toBe('');
  });

  it('a_400_is_not_retried', async () => {
    const env = await devEnv();
    const stub = devStubFetch(() => refusal(400, 'bad_request'));
    const fake = fakeDeps(stub.fetchImpl);
    const out = capture();

    await expect(main(argv, env, out.io, fake.deps)).resolves.toBe(3);

    expect(stub.calls).toHaveLength(1);
    expect(fake.sleeps).toEqual([]);
    expect(out.stderr.join('')).toContain('evidgate: error bad_request: ');
  });

  it('a_408_is_not_retried', async () => {
    const env = await devEnv();
    const stub = devStubFetch(() => refusal(408, 'request_timeout'));
    const fake = fakeDeps(stub.fetchImpl);
    const out = capture();

    await expect(main(argv, env, out.io, fake.deps)).resolves.toBe(3);

    expect(stub.calls).toHaveLength(1);
    expect(fake.sleeps).toEqual([]);
    expect(out.stderr.join('')).toContain('evidgate: error request_timeout: ');
  });

  it('a_429_waits_the_retry_after_then_succeeds', async () => {
    const env = await devEnv();
    let attempt = 0;
    const stub = devStubFetch(() => {
      attempt += 1;
      return attempt === 1
        ? refusal(429, 'rate_limited', { 'retry-after': '2' })
        : jsonResponse(201, { run_id: createdRunId, state: 'queued', unverified_ref: false });
    });
    const fake = fakeDeps(stub.fetchImpl);
    const out = capture();

    await expect(main(argv, env, out.io, fake.deps)).resolves.toBe(0);

    expect(fake.sleeps).toEqual([2_000]);
    expect(stub.calls).toHaveLength(2);
  });

  it('an_exhausted_budget_exits_non_zero_with_the_server_code', async () => {
    const env = await devEnv();
    const stub = devStubFetch(() => refusal(503, 'unavailable'));
    const fake = fakeDeps(stub.fetchImpl);
    const out = capture();

    await expect(main(argv, env, out.io, fake.deps)).resolves.toBe(3);

    expect(stub.calls).toHaveLength(3);
    expect(fake.sleeps).toHaveLength(2);
    expect(out.stderr.join('')).toContain('evidgate: error unavailable: ');
  });
});

describe('runs finalize retries', () => {
  const dirs: string[] = [];

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  /** A producer spool whose ack cursor covers `acked` envelopes (all of them acked). */
  async function writeSpool(acked: number): Promise<string> {
    const spoolDir = await mkdtemp(join(tmpdir(), 'evidgate-cli-'));
    dirs.push(spoolDir);
    const spool = await ProducerSpool.open({
      spoolDir,
      runId,
      producerId,
      maxBytes: defaultSpoolMaxBytes,
      createIfMissing: true,
    });
    const lines = Array.from({ length: acked }, (_, index) =>
      JSON.stringify({ sequence: index + 1, event_id: `evt-${index + 1}`, type: 'test.finished' }),
    );
    await spool.append(lines);
    await spool.ack(acked, acked);
    return spoolDir;
  }

  async function devEnv(spoolDir: string) {
    const dir = await mkdtemp(join(tmpdir(), 'evidgate-cli-'));
    dirs.push(dir);
    return {
      EVIDGATE_API_URL: 'http://127.0.0.1:8080',
      EVIDGATE_PROJECT: project,
      EVIDGATE_ENV: 'dev',
      EVIDGATE_DEV_TOKEN: devToken,
      EVIDGATE_CACHE_DIR: dir,
      EVIDGATE_SPOOL_DIR: spoolDir,
    };
  }

  const argv = [
    'runs',
    'finalize',
    '--run',
    runId,
    '--producer',
    producerId,
    '--shard',
    '1',
    '--manifest-hash',
    manifestHash,
  ];

  it('a_503_is_retried_with_the_same_key_and_body', async () => {
    const env = await devEnv(await writeSpool(2));
    let attempt = 0;
    const stub = devStubFetch(() => {
      attempt += 1;
      return attempt === 1
        ? refusal(503, 'unavailable', { 'retry-after': '1' })
        : new Response(null, { status: 202 });
    });
    const fake = fakeDeps(stub.fetchImpl);
    const out = capture();

    await expect(main(argv, env, out.io, fake.deps)).resolves.toBe(0);

    expect(stub.calls).toHaveLength(2);
    expect(fake.sleeps).toEqual([1_000]);
    const [first, second] = stub.calls;
    expect(second?.request.headers.get('authorization')).toBe(`Bearer ${mintedToken}`);
    expect(second?.request.headers.get('idempotency-key')).toBe(
      first?.request.headers.get('idempotency-key'),
    );
    expect(bodyText(second)).toBe(bodyText(first));
    expect(out.stdout.join('')).toContain('reconcile accepted');
  });

  it('a_403_is_not_retried_and_keeps_the_spool', async () => {
    const spoolDir = await writeSpool(2);
    const env = await devEnv(spoolDir);
    const stub = devStubFetch(() => refusal(403, 'producer_slot_taken'));
    const fake = fakeDeps(stub.fetchImpl);
    const out = capture();

    await expect(main(argv, env, out.io, fake.deps)).resolves.toBe(3);

    expect(stub.calls).toHaveLength(1);
    expect(fake.sleeps).toEqual([]);
    expect(out.stderr.join('')).toContain('evidgate: error producer_slot_taken: ');
  });

  it('a_400_is_not_retried', async () => {
    const spoolDir = await writeSpool(1);
    const env = await devEnv(spoolDir);
    const stub = devStubFetch(() => refusal(400, 'bad_request'));
    const fake = fakeDeps(stub.fetchImpl);
    const out = capture();

    await expect(main(argv, env, out.io, fake.deps)).resolves.toBe(3);

    expect(stub.calls).toHaveLength(1);
    expect(fake.sleeps).toEqual([]);
    expect(out.stderr.join('')).toContain('evidgate: error bad_request: ');
  });

  it('a_429_waits_the_retry_after_then_succeeds', async () => {
    const env = await devEnv(await writeSpool(1));
    let attempt = 0;
    const stub = devStubFetch(() => {
      attempt += 1;
      return attempt === 1
        ? refusal(429, 'rate_limited', { 'retry-after': '2' })
        : new Response(null, { status: 202 });
    });
    const fake = fakeDeps(stub.fetchImpl);
    const out = capture();

    await expect(main(argv, env, out.io, fake.deps)).resolves.toBe(0);

    expect(fake.sleeps).toEqual([2_000]);
    expect(stub.calls).toHaveLength(2);
  });

  it('an_exhausted_budget_exits_non_zero_with_the_server_code', async () => {
    const env = await devEnv(await writeSpool(1));
    const stub = devStubFetch(() => refusal(503, 'unavailable'));
    const fake = fakeDeps(stub.fetchImpl);
    const out = capture();

    await expect(main(argv, env, out.io, fake.deps)).resolves.toBe(3);

    expect(stub.calls).toHaveLength(3);
    expect(fake.sleeps).toHaveLength(2);
    expect(out.stderr.join('')).toContain('evidgate: error unavailable: ');
  });
});
