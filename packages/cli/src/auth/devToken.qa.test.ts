import { mkdtemp, readdir, readFile, rm, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resolveConfig } from '../config.js';
import { main } from '../main.js';
import { redact, resetSecrets, type Io } from '../redact.js';

import { getToken } from './getToken.js';
import { tokenCachePath, writeCachedToken } from './tokenCache.js';

const now = Date.parse('2030-01-01T00:00:00Z');
const devToken = 'egt_independent_dev_credential';
const bearer = 'egt_independent_exchanged_bearer';
const projectId = '3f2504e0-4f89-41d3-9a0c-0305e82c3301';
const manifestId = '9b2f4c1e-2a3b-4c5d-8e9f-0a1b2c3d4e5f';
const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
const expiresAt = new Date(now + 30 * 60_000).toISOString();
const createArgv = [
  'runs',
  'create',
  '--manifest-id',
  manifestId,
  '--manifest-hash',
  `sha256:${'a'.repeat(64)}`,
  '--json',
  '--verbose',
];

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json', 'retry-after': '1' },
  });
}

function success(): Response {
  return jsonResponse(200, {
    token: bearer,
    expires_at: expiresAt,
    project_id: projectId,
    permission_set: [],
  });
}

function capture(): { io: Io; stdout: string[]; stderr: string[] } {
  const stdout: string[] = [];
  const stderr: string[] = [];
  return { io: { stdout: (s) => stdout.push(s), stderr: (s) => stderr.push(s) }, stdout, stderr };
}

function transport(handler: (request: Request) => Response | Promise<Response>): {
  fetchImpl: typeof fetch;
  calls: Request[];
} {
  const calls: Request[] = [];
  const fetchImpl: typeof fetch = (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    calls.push(request.clone());
    return Promise.resolve(handler(request));
  };
  return { fetchImpl, calls };
}

describe('dev-token exchange through CLI entry points', () => {
  let root: string;
  let env: Record<string, string | undefined>;

  beforeEach(async () => {
    resetSecrets();
    root = await mkdtemp(join(tmpdir(), 'evidgate-devtoken-qa-'));
    env = {
      EVIDGATE_API_URL: 'http://127.0.0.1:8080',
      EVIDGATE_PROJECT: projectId,
      EVIDGATE_ENV: 'dev',
      EVIDGATE_DEV_TOKEN: devToken,
      EVIDGATE_CACHE_DIR: join(root, 'cache'),
      EVIDGATE_SPOOL_DIR: join(root, 'spool'),
    };
  });

  afterEach(async () => {
    resetSecrets();
    await rm(root, { recursive: true, force: true });
  });

  it('sends only the exchanged bearer to runs create and persists no dev credential', async () => {
    const stub = transport((request) =>
      new URL(request.url).pathname === '/v1/ci/token'
        ? success()
        : jsonResponse(201, { run_id: runId, state: 'queued', unverified_ref: false }),
    );
    const out = capture();

    expect(await main(createArgv, env, out.io, { fetch: stub.fetchImpl, now: () => now })).toBe(0);
    expect(stub.calls).toHaveLength(2);
    expect(stub.calls[0]?.method).toBe('POST');
    expect(stub.calls[0]?.url).toBe('http://127.0.0.1:8080/v1/ci/token');
    expect(stub.calls[0]?.headers.get('authorization')).toBeNull();
    expect(await stub.calls[0]?.json()).toEqual({ ingest_token: devToken });
    expect(stub.calls[1]?.url).toBe(`http://127.0.0.1:8080/v1/projects/${projectId}/runs`);
    expect(stub.calls[1]?.headers.get('authorization')).toBe(`Bearer ${bearer}`);
    expect(await stub.calls[1]?.text()).not.toContain(devToken);
    const cachePath = tokenCachePath(resolveConfig({ env }));
    const text = await readFile(cachePath, 'utf8');
    expect(JSON.parse(text)).toEqual({ token: bearer, expires_at: expiresAt });
    expect(text).not.toContain(devToken);
    expect((await stat(cachePath)).mode & 0o777).toBe(0o600);
    expect(await readdir(root)).toEqual(['cache']);
    expect(await readdir(join(root, 'cache'))).toHaveLength(1);
    for (const secret of [devToken, bearer]) {
      expect(out.stdout.join('') + out.stderr.join('')).not.toContain(secret);
    }
  });

  it.each([60_001, 60_000, 0])(
    'honours the dev cache refresh boundary at %i ms',
    async (remaining) => {
      const config = resolveConfig({ env });
      await writeCachedToken(config, {
        token: 'cached_exchanged_bearer',
        expiresAt: new Date(now + remaining).toISOString(),
      });
      const stub = transport(() => success());
      const result = await getToken(config, { fetch: stub.fetchImpl, now: () => now });

      expect(result.source).toBe('dev');
      expect(result.token).toBe(remaining > 60_000 ? 'cached_exchanged_bearer' : bearer);
      expect(stub.calls).toHaveLength(remaining > 60_000 ? 0 : 1);
      expect(await readFile(tokenCachePath(config), 'utf8')).not.toContain(devToken);
    },
  );

  it.each([
    ['staging', 'http://127.0.0.1:8080'],
    ['prod', 'http://127.0.0.1:8080'],
    [undefined, 'http://127.0.0.1:8080'],
    ['test', 'http://127.0.0.1:8080'],
    ['dev', 'https://api.example.test'],
    ['dev', 'https://localhost.example.test'],
    ['dev', 'https://127.0.0.1.example.test:8080'],
    ['dev', 'https://[::ffff:127.0.0.1]:8080'],
  ])('refuses env=%s url=%s before trusting even a fresh cache', async (environment, apiUrl) => {
    env.EVIDGATE_ENV = environment;
    env.EVIDGATE_API_URL = apiUrl;
    await writeCachedToken(resolveConfig({ env }), { token: bearer, expiresAt });
    const stub = transport(() => success());
    const out = capture();

    expect(
      await main(['config', 'check', '--verbose'], env, out.io, {
        fetch: stub.fetchImpl,
        now: () => now,
      }),
    ).toBe(3);
    expect(out.stderr.join('')).toContain('evidgate: error dev_token_refused:');
    expect(out.stdout).toEqual([]);
    expect(stub.calls).toHaveLength(0);
    for (const secret of [devToken, bearer]) {
      expect(out.stderr.join('')).not.toContain(secret);
    }
  });

  it.each([
    [401, 'token_invalid', 1],
    [403, 'binding_pending', 1],
    [409, 'binding_conflict', 1],
    [500, 'internal_error', 1],
    [502, 'upstream_failed', 1],
    [503, 'unavailable', 2],
    [504, 'upstream_timeout', 1],
    [429, 'rate_limited', 2],
  ])(
    'exchange HTTP %i exits 3 with %s and exactly %i request(s)',
    async (status, code, attempts) => {
      const stub = transport(() =>
        jsonResponse(status, {
          error: {
            code,
            message: `refused ${devToken}\n::error::no`,
            request_id: `req-${devToken}`,
            retryable: true,
            details: [{ reason: devToken }],
          },
        }),
      );
      const out = capture();
      const sleeps: number[] = [];

      expect(
        await main(['config', 'check', '--json', '--verbose'], env, out.io, {
          fetch: stub.fetchImpl,
          now: () => now,
          random: () => 0,
          sleep: (ms) => {
            sleeps.push(ms);
            return Promise.resolve();
          },
        }),
      ).toBe(3);
      expect(stub.calls).toHaveLength(attempts);
      expect(sleeps).toHaveLength(attempts - 1);
      expect(out.stdout).toEqual([]);
      const error = out.stderr.join('');
      expect(error).toContain(`evidgate: error ${code}:`);
      expect(error).not.toContain(devToken);
      expect(error).not.toContain('evidgate: detail:');
      expect(
        error
          .trimEnd()
          .split('\n')
          .every((line) => line.startsWith('evidgate: ')),
      ).toBe(true);
      expect(await readdir(root)).toEqual([]);
    },
  );

  it.each([401, 503])('does not echo an unusable HTTP %i response body', async (status) => {
    const stub = transport(() => new Response(devToken, { status }));
    const out = capture();

    expect(
      await main(['config', 'check', '--verbose'], env, out.io, {
        fetch: stub.fetchImpl,
        now: () => now,
        random: () => 0,
        sleep: async () => {},
      }),
    ).toBe(3);
    expect(stub.calls).toHaveLength(status === 401 ? 1 : 2);
    expect(out.stderr.join('')).toContain('evidgate: error exchange_failed:');
    expect(out.stdout.join('') + out.stderr.join('')).not.toContain(devToken);
    expect(await readdir(root)).toEqual([]);
  });

  it('rejects malformed success without caching or printing either credential', async () => {
    const stub = transport(() => jsonResponse(200, { token: bearer, expires_at: 'invalid' }));
    const out = capture();

    expect(
      await main(['config', 'check', '--verbose'], env, out.io, {
        fetch: stub.fetchImpl,
        now: () => now,
      }),
    ).toBe(3);
    expect(stub.calls).toHaveLength(1);
    expect(out.stderr.join('')).toContain('evidgate: error exchange_failed:');
    for (const secret of [devToken, bearer]) {
      expect(out.stdout.join('') + out.stderr.join('')).not.toContain(secret);
      expect(redact(secret)).toBe('[REDACTED]');
    }
    expect(await readdir(root)).toEqual([]);
  });

  it('does not retry or echo credentials from an exchange transport exception', async () => {
    const stub = transport(() => {
      throw new Error(`cannot send ${devToken}`);
    });
    const out = capture();

    expect(
      await main(['config', 'check', '--verbose'], env, out.io, {
        fetch: stub.fetchImpl,
        now: () => now,
      }),
    ).toBe(3);
    expect(stub.calls).toHaveLength(1);
    expect(out.stderr.join('')).toContain('evidgate: error transport:');
    expect(out.stdout.join('') + out.stderr.join('')).not.toContain(devToken);
    expect(await readdir(root)).toEqual([]);
  });

  it('ends a project 401 without replaying the command or its dev exchange', async () => {
    const stub = transport((request) =>
      new URL(request.url).pathname === '/v1/ci/token'
        ? success()
        : jsonResponse(401, {
            error: {
              code: 'token_expired',
              message: `expired ${bearer}`,
              request_id: 'req-401',
              retryable: false,
            },
          }),
    );
    const out = capture();

    expect(await main(createArgv, env, out.io, { fetch: stub.fetchImpl, now: () => now })).toBe(3);
    expect(stub.calls).toHaveLength(2);
    expect(out.stdout).toEqual([]);
    expect(out.stderr.join('')).toContain('evidgate: error token_expired:');
    for (const secret of [devToken, bearer]) {
      expect(out.stderr.join('')).not.toContain(secret);
    }
    expect(await readdir(join(root, 'cache'))).toEqual([]);
  });
});
