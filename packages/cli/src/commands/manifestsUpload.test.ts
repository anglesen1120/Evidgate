// 04.2.2-T1.1: `evidgate manifests upload` on the binary — the raw-bytes upload, the
// `manifest_id`/`manifest_hash` output, the `manifest_invalid` details passthrough, the
// local size cap, the 401 cache purge (hand-over from 04.2.1-T1 #23) and every failure
// path (04.2.2 §2 AC1, §5, §8, §9, §10; contracts/cli.md §5/§6/§7).
import { createHash } from 'node:crypto';
import { chmod, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

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
  readonly body: string;
}

/** A `fetch` stub that records every request (URL, headers, body) and answers from `handler`. */
function stubFetch(handler: (request: Request) => Response | Promise<Response>): {
  readonly fetchImpl: typeof fetch;
  readonly calls: StubCall[];
} {
  const calls: StubCall[] = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    calls.push({ request, body: await request.clone().text() });
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
    // The SDK server base carries `/v1`; assert the operation path and the method.
    expect(
      new URL(call?.request.url ?? '').pathname.endsWith(`/projects/${project}/manifests`),
    ).toBe(true);
    expect(call?.request.headers.get('authorization')).toBe('Bearer dev-token-value');
    expect(call?.request.headers.get('content-type')).toBe('application/json');
    // 00 §9.1: the route requires an `Idempotency-Key` (openapi `required: true`); the
    // file's own digest makes a retry of the same manifest replay the stored 201.
    expect(call?.request.headers.get('idempotency-key')).toBe(
      createHash('sha256').update(text).digest('hex'),
    );
    expect(call?.body).toBe(text);
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
