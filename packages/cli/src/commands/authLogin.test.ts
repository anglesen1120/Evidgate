import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { readCachedToken, tokenCachePath, writeCachedToken } from '../auth/tokenCache.js';
import { resolveConfig } from '../config.js';
import { main } from '../main.js';
import { resetSecrets, type Io } from '../redact.js';

/** A valid `project_id` (openapi `format: uuid`); the exchange refuses anything else (S1). */
const projectId = '3f2504e0-4f89-41d3-9a0c-0305e82c3301';

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

function envelope(code: string): unknown {
  return {
    error: { code, message: `server says ${code}`, request_id: 'req-1', retryable: false },
  };
}

function successBody(): unknown {
  return {
    token: 'egt_ci_token',
    expires_at: '2030-01-01T00:00:00Z',
    project_id: projectId,
    permission_set: [],
  };
}

// 04.2.1-T1.1 / AC1, AC2, AC4, AC5, AC6, AC7: `auth login` and `auth logout` on the
// binary — the exchange, the cache write, the `--json`/`--github-env` output rules and
// every failure path (contracts/cli.md §5/§6).
describe('auth login / auth logout', () => {
  const cacheDirs: string[] = [];

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await Promise.all(cacheDirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  async function tempCacheDir(): Promise<string> {
    const dir = await mkdtemp(join(tmpdir(), 'evidgate-cli-'));
    cacheDirs.push(dir);
    return dir;
  }

  async function oidcEnv(extra: Record<string, string> = {}) {
    const cacheDir = await tempCacheDir();
    return {
      EVIDGATE_API_URL: 'https://api.evidgate.test',
      EVIDGATE_PROJECT: 'proj-1',
      EVIDGATE_CACHE_DIR: cacheDir,
      EVIDGATE_OIDC_TOKEN: 'jwt-value',
      ...extra,
    };
  }

  it('oidc_env_exchanges_and_caches', async () => {
    const env = await oidcEnv();
    const { fetchImpl, calls } = stubFetch(() => jsonResponse(200, successBody()));
    const { io, stdout, stderr } = capture();

    await expect(main(['auth', 'login'], env, io, { fetch: fetchImpl })).resolves.toBe(0);

    expect(stdout.join('')).toBe(`ok expires_at=2030-01-01T00:00:00Z project_id=${projectId}\n`);
    expect(stderr.join('')).toBe('');
    expect(calls[0]?.request.headers.get('authorization')).toBe('Bearer jwt-value');
    const cached = JSON.parse(await readFile(tokenCachePath(resolveConfig({ env })), 'utf8')) as {
      token: string;
      expires_at: string;
    };
    expect(cached).toEqual({ token: 'egt_ci_token', expires_at: '2030-01-01T00:00:00Z' });
  });

  it('ingest_token_not_persisted', async () => {
    const cacheDir = await tempCacheDir();
    const ingestToken = 'egt_ingest_secret_value';
    const env = {
      EVIDGATE_API_URL: 'https://api.evidgate.test',
      EVIDGATE_PROJECT: 'proj-1',
      EVIDGATE_CACHE_DIR: cacheDir,
      EVIDGATE_INGEST_TOKEN: ingestToken,
    };
    const { fetchImpl, calls } = stubFetch(() => jsonResponse(200, successBody()));
    const { io, stdout, stderr } = capture();

    await expect(main(['auth', 'login'], env, io, { fetch: fetchImpl })).resolves.toBe(0);

    // The ingest credential travels in the body, never as a bearer.
    expect(calls[0]?.request.headers.get('authorization')).toBeNull();
    expect(JSON.parse(calls[0]?.body ?? '{}')).toEqual({ ingest_token: ingestToken });
    // The plaintext ingest token is nowhere on disk or in either stream.
    const cacheText = await readFile(tokenCachePath(resolveConfig({ env })), 'utf8');
    expect(cacheText).not.toContain(ingestToken);
    expect(stdout.join('')).not.toContain(ingestToken);
    expect(stderr.join('')).not.toContain(ingestToken);
  });

  it('auth_failure_exit3', async () => {
    for (const status of [401, 403]) {
      const env = await oidcEnv();
      const config = resolveConfig({ env });
      // A pre-existing cache entry must be purged so the next run re-authenticates.
      await writeCachedToken(config, {
        token: 'stale-token-value',
        expiresAt: '2030-01-01T00:00:00Z',
      });
      const { fetchImpl, calls } = stubFetch(() => jsonResponse(status, envelope('token_invalid')));
      const { io, stderr } = capture();

      await expect(main(['auth', 'login'], env, io, { fetch: fetchImpl })).resolves.toBe(3);

      const text = stderr.join('');
      expect(text).toContain('evidgate: error token_invalid:');
      expect(text).toContain('evidgate: request_id: req-1');
      expect(calls).toHaveLength(1);
      await expect(readCachedToken(config)).resolves.toBeUndefined();
    }
  });

  it('missing_config_exit3', async () => {
    const empty = capture();
    await expect(main(['auth', 'login'], {}, empty.io)).resolves.toBe(3);
    const text = empty.stderr.join('');
    expect(text).toContain('evidgate: error missing_config:');
    expect(text).toContain('EVIDGATE_API_URL (or --api-url)');
    expect(text).toContain('EVIDGATE_PROJECT (or --project)');
    expect(text).toContain('EVIDGATE_OIDC_TOKEN');
    expect(text).toContain('EVIDGATE_INGEST_TOKEN');

    // A dev source is not exchangeable: the exchange names the sources it needs.
    const dev = capture();
    const devEnv = {
      EVIDGATE_API_URL: 'http://127.0.0.1:8080',
      EVIDGATE_PROJECT: 'proj-1',
      EVIDGATE_ENV: 'dev',
      EVIDGATE_DEV_TOKEN: 'dev-token-value',
    };
    await expect(main(['auth', 'login'], devEnv, dev.io)).resolves.toBe(3);
    expect(dev.stderr.join('')).toContain('an exchange token source');
  });

  it('login_json_never_contains_token', async () => {
    const env = await oidcEnv();
    const { fetchImpl } = stubFetch(() => jsonResponse(200, successBody()));
    const { io, stdout, stderr } = capture();

    await expect(main(['auth', 'login', '--json'], env, io, { fetch: fetchImpl })).resolves.toBe(0);

    expect(JSON.parse(stdout.join(''))).toEqual({
      ok: true,
      expires_at: '2030-01-01T00:00:00Z',
      project_id: projectId,
    });
    expect(stdout.join('')).not.toContain('egt_ci_token');
    expect(stderr.join('')).not.toContain('egt_ci_token');
  });

  it('github_env_masked', async () => {
    const cacheDir = await tempCacheDir();
    const githubEnvPath = join(cacheDir, 'github-env');
    await writeFile(githubEnvPath, '', 'utf8');
    const env = {
      EVIDGATE_API_URL: 'https://api.evidgate.test',
      EVIDGATE_PROJECT: 'proj-1',
      EVIDGATE_CACHE_DIR: cacheDir,
      EVIDGATE_OIDC_TOKEN: 'jwt-value',
      GITHUB_ENV: githubEnvPath,
    };
    const { fetchImpl } = stubFetch(() => jsonResponse(200, successBody()));
    const { io, stdout, stderr } = capture();

    await expect(
      main(['auth', 'login', '--github-env'], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(0);

    const out = stdout.join('');
    // The mask line is written to the RAW stdout, before the heredoc is appended.
    expect(out.split('\n')[0]).toBe('::add-mask::egt_ci_token');
    const file = await readFile(githubEnvPath, 'utf8');
    const heredoc = /^EVIDGATE_CI_TOKEN<<(ghadelimiter_[0-9a-f-]+)\negt_ci_token\n\1\n$/m;
    expect(heredoc.test(file)).toBe(true);
    // The token appears nowhere else in either stream.
    expect(out.replace('::add-mask::egt_ci_token', '')).not.toContain('egt_ci_token');
    expect(stderr.join('')).not.toContain('egt_ci_token');
  });

  it('github_env_unset_exit3', async () => {
    const env = await oidcEnv();
    const { fetchImpl, calls } = stubFetch(() => jsonResponse(200, successBody()));
    const { io, stderr } = capture();

    await expect(
      main(['auth', 'login', '--github-env'], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: error missing_config:');
    expect(stderr.join('')).toContain('GITHUB_ENV');
    // The guard runs before the exchange and the cache write (D3).
    expect(calls).toHaveLength(0);
    await expect(readCachedToken(resolveConfig({ env }))).resolves.toBeUndefined();
  });

  it('purge_failure_surfaces_server_code', async () => {
    const env = await oidcEnv();
    const config = resolveConfig({ env });
    // A directory at the cache path makes the purge fail (EISDIR); the server's code
    // must still be the one reported (C2).
    await mkdir(tokenCachePath(config), { recursive: true });
    const { fetchImpl } = stubFetch(() => jsonResponse(401, envelope('token_invalid')));
    const { io, stderr } = capture();

    await expect(main(['auth', 'login'], env, io, { fetch: fetchImpl })).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain('evidgate: error token_invalid:');
    expect(text).not.toContain('token_cache_error');
  });

  it('binding_errors_exit3', async () => {
    const cases = [
      { status: 403, code: 'binding_pending', hint: 'EVIDGATE_BINDING_CHALLENGE' },
      { status: 409, code: 'binding_conflict', hint: 'disable that binding first' },
    ];
    for (const { status, code, hint } of cases) {
      const env = await oidcEnv();
      const { fetchImpl, calls } = stubFetch(() => jsonResponse(status, envelope(code)));
      const { io, stderr } = capture();

      await expect(main(['auth', 'login'], env, io, { fetch: fetchImpl })).resolves.toBe(3);

      const text = stderr.join('');
      expect(text).toContain(`evidgate: error ${code}:`);
      expect(text).toContain('evidgate: request_id: req-1');
      expect(text).toContain('evidgate: hint:');
      expect(text).toContain(hint);
      expect(calls).toHaveLength(1);
    }
  });

  it('transport_error_exit3', async () => {
    const env = await oidcEnv();
    const fetchImpl: typeof fetch = () => Promise.reject(new TypeError('fetch failed'));
    const { io, stderr } = capture();

    await expect(main(['auth', 'login'], env, io, { fetch: fetchImpl })).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: error transport:');
  });

  it('logout_purges_cache', async () => {
    const cacheDir = await tempCacheDir();
    const env = {
      EVIDGATE_API_URL: 'https://api.evidgate.test',
      EVIDGATE_PROJECT: 'proj-1',
      EVIDGATE_CACHE_DIR: cacheDir,
    };
    const config = resolveConfig({ env });
    await writeCachedToken(config, { token: 'egt_ci_token', expiresAt: '2030-01-01T00:00:00Z' });
    await expect(readCachedToken(config)).resolves.toEqual({
      token: 'egt_ci_token',
      expiresAt: '2030-01-01T00:00:00Z',
    });

    const text = capture();
    await expect(main(['auth', 'logout'], env, text.io)).resolves.toBe(0);
    expect(text.stdout.join('')).toBe('ok\n');
    await expect(readCachedToken(config)).resolves.toBeUndefined();

    const json = capture();
    await expect(main(['auth', 'logout', '--json'], env, json.io)).resolves.toBe(0);
    expect(json.stdout.join('')).toBe('{"ok":true}\n');
  });
});
