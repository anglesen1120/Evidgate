// 04.2.2-T2.1: `evidgate runs create` on the binary — the `Idempotency-Key` header, the
// derived key (AC4), the replay/conflict pair (AC3), the flag → body mapping and the exact
// `--json` line (AC5, amended 2026-10-14 D-S47-Q1), plus every failure path: the flag
// validation that runs before any token work, the 401-only cache purge, the 403 that keeps
// the token, the transport error, the un-echoed server body and `--verbose` redaction
// (04.2.2 §2, §5, §8, §9, §10; contracts/cli.md §5/§6/§7/§9).
import { createHash } from 'node:crypto';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { readCachedToken, writeCachedToken } from '../auth/tokenCache.js';
import { resolveConfig } from '../config.js';
import { main } from '../main.js';
import { resetSecrets, type Io } from '../redact.js';

import { deriveRunIdempotencyKey } from './runsCreate.js';

/** The project slug the command scopes the create to. */
const project = 'proj-1';
/** A valid `manifest_id` (openapi `format: uuid`). */
const manifestId = '9b2f4c1e-2a3b-4c5d-8e9f-0a1b2c3d4e5f';
/** A second UUID, to prove the derived key moves with `manifest_id` (AC4). */
const otherManifestId = '1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f';
/** A valid `manifest_hash` (`sha256:` + 64 hex); assembled so no digest literal is stored. */
const manifestHash = `sha256:${'a'.repeat(64)}`;
/** A second digest for the AC4 difference checks. */
const otherManifestHash = `sha256:${'b'.repeat(64)}`;
/** A digest one character short of the `sha256:<64 hex>` form. */
const shortHash = `sha256:${'a'.repeat(63)}`;
/** An uppercase digest, refused because the server's form is lowercase. */
const upperHash = `sha256:${'A'.repeat(64)}`;
/** The `run_id` the server returns (openapi `RunCreated`). */
const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
/** The namespace of the documented derived-key pre-image (AC4). */
const keyVersion = 'evidgate.run-key.v1';

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

/** A `fetch` stub that records every request (URL, headers, raw body) and answers from `handler`. */
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

/** `main()` with the injected `fetch` stub — the only transport the specs use. */
function invoke(
  argv: ReadonlyArray<string>,
  env: Readonly<Record<string, string | undefined>>,
  out: Captured,
  fetchImpl: typeof fetch,
): Promise<number> {
  return main(argv, env, out.io, { fetch: fetchImpl });
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

/** The canonical 201 body (openapi `RunCreated`), with per-case overrides. */
function created(overrides: Record<string, unknown> = {}): Response {
  return jsonResponse(201, { run_id: runId, state: 'queued', unverified_ref: false, ...overrides });
}

/** The `project_id` the exchange echoes back (openapi `format: uuid`). */
const exchangeProjectId = '5a0b1c2d-3e4f-4a5b-8c6d-7e8f9a0b1c2d';
/** The `EVIDGATE_DEV_TOKEN` value: an ingest credential the exchange presents, never a bearer. */
const devToken = 'dev-token-value';
/**
 * The `ci_producer` token the dev exchange mints: the bearer the command sends. It is
 * deliberately a different string from `devToken`, so a command that sent the dev token raw
 * would fail the `Bearer ${mintedToken}` assertion.
 */
const mintedToken = 'egt_minted_ci_token';

/**
 * The `/v1/ci/token` answer for the dev source: the dev token is exchanged like an ingest
 * credential, so the bearer the command sends is the token this body hands out. The answer is
 * `mintedToken`, not the dev token's value, and it expires 30 minutes out, inside the cache's
 * 60 + 5 minute freshness window.
 */
function devTokenExchange(request: Request): Response | undefined {
  return new URL(request.url).pathname === '/v1/ci/token'
    ? jsonResponse(200, {
        token: mintedToken,
        expires_at: new Date(Date.now() + 30 * 60_000).toISOString(),
        project_id: exchangeProjectId,
        permission_set: [],
      })
    : undefined;
}

/** A stub whose handler answers the command's own request, never the token exchange. */
function stubDevFetch(handler: (request: Request) => Response | Promise<Response>): {
  readonly fetchImpl: typeof fetch;
  readonly calls: StubCall[];
} {
  return stubFetch((request) => devTokenExchange(request) ?? handler(request));
}

/**
 * The command's own requests: everything but the `/v1/ci/token` exchange, so a count or an
 * index means the same thing it did before the dev source gained a token exchange.
 */
function apiCalls(stub: { readonly calls: StubCall[] }): StubCall[] {
  return stub.calls.filter((call) => new URL(call.request.url).pathname !== '/v1/ci/token');
}

/** The JSON body the stub recorded for `call`. */
function sentBody(call: StubCall | undefined): Record<string, unknown> {
  return JSON.parse(call?.body.toString('utf8') ?? '') as Record<string, unknown>;
}

/** A bare `runs create` argv, for the cases that replace a required flag. */
function runsCreateArgv(flags: ReadonlyArray<string>): string[] {
  return ['runs', 'create', ...flags];
}

/** `runs create` with both required flags, plus `extra` (the common case). */
function createArgv(extra: ReadonlyArray<string> = []): string[] {
  return runsCreateArgv(['--manifest-id', manifestId, '--manifest-hash', manifestHash, ...extra]);
}

describe('runs create', () => {
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

  /**
   * The dev path: the dev token is exchanged at `/v1/ci/token` like an ingest credential,
   * the returned producer token is the bearer, and it is cached (the Api accepts only
   * exchanged tokens).
   */
  async function devEnv(extra: Record<string, string> = {}) {
    const cacheDir = await tempDir();
    return {
      EVIDGATE_API_URL: 'http://127.0.0.1:8080',
      EVIDGATE_PROJECT: project,
      EVIDGATE_ENV: 'dev',
      EVIDGATE_DEV_TOKEN: devToken,
      EVIDGATE_CACHE_DIR: cacheDir,
      ...extra,
    };
  }

  /** An ingest source: `getToken()` would exchange at `/v1/ci/token` when the cache is cold. */
  async function ingestEnv(extra: Record<string, string> = {}) {
    const cacheDir = await tempDir();
    return {
      EVIDGATE_API_URL: 'https://api.evidgate.test',
      EVIDGATE_PROJECT: project,
      EVIDGATE_CACHE_DIR: cacheDir,
      EVIDGATE_INGEST_TOKEN: 'egt_ingest_secret_value',
      ...extra,
    };
  }

  // 04.2.2-T2.1 / AC2: the `Idempotency-Key` header is sent, the body carries only
  // `manifest_id`, and `run_id`/`manifest_hash`/`state` are printed.
  it('prints_run_id', async () => {
    const env = await devEnv();
    const stub = stubDevFetch(() => created({ unverified_ref: true }));
    const out = capture();

    const argv = createArgv(['--idempotency-key', 'explicit-key-1']);
    await expect(invoke(argv, env, out, stub.fetchImpl)).resolves.toBe(0);

    expect(out.stdout.join('')).toBe(
      `run_id=${runId} manifest_hash=${manifestHash} state=queued\n`,
    );
    expect(out.stderr.join('')).toBe('');
    const calls = apiCalls(stub);
    expect(calls).toHaveLength(1);
    const call = calls[0];
    expect(call?.request.method).toBe('POST');
    // `createApiClient` puts the SDK's `/v1` server prefix on the base URL, so the
    // operation path is `<root>/v1/projects/{p}/runs` (the env value carries no `/v1`).
    expect(new URL(call?.request.url ?? '').pathname).toBe(`/v1/projects/${project}/runs`);
    // The command runs on the exchanged token: the dev token only travels in the exchange
    // body, so a regression that sent it as the bearer fails here.
    expect(call?.request.headers.get('authorization')).toBe(`Bearer ${mintedToken}`);
    expect(call?.request.headers.get('content-type')).toBe('application/json');
    // 00 §9.1: the route requires an `Idempotency-Key`; an explicit one is sent verbatim.
    expect(call?.request.headers.get('idempotency-key')).toBe('explicit-key-1');
    // `CreateRunRequest` has `additionalProperties: false`: only the members the CLI owns.
    expect(sentBody(call)).toEqual({ manifest_id: manifestId });
  });

  // 04.2.2-T2.1 / AC3: two invocations with the same key and the same payload send the
  // identical request, the stub replays the stored 201, and both print that same run.
  it('idempotent_replay', async () => {
    const env = await devEnv();
    const stub = stubDevFetch(() => created());
    const argv = createArgv(['--idempotency-key', 'replay-key-1']);
    const first = capture();
    const second = capture();

    await expect(invoke(argv, env, first, stub.fetchImpl)).resolves.toBe(0);
    await expect(invoke(argv, env, second, stub.fetchImpl)).resolves.toBe(0);

    const line = `run_id=${runId} manifest_hash=${manifestHash} state=queued\n`;
    expect(first.stdout.join('')).toBe(line);
    expect(first.stdout.join('')).toBe(second.stdout.join(''));
    expect(first.stderr.join('')).toBe('');
    // One exchange (the second invocation reuses the cached token), then two creates.
    const calls = apiCalls(stub);
    expect(calls).toHaveLength(2);
    expect(calls[1]?.request.headers.get('idempotency-key')).toBe('replay-key-1');
    // The second request is byte-identical, so the server replays instead of conflicting.
    expect(sentBody(calls[1])).toEqual(sentBody(calls[0]));
    expect(calls[1]?.body.toString('utf8')).toBe(calls[0]?.body.toString('utf8') ?? '');
  });

  // 04.2.2-T2.1 / AC3 + T11: the same key with a different body is 409
  // `idempotency_conflict`, which exits 3 echoing the server's code — and the cached
  // token survives, because a conflict says nothing about the token (contracts/cli.md §7).
  it('idempotency_conflict_exit3', async () => {
    const env = await ingestEnv();
    const config = resolveConfig({ env });
    const expiresAt = new Date(Date.now() + 30 * 60_000).toISOString();
    await writeCachedToken(config, { token: 'egt_cached_token', expiresAt });

    const stub = stubFetch(() =>
      jsonResponse(409, {
        error: {
          code: 'idempotency_conflict',
          message: 'the key was used with a different body',
          request_id: 'req-9',
          retryable: false,
        },
      }),
    );
    const out = capture();

    const argv = createArgv(['--idempotency-key', 'conflict-key-1']);
    await expect(invoke(argv, env, out, stub.fetchImpl)).resolves.toBe(3);

    const text = out.stderr.join('');
    expect(text).toContain('evidgate: error idempotency_conflict: ');
    expect(text).toContain('evidgate: request_id: req-9');
    expect(out.stdout.join('')).toBe('');
    // The cached token was reused (no exchange), so the create is the only call.
    expect(stub.calls).toHaveLength(1);
    await expect(readCachedToken(config)).resolves.toEqual({
      token: 'egt_cached_token',
      expiresAt,
    });
  });

  // 04.2.2-T2.1 / AC4 (amended 2026-10-14, D-S47-Q1): the derived key is the SHA-256 hex
  // of the documented JSON array. Same inputs → same key, across two separate
  // invocations; each request field moves the key; an absent optional member is JSON null.
  it('derived_key_stable', async () => {
    const key = deriveRunIdempotencyKey({ manifestId, manifestHash });
    expect(key).toMatch(/^[0-9a-f]{64}$/);
    expect(key.length).toBeLessThanOrEqual(128);
    // Deterministic: the same inputs derive the same key on a second call.
    expect(deriveRunIdempotencyKey({ manifestId, manifestHash })).toBe(key);
    // An absent optional member occupies the same slot as an explicit `null`.
    const absent = deriveRunIdempotencyKey({
      manifestId,
      manifestHash,
      release: null,
      ciRef: null,
    });
    expect(absent).toBe(key);
    // Golden vector, recomputed here from the documented pre-image (never a literal).
    const golden = [keyVersion, manifestHash, manifestId, null, null];
    expect(key).toBe(createHash('sha256').update(JSON.stringify(golden), 'utf8').digest('hex'));

    const withOptionals = deriveRunIdempotencyKey({
      manifestId,
      manifestHash,
      release: 'rel-1',
      ciRef: 'refs/heads/main',
    });
    const goldenWithOptionals = [keyVersion, manifestHash, manifestId, 'rel-1', 'refs/heads/main'];
    expect(withOptionals).toBe(
      createHash('sha256').update(JSON.stringify(goldenWithOptionals), 'utf8').digest('hex'),
    );

    // Every request value feeds the key: a different manifest_id, manifest_hash, release
    // or ci-ref never reuses it, and the variants are distinct from each other too.
    const variants = [
      deriveRunIdempotencyKey({ manifestId: otherManifestId, manifestHash }),
      deriveRunIdempotencyKey({ manifestId, manifestHash: otherManifestHash }),
      deriveRunIdempotencyKey({ manifestId, manifestHash, release: 'rel-1' }),
      deriveRunIdempotencyKey({ manifestId, manifestHash, ciRef: 'refs/heads/main' }),
    ];
    for (const variant of variants) {
      expect(variant).not.toBe(key);
      expect(variant).toMatch(/^[0-9a-f]{64}$/);
    }
    expect(new Set(variants).size).toBe(variants.length);
    expect(withOptionals).not.toBe(key);

    // The same derivation reaches the wire: one key per body, and the identical key in a
    // second, separate invocation (no `--idempotency-key` is passed at all).
    const env = await devEnv();
    const stub = stubDevFetch(() => created());
    await expect(invoke(createArgv(), env, capture(), stub.fetchImpl)).resolves.toBe(0);
    await expect(invoke(createArgv(), env, capture(), stub.fetchImpl)).resolves.toBe(0);
    const flagged = createArgv(['--release', 'rel-1', '--ci-ref', 'refs/heads/main']);
    await expect(invoke(flagged, env, capture(), stub.fetchImpl)).resolves.toBe(0);
    const calls = apiCalls(stub);
    expect(calls).toHaveLength(3);
    expect(calls[0]?.request.headers.get('idempotency-key')).toBe(key);
    expect(calls[1]?.request.headers.get('idempotency-key')).toBe(key);
    expect(calls[2]?.request.headers.get('idempotency-key')).toBe(withOptionals);
  });

  // 04.2.2-T2.1 / AC5: `--release` and every `--ci-ref` form map to the documented body
  // members, and `--json` prints exactly one line with `run_id`, `manifest_hash`, `state`.
  it('flags_and_json_output', async () => {
    const env = await devEnv();
    const cases: ReadonlyArray<{ readonly ciRef: string; readonly ref: unknown }> = [
      { ciRef: 'refs/heads/main', ref: { kind: 'branch', name: 'main' } },
      { ciRef: 'refs/heads/release/1.2', ref: { kind: 'branch', name: 'release/1.2' } },
      { ciRef: 'refs/tags/v1.2.3', ref: { kind: 'tag', name: 'v1.2.3' } },
      {
        ciRef: 'refs/pull/42/merge',
        ref: { kind: 'pull_request', name: '42/merge', pr_number: 42 },
      },
    ];

    for (const testCase of cases) {
      const stub = stubDevFetch(() => created({ unverified_ref: true }));
      const out = capture();
      const flags = ['--release', 'rel-1', '--ci-ref', testCase.ciRef, '--idempotency-key', 'k'];
      await expect(invoke(createArgv(flags), env, out, stub.fetchImpl)).resolves.toBe(0);
      expect(out.stderr.join('')).toBe('');
      // Only the members the CLI owns: no head_sha/base_sha/trigger_event, and no policy,
      // suite or environment (the server pins those, D-S47-Q1).
      expect(sentBody(apiCalls(stub)[0])).toEqual({
        manifest_id: manifestId,
        release: 'rel-1',
        ref: testCase.ref,
      });
    }

    // `--release` is trimmed before it is sent (and before it feeds the key).
    const trimStub = stubDevFetch(() => created());
    const trimArgv = createArgv(['--release', '  rel-1  ', '--idempotency-key', 'k']);
    await expect(invoke(trimArgv, env, capture(), trimStub.fetchImpl)).resolves.toBe(0);
    expect(sentBody(apiCalls(trimStub)[0])).toEqual({ manifest_id: manifestId, release: 'rel-1' });

    // `--json`: one line, three members in the documented order, and nothing else.
    const jsonStub = stubDevFetch(() => created());
    const jsonOut = capture();
    await expect(invoke(createArgv(['--json']), env, jsonOut, jsonStub.fetchImpl)).resolves.toBe(0);
    const expected = `{"run_id":"${runId}","manifest_hash":"${manifestHash}","state":"queued"}`;
    expect(jsonOut.stdout.join('')).toBe(`${expected}\n`);
    expect(jsonOut.stderr.join('')).toBe('');
    expect(jsonOut.stdout.join('')).not.toContain('unverified_ref');
  });

  // 04.2.2-T2.1 / §8: every flag failure is a `usage` error raised before any token work,
  // so an ingest source (which would exchange over the network) makes no call at all.
  it('usage_errors_no_request', async () => {
    const cases: ReadonlyArray<{ readonly label: string; readonly argv: string[] }> = [
      {
        label: '--manifest-id absent',
        argv: runsCreateArgv(['--manifest-hash', manifestHash]),
      },
      {
        label: '--manifest-id not a UUID',
        argv: runsCreateArgv(['--manifest-id', 'nope', '--manifest-hash', manifestHash]),
      },
      {
        label: '--manifest-hash absent',
        argv: runsCreateArgv(['--manifest-id', manifestId]),
      },
      {
        label: '--manifest-hash without the prefix',
        argv: runsCreateArgv(['--manifest-id', manifestId, '--manifest-hash', 'a'.repeat(64)]),
      },
      {
        label: '--manifest-hash too short',
        argv: runsCreateArgv(['--manifest-id', manifestId, '--manifest-hash', shortHash]),
      },
      {
        label: '--manifest-hash uppercase',
        argv: runsCreateArgv(['--manifest-id', manifestId, '--manifest-hash', upperHash]),
      },
      { label: '--ci-ref unprefixed', argv: createArgv(['--ci-ref', 'main']) },
      { label: '--ci-ref empty', argv: createArgv(['--ci-ref', '']) },
      {
        label: '--ci-ref other namespace',
        argv: createArgv(['--ci-ref', 'refs/remotes/origin/main']),
      },
      { label: '--ci-ref empty branch name', argv: createArgv(['--ci-ref', 'refs/heads/']) },
      { label: '--ci-ref PR number zero', argv: createArgv(['--ci-ref', 'refs/pull/0/merge']) },
      { label: '--ci-ref PR wrong suffix', argv: createArgv(['--ci-ref', 'refs/pull/42/head']) },
      {
        label: '--ci-ref PR number past the safe integer range',
        argv: createArgv(['--ci-ref', 'refs/pull/9007199254740992/merge']),
      },
      {
        label: '--ci-ref PR number of 400 digits',
        argv: createArgv(['--ci-ref', `refs/pull/${'9'.repeat(400)}/merge`]),
      },
      {
        label: '--ci-ref name over 255 chars',
        argv: createArgv(['--ci-ref', `refs/heads/${'a'.repeat(256)}`]),
      },
      { label: '--idempotency-key empty', argv: createArgv(['--idempotency-key', '']) },
      {
        label: '--idempotency-key over 128 chars',
        argv: createArgv(['--idempotency-key', 'k'.repeat(129)]),
      },
      {
        label: '--idempotency-key with a space',
        argv: createArgv(['--idempotency-key', 'key with space']),
      },
      { label: '--idempotency-key with a tab', argv: createArgv(['--idempotency-key', 'key\tv']) },
      { label: '--release empty after trim', argv: createArgv(['--release', '   ']) },
      { label: '--release over 128 chars', argv: createArgv(['--release', 'r'.repeat(129)]) },
    ];

    for (const testCase of cases) {
      const env = await ingestEnv();
      const stub = stubFetch(() => created());
      const out = capture();
      const attempt = invoke(testCase.argv, env, out, stub.fetchImpl);
      await expect(attempt, testCase.label).resolves.toBe(3);
      const text = out.stderr.join('');
      expect(text, testCase.label).toContain('evidgate: error usage: ');
      expect(text, testCase.label).toContain("evidgate: hint: run 'evidgate --help' for usage");
      expect(out.stdout.join(''), testCase.label).toBe('');
      expect(stub.calls, testCase.label).toHaveLength(0);
    }
  });

  // 04.2.2-T2.1 / contracts/cli.md §9 step 1: every option is single-use. `multiple: true`
  // makes a repetition visible to the reader, which refuses it instead of silently sending
  // the last value — the failure mode that would defeat the `--idempotency-key` replay
  // guard. The message names the flag only, never the value, and no request is made.
  it('repeated_flag_refused', async () => {
    const cases: ReadonlyArray<{ readonly flag: string; readonly argv: string[] }> = [
      {
        flag: 'manifest-id',
        argv: runsCreateArgv([
          '--manifest-id',
          manifestId,
          '--manifest-hash',
          manifestHash,
          '--manifest-id',
          'zz-other-id',
        ]),
      },
      {
        flag: 'manifest-hash',
        argv: runsCreateArgv([
          '--manifest-id',
          manifestId,
          '--manifest-hash',
          manifestHash,
          '--manifest-hash',
          'zz-other-hash',
        ]),
      },
      { flag: 'release', argv: createArgv(['--release', 'zz-one', '--release', 'zz-two']) },
      {
        flag: 'ci-ref',
        argv: createArgv(['--ci-ref', 'refs/heads/zz-main', '--ci-ref', 'refs/tags/zz-v1']),
      },
      {
        flag: 'idempotency-key',
        argv: createArgv(['--idempotency-key', 'zz-first', '--idempotency-key', 'zz-second']),
      },
    ];

    for (const testCase of cases) {
      const stub = stubFetch(() => created());
      const out = capture();
      // An ingest source would exchange a token over the network: the refusal is local.
      await expect(
        invoke(testCase.argv, await ingestEnv(), out, stub.fetchImpl),
        testCase.flag,
      ).resolves.toBe(3);
      const text = out.stderr.join('');
      expect(text, testCase.flag).toContain(
        `evidgate: error usage: --${testCase.flag} may be given only once`,
      );
      expect(text, testCase.flag).toContain("evidgate: hint: run 'evidgate --help' for usage");
      expect(text, testCase.flag).not.toContain('zz-');
      expect(out.stdout.join(''), testCase.flag).toBe('');
      expect(stub.calls, testCase.flag).toHaveLength(0);
    }
  });

  // 04.2.2-T2.1 / hand-over from 04.2.1-T1 #23: a 401 from the project-scoped command
  // purges the cached CI token, so the next `getToken()` re-exchanges.
  it('unauthorized_purges_cache', async () => {
    const env = await ingestEnv();
    const config = resolveConfig({ env });
    const expiresAt = new Date(Date.now() + 30 * 60_000).toISOString();
    await writeCachedToken(config, { token: 'egt_cached_token', expiresAt });
    await expect(readCachedToken(config)).resolves.toEqual({
      token: 'egt_cached_token',
      expiresAt,
    });

    const stub = stubFetch(() =>
      jsonResponse(401, {
        error: {
          code: 'token_revoked',
          message: 'the token was revoked',
          request_id: 'req-1',
          retryable: false,
        },
      }),
    );
    const out = capture();

    await expect(invoke(createArgv(), env, out, stub.fetchImpl)).resolves.toBe(3);

    expect(out.stderr.join('')).toContain('evidgate: error token_revoked: ');
    // The cached token was reused (no exchange), so the create is the only call.
    expect(stub.calls).toHaveLength(1);
    await expect(readCachedToken(config)).resolves.toBeUndefined();
  });

  // 04.2.2-T2.1 / contracts/cli.md §7: a 403 is a scope refusal, not a dead token, so the
  // cached token stays.
  it('forbidden_keeps_cache', async () => {
    const env = await ingestEnv();
    const config = resolveConfig({ env });
    const expiresAt = new Date(Date.now() + 30 * 60_000).toISOString();
    await writeCachedToken(config, { token: 'egt_cached_token', expiresAt });
    const stub = stubFetch(() =>
      jsonResponse(403, {
        error: {
          code: 'project_scope_mismatch',
          message: 'the token is scoped to another project',
          request_id: 'req-1',
          retryable: false,
        },
      }),
    );
    const out = capture();

    await expect(invoke(createArgv(), env, out, stub.fetchImpl)).resolves.toBe(3);

    expect(out.stderr.join('')).toContain('evidgate: error project_scope_mismatch: ');
    await expect(readCachedToken(config)).resolves.toEqual({
      token: 'egt_cached_token',
      expiresAt,
    });
  });

  // 04.2.2-T2.1 / §5: a 2xx with an invalid body (or a non-envelope non-2xx) is
  // `request_failed`, and the body is never echoed.
  it('malformed_body_request_failed', async () => {
    const env = await devEnv();

    const badId = stubDevFetch(() => created({ run_id: 'not-a-uuid' }));
    const badIdOut = capture();
    await expect(invoke(createArgv(), env, badIdOut, badId.fetchImpl)).resolves.toBe(3);
    const badIdText = badIdOut.stderr.join('');
    expect(badIdText).toContain('evidgate: error request_failed: ');
    expect(badIdText).not.toContain('not-a-uuid');
    expect(badIdOut.stdout.join('')).toBe('');

    const badState = stubDevFetch(() => created({ state: '' }));
    const badStateOut = capture();
    await expect(invoke(createArgv(), env, badStateOut, badState.fetchImpl)).resolves.toBe(3);
    expect(badStateOut.stderr.join('')).toContain('evidgate: error request_failed: ');
    expect(badStateOut.stdout.join('')).toBe('');

    // §9 step 3: the state must match `^[a-z][a-z0-9_]{0,31}$`. A newline, an ANSI escape,
    // an uppercase letter or a 33-character value is a bad body, and nothing from it — the
    // state included — reaches either stream.
    for (const state of [
      'queued\n',
      'queued\u001b[31m',
      'Queued',
      'queued state',
      '1queued',
      '_queued',
      'a'.repeat(33),
    ]) {
      const stub = stubDevFetch(() => created({ state }));
      const out = capture();
      await expect(invoke(createArgv(), env, out, stub.fetchImpl)).resolves.toBe(3);
      const text = out.stderr.join('');
      expect(text).toContain('evidgate: error request_failed: ');
      expect(text).toContain('the run create response was not valid');
      expect(text).not.toContain('queued');
      expect(text).not.toContain('\u001b');
      expect(out.stdout.join('')).toBe('');
    }

    // A gateway page that is not the 00 §9.2 envelope keeps the CLI's fallback code.
    const gateway = stubDevFetch(
      () =>
        new Response('<html>bad gateway</html>', {
          status: 502,
          headers: { 'content-type': 'text/html' },
        }),
    );
    const gatewayOut = capture();
    await expect(invoke(createArgv(), env, gatewayOut, gateway.fetchImpl)).resolves.toBe(3);
    const gatewayText = gatewayOut.stderr.join('');
    expect(gatewayText).toContain('evidgate: error request_failed: ');
    expect(gatewayText).toContain('HTTP 502');
    expect(gatewayText).not.toContain('bad gateway');
  });

  // 04.2.2-T2.1 / §5: a transport failure is `transport` and never echoes the URL.
  it('transport_error_exit3', async () => {
    const env = await devEnv();
    // The exchange answers, so the failure is the create's own transport error.
    const failing = stubDevFetch(() => {
      throw new TypeError('fetch failed http://x');
    });
    const out = capture();

    await expect(invoke(createArgv(), env, out, failing.fetchImpl)).resolves.toBe(3);

    const text = out.stderr.join('');
    expect(text).toContain('evidgate: error transport: ');
    expect(text).not.toContain('http://x');
  });

  // 04.2.2-T2.1 / T78: `--verbose` dumps the resolved config with the secret fields
  // redacted, and the command adds no output of its own — neither the token nor the
  // `Idempotency-Key` it sent can appear.
  it('verbose_no_secrets', async () => {
    const env = await devEnv();
    const derivedKey = deriveRunIdempotencyKey({ manifestId, manifestHash });
    const stub = stubDevFetch(() => created());
    const out = capture();

    const argv = createArgv(['--verbose']);
    await expect(invoke(argv, env, out, stub.fetchImpl)).resolves.toBe(0);

    const text = out.stderr.join('');
    expect(text).toContain('evidgate: resolved config');
    expect(text).toContain('dev_token=[REDACTED]');
    expect(text).toContain(`project=${project}`);
    expect(text).not.toContain(devToken);
    expect(text).not.toContain(mintedToken);
    // The key did travel (the header is set) but never reaches either stream.
    expect(apiCalls(stub)[0]?.request.headers.get('idempotency-key')).toBe(derivedKey);
    expect(text).not.toContain(derivedKey);
    expect(out.stdout.join('')).not.toContain(derivedKey);
    expect(out.stdout.join('')).toBe(
      `run_id=${runId} manifest_hash=${manifestHash} state=queued\n`,
    );
  });
});
