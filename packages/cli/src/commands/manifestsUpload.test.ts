// 04.2.2-T1.1: `evidgate manifests upload` on the binary — the raw-bytes upload, the
// `manifest_id`/`manifest_hash` output, the `manifest_invalid` details passthrough, the
// local size cap, the 401 cache purge (hand-over from 04.2.1-T1 #23) and every failure
// path (04.2.2 §2 AC1, §5, §8, §9, §10; contracts/cli.md §5/§6/§7).
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { chmod, mkdtemp, rm, symlink, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { readCachedToken, writeCachedToken } from '../auth/tokenCache.js';
import { resolveConfig } from '../config.js';
import { main } from '../main.js';
import { resetSecrets, type Io } from '../redact.js';

/** The project slug the command scopes the upload to. */
const project = 'proj-1';
/** A valid `manifest_id` (openapi `format: uuid`). */
const manifestId = '9b2f4c1e-2a3b-4c5d-8e9f-0a1b2c3d4e5f';
/** A valid `manifest_hash` (`sha256:` + 64 hex). */
const manifestHash = `sha256:${'a'.repeat(64)}`;
/** The server's documented body cap (`createManifest`: "Body ≤ 256 KiB → 413"). */
const maxManifestBytes = 256 * 1024;

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

describe('manifests upload', () => {
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

  /** Writes `text` to a fresh temp file and returns its path. */
  async function manifestFile(text: string): Promise<string> {
    const path = join(await tempDir(), 'manifest.json');
    await writeFile(path, text, 'utf8');
    return path;
  }

  /** The M1 dev path: no exchange, no cache, no network (04.2.1 AC8). */
  async function devEnv(extra: Record<string, string> = {}) {
    const cacheDir = await tempDir();
    return {
      EVIDGATE_API_URL: 'http://127.0.0.1:8080',
      EVIDGATE_PROJECT: project,
      EVIDGATE_ENV: 'dev',
      EVIDGATE_DEV_TOKEN: 'dev-token-value',
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

  // 04.2.2-T1.1 / AC1: the file bytes travel unchanged (the server hashes the canonical
  // form, so a re-serialisation could lose the large integer's precision) and the two
  // ids are printed.
  it('prints_ids', async () => {
    const text =
      '{\n  "schema": "manifest.v1",\n  "n": 12345678901234567890,\n  "pad": "  x  "\n}\n';
    const file = await manifestFile(text);
    const env = await devEnv();
    const { fetchImpl, calls } = stubFetch(() =>
      jsonResponse(201, { manifest_id: manifestId, manifest_hash: manifestHash }),
    );
    const { io, stdout, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(0);

    expect(stdout.join('')).toBe(`manifest_id=${manifestId} manifest_hash=${manifestHash}\n`);
    expect(stderr.join('')).toBe('');
    expect(calls).toHaveLength(1);
    const call = calls[0];
    expect(call?.request.method).toBe('POST');
    // `createApiClient` puts the SDK's `/v1` server prefix on the base URL, so the
    // operation path is `<root>/v1/projects/{p}/manifests` (the env value carries no `/v1`).
    expect(new URL(call?.request.url ?? '').pathname).toBe(`/v1/projects/${project}/manifests`);
    expect(call?.request.headers.get('authorization')).toBe('Bearer dev-token-value');
    expect(call?.request.headers.get('content-type')).toBe('application/json');
    // 00 §9.1: the route requires an `Idempotency-Key` (openapi `required: true`); the
    // file's own digest makes a retry of the same manifest replay the stored 201.
    expect(call?.request.headers.get('idempotency-key')).toBe(
      createHash('sha256').update(text).digest('hex'),
    );
    expect(call?.body.toString('utf8')).toBe(text);
  });

  // 04.2.2-T1.1 / AC1: `--json` prints exactly one line with the two ids.
  it('json_output', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = stubFetch(() =>
      jsonResponse(201, { manifest_id: manifestId, manifest_hash: manifestHash }),
    );
    const { io, stdout, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file, '--json'], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(0);

    expect(stdout.join('')).toBe(
      `{"manifest_id":"${manifestId}","manifest_hash":"${manifestHash}"}\n`,
    );
    expect(stderr.join('')).toBe('');
  });

  // 04.2.2-T1.1 / AC1: a 422 `manifest_invalid` exits 3 and passes the details through.
  it('invalid_manifest_exit3', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = stubFetch(() =>
      jsonResponse(422, {
        error: {
          code: 'manifest_invalid',
          message: 'the manifest is invalid',
          request_id: 'req-1',
          retryable: false,
          details: [
            { index: 0, path: '/executions/0/case_revision_id', reason: 'unknown_case_revision' },
            { path: '/matrix', reason: 'matrix_not_covered' },
          ],
        },
      }),
    );
    const { io, stdout, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain('evidgate: error manifest_invalid: ');
    expect(text).toContain('evidgate: request_id: req-1');
    expect(text).toContain(
      'evidgate: detail: index=0 path=/executions/0/case_revision_id reason=unknown_case_revision',
    );
    expect(text).toContain('evidgate: detail: path=/matrix reason=matrix_not_covered');
    expect(stdout.join('')).toBe('');
  });

  // 04.2.2-T1.1 / §5: at most 50 detail lines, then a `(k more)` line; every field is
  // control-char escaped so a detail can never forge a `::` workflow command.
  it('details_capped_and_escaped', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const details = Array.from({ length: 60 }, (_value, index) => ({
      index,
      path: `/p/${index}`,
      reason: 'r',
    }));
    const { fetchImpl } = stubFetch(() =>
      jsonResponse(422, {
        error: {
          code: 'manifest_invalid',
          message: 'the manifest is invalid',
          request_id: 'req-1',
          retryable: false,
          details,
        },
      }),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const detailLines = stderr
      .join('')
      .split('\n')
      .filter((line) => line.startsWith('evidgate: detail: '));
    expect(detailLines).toHaveLength(51);
    expect(detailLines[50]).toBe('evidgate: detail: (10 more)');

    // A path that embeds a newline and a `::` command is escaped, not split.
    const escapedEnv = await devEnv();
    const escapedFile = await manifestFile('{"schema":"manifest.v1"}');
    const { fetchImpl: escapedFetch } = stubFetch(() =>
      jsonResponse(422, {
        error: {
          code: 'manifest_invalid',
          message: 'the manifest is invalid',
          request_id: 'req-1',
          retryable: false,
          details: [{ path: '/x\n::error::x', reason: 'bad' }],
        },
      }),
    );
    const escapedCapture = capture();
    await expect(
      main(['manifests', 'upload', '--file', escapedFile], escapedEnv, escapedCapture.io, {
        fetch: escapedFetch,
      }),
    ).resolves.toBe(3);

    const escapedText = escapedCapture.stderr.join('');
    expect(escapedText).toContain('path=/x\\u000a::error::x');
    expect(escapedText).not.toContain('\n::error::x');
    for (const line of escapedText.split('\n')) {
      if (line.length > 0) {
        expect(line.startsWith('evidgate: ')).toBe(true);
      }
    }
  });

  // 04.2.2-T1.1 / §10 done-when: a file over the cap exits 3 before any request (no
  // token exchange either); exactly the cap is sent.
  it('oversize_no_request', async () => {
    const oversize = await manifestFile('a'.repeat(maxManifestBytes + 1));
    const env = await devEnv();
    const { fetchImpl, calls } = stubFetch(() =>
      jsonResponse(201, { manifest_id: manifestId, manifest_hash: manifestHash }),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', oversize], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: error manifest_too_large: ');
    expect(calls).toHaveLength(0);

    // An ingest source would exchange a token first; the size check still wins.
    const ingest = await ingestEnv();
    const ingestStub = stubFetch(() =>
      jsonResponse(201, { manifest_id: manifestId, manifest_hash: manifestHash }),
    );
    const ingestCapture = capture();
    await expect(
      main(['manifests', 'upload', '--file', oversize], ingest, ingestCapture.io, {
        fetch: ingestStub.fetchImpl,
      }),
    ).resolves.toBe(3);
    expect(ingestCapture.stderr.join('')).toContain('evidgate: error manifest_too_large: ');
    expect(ingestStub.calls).toHaveLength(0);

    // Exactly the cap is accepted and sent.
    const boundary = await manifestFile(`{"pad":"${'a'.repeat(maxManifestBytes - 10)}"}`);
    const boundaryStub = stubFetch(() =>
      jsonResponse(201, { manifest_id: manifestId, manifest_hash: manifestHash }),
    );
    const boundaryCapture = capture();
    await expect(
      main(['manifests', 'upload', '--file', boundary], env, boundaryCapture.io, {
        fetch: boundaryStub.fetchImpl,
      }),
    ).resolves.toBe(0);
    expect(boundaryStub.calls).toHaveLength(1);
  });

  // 04.2.2-T1.1 / §5: an unreadable input (missing, a directory, empty) exits 3 with no
  // request at all.
  it('unreadable_no_request', async () => {
    const env = await devEnv();
    const missing = join(await tempDir(), 'nope.json');
    const directory = await tempDir();
    const empty = await manifestFile('');

    for (const path of [missing, directory, empty]) {
      const { fetchImpl, calls } = stubFetch(() =>
        jsonResponse(201, { manifest_id: manifestId, manifest_hash: manifestHash }),
      );
      const { io, stderr } = capture();
      await expect(
        main(['manifests', 'upload', '--file', path], env, io, { fetch: fetchImpl }),
      ).resolves.toBe(3);
      expect(stderr.join('')).toContain('evidgate: error manifest_unreadable: ');
      expect(calls).toHaveLength(0);
    }
  });

  // 04.2.2-T1.1 / §5: a missing `--file` is a usage error before any request.
  it('missing_file_flag_exit3', async () => {
    const env = await devEnv();
    const { fetchImpl, calls } = stubFetch(() =>
      jsonResponse(201, { manifest_id: manifestId, manifest_hash: manifestHash }),
    );
    const { io, stderr } = capture();

    await expect(main(['manifests', 'upload'], env, io, { fetch: fetchImpl })).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain('evidgate: error usage: ');
    expect(text).toContain('--file');
    // The command-level usage error carries the same hint as main.ts's parse errors.
    expect(text).toContain("evidgate: hint: run 'evidgate --help' for usage");
    expect(calls).toHaveLength(0);
  });

  // 04.2.2-T1.1 / hand-over from 04.2.1-T1 #23: a 401 from the project-scoped command
  // purges the cached CI token; a 403 (scope mismatch) leaves it in place.
  it('unauthorized_purges_cache', async () => {
    const env = await ingestEnv();
    const config = resolveConfig({ env });
    const expiresAt = new Date(Date.now() + 30 * 60_000).toISOString();
    await writeCachedToken(config, { token: 'egt_cached_token', expiresAt });
    await expect(readCachedToken(config)).resolves.toEqual({
      token: 'egt_cached_token',
      expiresAt,
    });

    const file = await manifestFile('{"schema":"manifest.v1"}');
    const { fetchImpl, calls } = stubFetch(() =>
      jsonResponse(401, {
        error: {
          code: 'token_revoked',
          message: 'the token was revoked',
          request_id: 'req-1',
          retryable: false,
        },
      }),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: error token_revoked: ');
    // The cached token was reused (no exchange), so the upload is the only call.
    expect(calls).toHaveLength(1);
    await expect(readCachedToken(config)).resolves.toBeUndefined();

    // 403 is a scope mismatch, not a dead token: the cache survives.
    const forbiddenEnv = await ingestEnv();
    const forbiddenConfig = resolveConfig({ env: forbiddenEnv });
    await writeCachedToken(forbiddenConfig, { token: 'egt_cached_token', expiresAt });
    const forbiddenFile = await manifestFile('{"schema":"manifest.v1"}');
    const forbiddenStub = stubFetch(() =>
      jsonResponse(403, {
        error: {
          code: 'project_scope_mismatch',
          message: 'the token is scoped to another project',
          request_id: 'req-1',
          retryable: false,
        },
      }),
    );
    const forbiddenCapture = capture();
    await expect(
      main(['manifests', 'upload', '--file', forbiddenFile], forbiddenEnv, forbiddenCapture.io, {
        fetch: forbiddenStub.fetchImpl,
      }),
    ).resolves.toBe(3);
    expect(forbiddenCapture.stderr.join('')).toContain('evidgate: error project_scope_mismatch: ');
    await expect(readCachedToken(forbiddenConfig)).resolves.toEqual({
      token: 'egt_cached_token',
      expiresAt,
    });
  });

  // 04.2.2-T1.1 / §5: a purge failure is swallowed — the server code still surfaces.
  it.skipIf(process.platform === 'win32' || process.getuid?.() === 0)(
    'purge_failure_swallowed',
    async () => {
      const env = await ingestEnv();
      const config = resolveConfig({ env });
      const expiresAt = new Date(Date.now() + 30 * 60_000).toISOString();
      await writeCachedToken(config, { token: 'egt_cached_token', expiresAt });
      const file = await manifestFile('{"schema":"manifest.v1"}');
      const { fetchImpl } = stubFetch(() =>
        jsonResponse(401, {
          error: {
            code: 'token_revoked',
            message: 'the token was revoked',
            request_id: 'req-1',
            retryable: false,
          },
        }),
      );
      const { io, stderr } = capture();

      // A read-only cache directory makes the purge fail (EACCES).
      await chmod(config.cacheDir, 0o500);
      try {
        await expect(
          main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
        ).resolves.toBe(3);
      } finally {
        await chmod(config.cacheDir, 0o700);
      }

      const text = stderr.join('');
      expect(text).toContain('evidgate: error token_revoked: ');
      expect(text).not.toContain('token_cache_error');
    },
  );

  // 04.2.2-T1.1 / T78: a server message that embeds the bearer is redacted.
  it('token_never_printed', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = stubFetch(() =>
      jsonResponse(500, {
        error: {
          code: 'server_error',
          message: 'boom dev-token-value',
          request_id: 'req-1',
          retryable: true,
        },
      }),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain('[REDACTED]');
    expect(text).not.toContain('dev-token-value');
  });

  // 04.2.2-T1.1 / §5: a non-envelope non-2xx and a 2xx with an invalid body are both
  // `request_failed`, and the body is never echoed.
  it('bad_response_bodies_request_failed', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();

    const gatewayStub = stubFetch(
      () =>
        new Response('<html>bad gateway</html>', {
          status: 502,
          headers: { 'content-type': 'text/html' },
        }),
    );
    const gatewayCapture = capture();
    await expect(
      main(['manifests', 'upload', '--file', file], env, gatewayCapture.io, {
        fetch: gatewayStub.fetchImpl,
      }),
    ).resolves.toBe(3);
    const gatewayText = gatewayCapture.stderr.join('');
    expect(gatewayText).toContain('evidgate: error request_failed: ');
    expect(gatewayText).toContain('HTTP 502');
    expect(gatewayText).not.toContain('bad gateway');

    const invalidStub = stubFetch(() =>
      jsonResponse(201, { manifest_id: manifestId, manifest_hash: 'nope' }),
    );
    const invalidCapture = capture();
    await expect(
      main(['manifests', 'upload', '--file', file], env, invalidCapture.io, {
        fetch: invalidStub.fetchImpl,
      }),
    ).resolves.toBe(3);
    const invalidText = invalidCapture.stderr.join('');
    expect(invalidText).toContain('evidgate: error request_failed: ');
    expect(invalidText).not.toContain('nope');
  });

  // 04.2.2-T1.1 / §5: a transport failure is `transport` and never echoes the URL.
  it('transport_error_exit3', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const fetchImpl: typeof fetch = () =>
      Promise.reject(new TypeError('fetch failed http://secret'));
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain('evidgate: error transport: ');
    expect(text).not.toContain('http://secret');
  });
});

// Independent boundary, negative and exploratory coverage for `manifests upload`, merged
// here so there is one test file per command (the `authLogin.test.ts` precedent). Every
// case is deterministic and isolated: the only transport is an injected `fetch` stub —
// except the redirect case, which needs a real 127.0.0.1 server because the redirect
// refusal lives in the transport layer (`redirect: 'error'` on the outgoing Request).
describe('manifests upload — boundaries', () => {
  const dirs: string[] = [];

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  async function tempDir(): Promise<string> {
    const dir = await mkdtemp(join(tmpdir(), 'evidgate-cli-qa-'));
    dirs.push(dir);
    return dir;
  }

  async function manifestFile(content: string | Buffer): Promise<string> {
    const path = join(await tempDir(), 'manifest.json');
    await writeFile(path, content);
    return path;
  }

  async function devEnv(extra: Record<string, string> = {}) {
    const cacheDir = await tempDir();
    return {
      EVIDGATE_API_URL: 'http://127.0.0.1:8080',
      EVIDGATE_PROJECT: project,
      EVIDGATE_ENV: 'dev',
      EVIDGATE_DEV_TOKEN: 'dev-token-value',
      EVIDGATE_CACHE_DIR: cacheDir,
      ...extra,
    };
  }

  const ok = () => jsonResponse(201, { manifest_id: manifestId, manifest_hash: manifestHash });

  function envelope(code: string, message: string, extra: Record<string, unknown> = {}) {
    return { error: { code, message, request_id: 'req-1', retryable: false, ...extra } };
  }

  // ---- boundary: non-UTF-8 bytes travel verbatim and the key is their digest ----

  it('non_utf8_bytes_sent_verbatim', async () => {
    const bytes = Buffer.from([0xff, 0xfe, 0x00, 0x01, 0x80, 0x7f, 0x0a]);
    const file = await manifestFile(bytes);
    const env = await devEnv();
    const { fetchImpl, calls } = stubFetch(ok);
    const { io } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(0);

    expect(calls[0]?.body.equals(bytes)).toBe(true);
    expect(calls[0]?.request.headers.get('idempotency-key')).toBe(
      createHash('sha256').update(bytes).digest('hex'),
    );
  });

  // ---- path handling: symlink, device, FIFO, relative path, empty flag ----

  it('symlink_to_a_file_is_uploaded', async () => {
    const target = await manifestFile('{"schema":"manifest.v1"}');
    const link = join(await tempDir(), 'link.json');
    await symlink(target, link);
    const env = await devEnv();
    const { fetchImpl, calls } = stubFetch(ok);
    const { io } = capture();

    await expect(
      main(['manifests', 'upload', '--file', link], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(0);
    expect(calls).toHaveLength(1);
  });

  it.skipIf(process.platform === 'win32')('dev_zero_is_unreadable_with_zero_requests', async () => {
    const env = await devEnv();
    const { fetchImpl, calls } = stubFetch(ok);
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', '/dev/zero'], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: error manifest_unreadable: ');
    expect(calls).toHaveLength(0);
  });

  // `open(path, 'r')` on a FIFO blocks until a writer appears, so the non-regular-file
  // check never ran and the command hung. The open is `O_RDONLY | O_NONBLOCK`, which
  // returns at once on a FIFO, so the `fstat` check refuses it (contracts/cli.md §8 step 2).
  it.skipIf(process.platform === 'win32')(
    'fifo_is_refused_without_hanging',
    async () => {
      const fifo = join(await tempDir(), 'manifest.fifo');
      execFileSync('mkfifo', [fifo]);
      const env = await devEnv();
      const { fetchImpl, calls } = stubFetch(ok);
      const { io, stderr } = capture();

      const pending = main(['manifests', 'upload', '--file', fifo], env, io, {
        fetch: fetchImpl,
      });
      // Real wall-clock timer on purpose: the hang was a blocked libuv `open()` on the
      // FIFO, not a scheduled timer, so fake timers cannot observe it. The race is a
      // bounded liveness probe, not a sleep that masks a race.
      const outcome = await Promise.race([
        pending.then(
          (code) => `settled:${code}`,
          () => 'settled:threw',
        ),
        new Promise<string>((resolve) => setTimeout(() => resolve('hung'), 1500)),
      ]);
      await pending;

      expect(outcome).toBe('settled:3');
      expect(stderr.join('')).toContain('evidgate: error manifest_unreadable: ');
      expect(calls).toHaveLength(0);
    },
    10_000,
  );

  it('empty_file_flag_is_usage_with_zero_requests', async () => {
    const env = await devEnv();
    const { fetchImpl, calls } = stubFetch(ok);
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file='], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: error usage: ');
    expect(calls).toHaveLength(0);
  });

  it('relative_path_is_uploaded', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const rel = relative(process.cwd(), file);
    expect(rel.startsWith('.')).toBe(true);
    const env = await devEnv();
    const { fetchImpl, calls } = stubFetch(ok);
    const { io } = capture();

    await expect(
      main(['manifests', 'upload', '--file', rel], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(0);
    expect(calls).toHaveLength(1);
  });

  // ---- details rendering: skipped fields, escaping, caps ----

  it('details_skip_non_string_index_and_path', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = stubFetch(() =>
      jsonResponse(
        422,
        envelope('manifest_invalid', 'bad', {
          details: [{ index: 'zero', path: 42, reason: 'fine' }],
        }),
      ),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    // contracts/cli.md §5: `index=` is omitted when the entry has none and a field that
    // is not a string or a number is skipped.
    expect(stderr.join('')).toContain('evidgate: detail: reason=fine\n');
    expect(stderr.join('')).not.toContain('index=');
    expect(stderr.join('')).not.toContain('path=');
  });

  // Pins contracts/cli.md §5's fail-closed rule: a detail whose `reason` is not a string
  // makes the whole envelope unusable (`isErrorEnvelope` requires a string `reason`), so
  // the reply falls back to `request_failed` and no `detail:` line is printed.
  it('details_non_string_reason_drops_the_envelope', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = stubFetch(() =>
      jsonResponse(
        422,
        envelope('manifest_invalid', 'bad', {
          details: [
            { index: 0, path: '/a', reason: 7 },
            { index: 1, path: '/b', reason: 'fine' },
          ],
        }),
      ),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain('evidgate: error request_failed: ');
    expect(text).not.toContain('detail:');
  });

  it('detail_fields_are_escaped_and_capped', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = stubFetch(() =>
      jsonResponse(
        422,
        envelope('manifest_invalid', 'msg\n::error::forged\u0007', {
          details: [{ index: 0, path: '/a\nb', reason: 'r'.repeat(300) }],
        }),
      ),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain('msg\\u000a::error::forged\\u0007');
    expect(text).toContain('path=/a\\u000ab');
    // Every stderr line carries the prefix, so no line can start with `::`.
    for (const line of text.split('\n').filter((l) => l.length > 0)) {
      expect(line.startsWith('evidgate: ')).toBe(true);
    }
    const reasonLine = text.split('\n').find((line) => line.includes('reason=')) ?? '';
    expect(reasonLine.length - 'evidgate: detail: index=0 path=/a\\u000ab reason='.length).toBe(
      200,
    );
  });

  it('message_is_capped_at_200_chars', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = stubFetch(() =>
      jsonResponse(422, envelope('manifest_invalid', 'm'.repeat(500))),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const line = stderr.join('').split('\n')[0] ?? '';
    expect(line).toBe(`evidgate: error manifest_invalid: ${'m'.repeat(200)}`);
  });

  it('message_cap_never_leaves_a_secret_prefix', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    // The dev token starts at code point 188, so the raw 200-code-point cut would fall
    // inside it. Redaction runs before the cap, so no prefix of the token can survive.
    const message = `${'x'.repeat(188)}dev-token-value${'y'.repeat(50)}`;
    const { fetchImpl } = stubFetch(() => jsonResponse(422, envelope('manifest_invalid', message)));
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).not.toContain('dev-token-value');
    expect(text).not.toContain('dev-toke');
    expect(text).toContain('[REDACTED]');
  });

  it('message_cap_does_not_split_an_astral_char', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    // The emoji is the 200th code point; a UTF-16 `slice(0, 200)` would keep only its
    // high surrogate, so the cap counts code points instead.
    const message = `${'a'.repeat(199)}\u{1F600}${'b'.repeat(10)}`;
    const { fetchImpl } = stubFetch(() => jsonResponse(422, envelope('manifest_invalid', message)));
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text.split('\n')[0]).toBe(
      `evidgate: error manifest_invalid: ${'a'.repeat(199)}\u{1F600}`,
    );
    // No lone surrogate anywhere in stderr.
    expect(text).not.toMatch(
      /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/,
    );
  });

  // ---- envelope edge cases: unsafe code, request_id escaping, header fallback ----

  it('unsafe_code_pattern_falls_back_to_request_failed', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = stubFetch(() => jsonResponse(422, envelope('Bad Code!', 'bad')));
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: error request_failed: bad\n');
  });

  it('request_id_control_chars_are_escaped', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = stubFetch(() =>
      jsonResponse(
        422,
        envelope('manifest_invalid', 'ok', { request_id: 'req\n::error::forged\u0007' }),
      ),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: request_id: req\\u000a::error::forged\\u0007\n');
  });

  it('request_id_is_capped_at_200_code_points', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = stubFetch(() =>
      jsonResponse(422, envelope('manifest_invalid', 'ok', { request_id: 'r'.repeat(5000) })),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain(`evidgate: request_id: ${'r'.repeat(200)}\n`);
    expect(text).not.toContain('r'.repeat(201));
  });

  it('x_request_id_header_is_the_fallback', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = stubFetch(() =>
      jsonResponse(500, { unexpected: true }, { 'x-request-id': 'header-req-9' }),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain(
      'evidgate: error request_failed: the manifest upload failed (HTTP 500)\n',
    );
    expect(text).toContain('evidgate: request_id: header-req-9\n');
  });

  it('server_413_code_is_echoed', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = stubFetch(() =>
      jsonResponse(413, envelope('payload_too_large', 'body too large')),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: error payload_too_large: body too large\n');
  });

  // The redirect refusal lives in the transport layer (`redirect: 'error'` on the
  // outgoing Request), so it needs a real 127.0.0.1 server: an injected `fetch` stub
  // that returns a 302 Response never performs the redirect.
  it('redirect_is_refused_as_transport', async () => {
    const server = createServer((_request, response) => {
      response.writeHead(302, { location: 'http://127.0.0.1:1/elsewhere' });
      response.end();
    });
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
    const address = server.address();
    const port = typeof address === 'object' && address !== null ? address.port : 0;
    try {
      const file = await manifestFile('{"schema":"manifest.v1"}');
      const env = await devEnv({ EVIDGATE_API_URL: `http://127.0.0.1:${port}` });
      const { io, stderr } = capture();

      await expect(main(['manifests', 'upload', '--file', file], env, io)).resolves.toBe(3);

      const text = stderr.join('');
      expect(text).toContain('evidgate: error transport: the request failed\n');
      expect(text).not.toContain('127.0.0.1:1');
    } finally {
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
  });

  // ---- output rules: --verbose, --json on failure, token cache ----

  it('verbose_never_prints_manifest_content_or_token', async () => {
    const secret = 'SUPER-SECRET-MANIFEST-BODY';
    const file = await manifestFile(`{"schema":"manifest.v1","note":"${secret}"}`);
    const env = await devEnv();
    const { fetchImpl } = stubFetch(ok);
    const { io, stdout, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file, '--verbose'], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(0);

    const text = stdout.join('') + stderr.join('');
    expect(text).not.toContain(secret);
    expect(text).not.toContain('dev-token-value');
    expect(stderr.join('')).toContain('dev_token=[REDACTED]');
  });

  it('json_flag_keeps_errors_on_stderr', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = stubFetch(() => jsonResponse(422, envelope('manifest_invalid', 'bad')));
    const { io, stdout, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file, '--json'], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    expect(stdout.join('')).toBe('');
    expect(stderr.join('')).toContain('evidgate: error manifest_invalid: bad\n');
  });

  it('401_without_a_cache_entry_is_not_a_cache_error', async () => {
    const cacheDir = await tempDir();
    const env = {
      EVIDGATE_API_URL: 'https://api.evidgate.test',
      EVIDGATE_PROJECT: project,
      EVIDGATE_CACHE_DIR: cacheDir,
      EVIDGATE_INGEST_TOKEN: 'egt_ingest_secret_value',
    };
    const config = resolveConfig({ env });
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const { fetchImpl } = stubFetch(() => jsonResponse(401, envelope('token_revoked', 'revoked')));
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain('evidgate: error token_revoked: revoked\n');
    expect(text).not.toContain('token_cache_error');
    await expect(readCachedToken(config)).resolves.toBeUndefined();
  });
});
