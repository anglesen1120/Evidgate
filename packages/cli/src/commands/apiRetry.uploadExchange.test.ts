// The other half of the retry surface: `manifests upload` is replay-safe through the
// file digest it sends as `Idempotency-Key`, while the pre-tenant token exchange has no
// key at all (openapi: "Idempotency-Key exempt: `jti` single-use is the arbiter") and may
// therefore retry only the answers that provably precede the token — 429 and 503. Both
// resend the SAME body bytes, and a spent budget surfaces the server's own error.
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { exchangeToken } from '../auth/exchange.js';
import { resolveConfig } from '../config.js';
import { CliError } from '../errors.js';
import { main } from '../main.js';
import { resetSecrets, type Io } from '../redact.js';

/** The project slug both calls scope the request to. */
const project = 'proj-1';
/** A valid `manifest_id` (openapi `format: uuid`). */
const manifestId = '9b2f4c1e-2a3b-4c5d-8e9f-0a1b2c3d4e5f';
/** A valid `manifest_hash` (`sha256:` + 64 lowercase hex). */
const manifestHash = `sha256:${'a'.repeat(64)}`;
/** A valid `project_id` for the exchange (openapi `format: uuid`). */
const projectId = '3f2504e0-4f89-41d3-9a0c-0305e82c3301';
/** The manifest bytes the command uploads; the digest of these is the upload's key. */
const manifestText = '{\n  "schema": "manifest.v1",\n  "n": 12345678901234567890\n}\n';

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

/** A `fetch` stub that records every request (headers, raw body) and answers from `handler`. */
function stubFetch(handler: (request: Request) => Response | Promise<Response>): {
  readonly fetchImpl: typeof fetch;
  readonly calls: StubCall[];
} {
  const calls: StubCall[] = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    calls.push({ request, body: Buffer.from(await request.clone().arrayBuffer()) });
    return handler(request);
  };
  return { fetchImpl, calls };
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

/** The successful exchange body (openapi `CiTokenResponse`), handing out the minted token. */
function exchangedToken(): Response {
  return jsonResponse(200, {
    token: mintedToken,
    expires_at: '2030-01-01T00:00:00Z',
    project_id: projectId,
    permission_set: [],
  });
}

/** The dev token the M1 env seeds: an ingest credential, never a bearer. */
const devToken = 'dev-token-value';
/**
 * The `ci_producer` token the dev exchange mints. It is deliberately a different string from
 * `devToken`, so an upload that sent the dev token raw would fail the bearer assertions.
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
 * exchanges it at `POST /v1/ci/token` and caches the result, so the upload runs on the
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
        project_id: projectId,
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

describe('manifests upload retries', () => {
  const dirs: string[] = [];

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  async function tempDir(): Promise<string> {
    const dir = await mkdtemp(join(tmpdir(), 'evidgate-cli-'));
    dirs.push(dir);
    return dir;
  }

  async function manifestFile(): Promise<string> {
    const path = join(await tempDir(), 'manifest.json');
    await writeFile(path, manifestText, 'utf8');
    return path;
  }

  /**
   * The dev path: the token is a seeded ingest credential, so the upload
   * is preceded by one `POST /v1/ci/token`, and the exchange's answer is the bearer.
   */
  async function devEnv() {
    return {
      EVIDGATE_API_URL: 'http://127.0.0.1:8080',
      EVIDGATE_PROJECT: project,
      EVIDGATE_ENV: 'dev',
      EVIDGATE_DEV_TOKEN: devToken,
      EVIDGATE_CACHE_DIR: await tempDir(),
    };
  }

  it('a_503_is_retried_with_the_same_key_and_bytes', async () => {
    const file = await manifestFile();
    const env = await devEnv();
    let attempt = 0;
    const stub = devStubFetch(() => {
      attempt += 1;
      return attempt === 1
        ? refusal(503, 'unavailable', { 'retry-after': '1' })
        : jsonResponse(201, { manifest_id: manifestId, manifest_hash: manifestHash });
    });
    const fake = fakeDeps(stub.fetchImpl);
    const out = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, out.io, fake.deps),
    ).resolves.toBe(0);

    // The dev token is exchanged once, before the upload's own attempts.
    expect(stub.exchanges).toHaveLength(1);
    expect(stub.calls).toHaveLength(2);
    expect(fake.sleeps).toEqual([1_000]);
    const [first, second] = stub.calls;
    // The upload runs on the exchanged token: the raw dev token only travels in the exchange
    // body, so a regression that sent it as the bearer fails here.
    expect(stub.calls[0]?.request.headers.get('authorization')).toBe(`Bearer ${mintedToken}`);
    expect(stub.calls[1]?.request.headers.get('authorization')).toBe(`Bearer ${mintedToken}`);
    expect(JSON.parse(stub.exchanges[0]?.body.toString('utf8') ?? '')).toStrictEqual({
      ingest_token: devToken,
    });
    expect(second?.request.headers.get('idempotency-key')).toBe(
      first?.request.headers.get('idempotency-key'),
    );
    expect(second?.body.equals(first?.body ?? Buffer.alloc(0))).toBe(true);
    expect(second?.request.headers.get('content-type')).toBe('application/json');
    expect(out.stdout.join('')).toContain(`manifest_id=${manifestId}`);
    expect(out.stderr.join('')).toBe('');
  });

  it('a_422_is_not_retried', async () => {
    const file = await manifestFile();
    const env = await devEnv();
    const stub = devStubFetch(() => refusal(422, 'manifest_invalid'));
    const fake = fakeDeps(stub.fetchImpl);
    const out = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, out.io, fake.deps),
    ).resolves.toBe(3);

    expect(stub.calls).toHaveLength(1);
    expect(fake.sleeps).toEqual([]);
    expect(out.stderr.join('')).toContain('evidgate: error manifest_invalid: ');
  });

  it('a_400_is_not_retried', async () => {
    const file = await manifestFile();
    const env = await devEnv();
    const stub = devStubFetch(() => refusal(400, 'bad_request'));
    const fake = fakeDeps(stub.fetchImpl);
    const out = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, out.io, fake.deps),
    ).resolves.toBe(3);

    expect(stub.calls).toHaveLength(1);
    expect(fake.sleeps).toEqual([]);
    expect(out.stderr.join('')).toContain('evidgate: error bad_request: ');
  });

  it('a_429_waits_the_retry_after_then_succeeds', async () => {
    const file = await manifestFile();
    const env = await devEnv();
    let attempt = 0;
    const stub = devStubFetch(() => {
      attempt += 1;
      return attempt === 1
        ? refusal(429, 'rate_limited', { 'retry-after': '2' })
        : jsonResponse(201, { manifest_id: manifestId, manifest_hash: manifestHash });
    });
    const fake = fakeDeps(stub.fetchImpl);
    const out = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, out.io, fake.deps),
    ).resolves.toBe(0);

    expect(fake.sleeps).toEqual([2_000]);
    expect(stub.calls).toHaveLength(2);
  });

  it('an_exhausted_budget_exits_non_zero_with_the_server_code', async () => {
    const file = await manifestFile();
    const env = await devEnv();
    const stub = devStubFetch(() => refusal(503, 'unavailable'));
    const fake = fakeDeps(stub.fetchImpl);
    const out = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, out.io, fake.deps),
    ).resolves.toBe(3);

    expect(stub.calls).toHaveLength(3);
    expect(fake.sleeps).toHaveLength(2);
    expect(out.stderr.join('')).toContain('evidgate: error unavailable: ');
  });
});

describe('token exchange retries', () => {
  const dirs: string[] = [];

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  async function oidcConfig() {
    const dir = await mkdtemp(join(tmpdir(), 'evidgate-cli-'));
    dirs.push(dir);
    return resolveConfig({
      env: {
        EVIDGATE_API_URL: 'https://api.evidgate.test',
        EVIDGATE_PROJECT: project,
        EVIDGATE_CACHE_DIR: dir,
        EVIDGATE_OIDC_TOKEN: 'jwt-value',
      },
    });
  }

  it('a_503_is_retried_with_the_same_body_and_bearer', async () => {
    const config = await oidcConfig();
    let attempt = 0;
    const stub = stubFetch(() => {
      attempt += 1;
      return attempt === 1 ? refusal(503, 'unavailable', { 'retry-after': '1' }) : exchangedToken();
    });
    const fake = fakeDeps(stub.fetchImpl);

    const result = await exchangeToken(config, fake.deps);

    expect(result.token).toBe(mintedToken);
    expect(stub.calls).toHaveLength(2);
    expect(fake.sleeps).toEqual([1_000]);
    const [first, second] = stub.calls;
    expect(second?.body.toString('utf8')).toBe(first?.body.toString('utf8') ?? '');
    expect(second?.request.headers.get('authorization')).toBe(
      first?.request.headers.get('authorization'),
    );
  });

  it('a_500_after_a_possible_mint_is_not_retried', async () => {
    const config = await oidcConfig();
    const stub = stubFetch(() => refusal(500, 'server_error'));
    const fake = fakeDeps(stub.fetchImpl);

    await expect(exchangeToken(config, fake.deps)).rejects.toBeInstanceOf(CliError);

    expect(stub.calls).toHaveLength(1);
    expect(fake.sleeps).toEqual([]);
  });

  it('a_400_is_not_retried', async () => {
    const config = await oidcConfig();
    const stub = stubFetch(() => refusal(400, 'bad_request'));
    const fake = fakeDeps(stub.fetchImpl);

    let caught: unknown;
    try {
      await exchangeToken(config, fake.deps);
    } catch (error) {
      caught = error;
    }

    expect(caught).toBeInstanceOf(CliError);
    if (!(caught instanceof CliError)) {
      throw new Error('expected the exchange to raise a CliError');
    }
    expect(caught.code).toBe('bad_request');
    expect(stub.calls).toHaveLength(1);
    expect(fake.sleeps).toEqual([]);
  });

  it('a_429_waits_the_retry_after_then_succeeds', async () => {
    const config = await oidcConfig();
    let attempt = 0;
    const stub = stubFetch(() => {
      attempt += 1;
      return attempt === 1
        ? refusal(429, 'rate_limited', { 'retry-after': '2' })
        : exchangedToken();
    });
    const fake = fakeDeps(stub.fetchImpl);

    const result = await exchangeToken(config, fake.deps);

    expect(result.token).toBe(mintedToken);
    expect(fake.sleeps).toEqual([2_000]);
    expect(stub.calls).toHaveLength(2);
  });

  it('an_exhausted_retry_surfaces_the_server_error', async () => {
    const config = await oidcConfig();
    const stub = stubFetch(() => refusal(503, 'unavailable'));
    const fake = fakeDeps(stub.fetchImpl);

    let caught: unknown;
    try {
      await exchangeToken(config, fake.deps);
    } catch (error) {
      caught = error;
    }

    expect(caught).toBeInstanceOf(CliError);
    if (!(caught instanceof CliError)) {
      throw new Error('expected the exchange to raise a CliError');
    }
    expect(caught.code).toBe('unavailable');
    // One retry, the exchange's own budget: a credential is single-use.
    expect(stub.calls).toHaveLength(2);
    expect(fake.sleeps).toHaveLength(1);
  });
});
