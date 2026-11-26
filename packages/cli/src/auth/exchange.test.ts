import { once } from 'node:events';
import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import { createServer, type Server } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  captureCliErrorAsync as captureCliError,
  jsonResponse,
  stubRequestFetch as stubFetch,
} from '../commands/cliSpec.testing.js';
import { resolveConfig } from '../config.js';
import { resetSecrets } from '../redact.js';

import { ExchangeAuthError, exchangeAndPurge, exchangeToken } from './exchange.js';
import { readCachedToken, tokenCachePath, writeCachedToken } from './tokenCache.js';

/** A valid `project_id` (openapi `format: uuid`); the exchange refuses anything else (S1). */
const projectId = '3f2504e0-4f89-41d3-9a0c-0305e82c3301';

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

async function listen(server: Server): Promise<number> {
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address();
  if (address === null || typeof address === 'string') {
    throw new Error('expected a TCP address');
  }
  return address.port;
}

async function close(server: Server): Promise<void> {
  if (!server.listening) {
    return;
  }
  const closed = once(server, 'close');
  server.close();
  server.closeAllConnections();
  await closed;
}

describe('exchangeToken', () => {
  const cacheDirs: string[] = [];
  const servers: Server[] = [];

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await Promise.all(servers.splice(0).map((server) => close(server)));
    await Promise.all(cacheDirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  async function tempCacheDir(): Promise<string> {
    const dir = await mkdtemp(join(tmpdir(), 'evidgate-cli-'));
    cacheDirs.push(dir);
    return dir;
  }

  async function ingestConfig(extra: Record<string, string> = {}) {
    const cacheDir = await tempCacheDir();
    return resolveConfig({
      env: {
        EVIDGATE_API_URL: 'https://api.evidgate.test',
        EVIDGATE_PROJECT: 'proj-1',
        EVIDGATE_CACHE_DIR: cacheDir,
        EVIDGATE_INGEST_TOKEN: 'ingest-value',
        ...extra,
      },
    });
  }

  const retiredCredentials = {
    EVIDGATE_OIDC_TOKEN: 'retired-jwt',
    ACTIONS_ID_TOKEN_REQUEST_URL: 'https://actions.example/oidc',
    ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'request-token-value',
    EVIDGATE_BINDING_CHALLENGE: 'unused-challenge',
  };

  it('sends only the project ingest token to Evidgate, even with provider credentials', async () => {
    const config = await ingestConfig(retiredCredentials);
    const { fetchImpl, calls } = stubFetch(() => jsonResponse(200, successBody()));

    const result = await exchangeToken(config, { fetch: fetchImpl });

    expect(result.token).toBe('egt_ci_token');
    expect(calls.map(({ request }) => request.url)).toEqual([
      'https://api.evidgate.test/v1/ci/token',
    ]);
    expect(calls[0]?.request.headers.get('authorization')).toBeNull();
    expect(JSON.parse(calls[0]?.body ?? '{}')).toEqual({ ingest_token: 'ingest-value' });
  });

  it('refuses provider credentials without opening a request when the ingest token is missing', async () => {
    const config = await ingestConfig({ ...retiredCredentials, EVIDGATE_INGEST_TOKEN: '' });
    const { fetchImpl, calls } = stubFetch(() => jsonResponse(200, successBody()));

    await expect(exchangeToken(config, { fetch: fetchImpl })).rejects.toMatchObject({
      code: 'missing_config',
    });
    expect(calls).toHaveLength(0);
  });

  it('propagates refusal of an unknown ingest token without a provider fallback', async () => {
    const config = await ingestConfig({ ...retiredCredentials, EVIDGATE_INGEST_TOKEN: 'unknown' });
    const { fetchImpl, calls } = stubFetch(() => jsonResponse(401, envelope('token_invalid')));

    await expect(exchangeToken(config, { fetch: fetchImpl })).rejects.toMatchObject({
      code: 'token_invalid',
      status: 401,
    });
    expect(calls.map(({ request }) => request.url)).toEqual([
      'https://api.evidgate.test/v1/ci/token',
    ]);
    expect(JSON.parse(calls[0]?.body ?? '{}')).toEqual({ ingest_token: 'unknown' });
  });

  it('retry_after_honoured_once', async () => {
    const config = await ingestConfig();

    // 429 then 200: one sleep of exactly `Retry-After` seconds, then success.
    let attempt = 0;
    const retryStub = stubFetch(() => {
      attempt += 1;
      return attempt === 1
        ? jsonResponse(429, envelope('rate_limited'), { 'retry-after': '2' })
        : jsonResponse(200, successBody());
    });
    const sleep = vi.fn(() => Promise.resolve());
    const result = await exchangeToken(config, { fetch: retryStub.fetchImpl, sleep });
    expect(result.token).toBe('egt_ci_token');
    expect(sleep).toHaveBeenCalledTimes(1);
    expect(sleep).toHaveBeenCalledWith(2000);
    expect(retryStub.calls).toHaveLength(2);

    // 429 twice: one sleep, then the server code is echoed and nothing is retried again.
    const failStub = stubFetch(() =>
      jsonResponse(429, envelope('rate_limited'), { 'retry-after': '1' }),
    );
    const sleepAgain = vi.fn(() => Promise.resolve());
    const error = await captureCliError(() =>
      exchangeToken(config, { fetch: failStub.fetchImpl, sleep: sleepAgain }),
    );
    expect(error.code).toBe('rate_limited');
    expect(sleepAgain).toHaveBeenCalledTimes(1);
    expect(failStub.calls).toHaveLength(2);
  });

  it('retry_after_clamped_and_http_date', async () => {
    const config = await ingestConfig();
    const retryOnce = (headers: Record<string, string>) => {
      let attempt = 0;
      return stubFetch(() => {
        attempt += 1;
        return attempt === 1
          ? jsonResponse(429, envelope('rate_limited'), headers)
          : jsonResponse(200, successBody());
      });
    };

    // A delta-seconds value above the ceiling is clamped to 60 s (contracts/cli.md §5).
    const clamped = retryOnce({ 'retry-after': '9999' });
    const sleepClamped = vi.fn(() => Promise.resolve());
    await exchangeToken(config, { fetch: clamped.fetchImpl, sleep: sleepClamped });
    expect(sleepClamped).toHaveBeenCalledWith(60_000);

    // An HTTP-date is honoured as a delta from now.
    const dated = retryOnce({ 'retry-after': new Date(Date.now() + 5_000).toUTCString() });
    const sleepDated = vi.fn<(ms: number) => Promise<void>>(() => Promise.resolve());
    await exchangeToken(config, { fetch: dated.fetchImpl, sleep: sleepDated });
    const waited = sleepDated.mock.calls[0]?.[0] ?? 0;
    expect(waited).toBeGreaterThan(3_000);
    expect(waited).toBeLessThanOrEqual(5_000);

    // A missing header falls back to 1 s.
    const bare = retryOnce({});
    const sleepBare = vi.fn(() => Promise.resolve());
    await exchangeToken(config, { fetch: bare.fetchImpl, sleep: sleepBare });
    expect(sleepBare).toHaveBeenCalledWith(1_000);
  });

  it('falls back to 1 s for a malformed Retry-After', async () => {
    const config = await ingestConfig();
    for (const value of ['-5', '1.5', 'soon 5']) {
      let attempt = 0;
      const { fetchImpl } = stubFetch(() => {
        attempt += 1;
        return attempt === 1
          ? jsonResponse(429, envelope('rate_limited'), { 'retry-after': value })
          : jsonResponse(200, successBody());
      });
      const sleep = vi.fn(() => Promise.resolve());

      await exchangeToken(config, { fetch: fetchImpl, sleep });

      expect(sleep).toHaveBeenCalledWith(1_000);
    }
  });

  it('rejects a 200 body whose token carries whitespace', async () => {
    const config = await ingestConfig();
    const { fetchImpl } = stubFetch(() =>
      jsonResponse(200, {
        token: 'egt_ ci',
        expires_at: '2030-01-01T00:00:00Z',
        project_id: projectId,
      }),
    );

    const error = await captureCliError(() => exchangeToken(config, { fetch: fetchImpl }));

    expect(error.code).toBe('exchange_failed');
    expect(error.message).not.toContain('egt_ ci');
  });

  it('rejects a 200 body whose expires_at is not RFC 3339', async () => {
    const config = await ingestConfig();
    const injected = [
      '2030-01-01T00:00:00Z\n(see https://evil.example)',
      '2030-01-01T00:00:00Z (see https://evil.example)',
    ];
    for (const expiresAt of injected) {
      const { fetchImpl } = stubFetch(() =>
        jsonResponse(200, { token: 'egt_ci_token', expires_at: expiresAt, project_id: projectId }),
      );

      const error = await captureCliError(() => exchangeToken(config, { fetch: fetchImpl }));

      expect(error.code).toBe('exchange_failed');
      expect(error.message).not.toContain('evil.example');
      expect(error.message).not.toContain(expiresAt);
    }
  });

  it('rejects a 200 body whose project_id is not a UUID', async () => {
    const config = await ingestConfig();
    const { fetchImpl } = stubFetch(() =>
      jsonResponse(200, {
        token: 'egt_ci_token',
        expires_at: '2030-01-01T00:00:00Z',
        project_id: 'proj-1',
      }),
    );

    const error = await captureCliError(() => exchangeToken(config, { fetch: fetchImpl }));

    expect(error.code).toBe('exchange_failed');
    expect(error.message).not.toContain('proj-1');
  });

  it('redirect_refused', async () => {
    const cacheDir = await tempCacheDir();
    const redirector = createServer((_request, response) => {
      response.writeHead(302, { location: 'https://elsewhere.example/' }).end();
    });
    servers.push(redirector);
    const port = await listen(redirector);
    const config = resolveConfig({
      env: {
        EVIDGATE_API_URL: `http://127.0.0.1:${port}`,
        EVIDGATE_PROJECT: 'proj-1',
        EVIDGATE_CACHE_DIR: cacheDir,
        EVIDGATE_INGEST_TOKEN: 'ingest-value',
      },
    });

    const error = await captureCliError(() => exchangeToken(config));

    expect(error.code).toBe('transport');
  });

  it('malformed_server_code_not_echoed', async () => {
    const config = await ingestConfig();
    const { fetchImpl } = stubFetch(() =>
      jsonResponse(409, {
        error: { code: 'Bad Code!', message: 'nope', request_id: 'req-9', retryable: false },
      }),
    );

    const error = await captureCliError(() => exchangeToken(config, { fetch: fetchImpl }));

    expect(error.code).toBe('exchange_failed');
    expect(error.message).not.toContain('Bad Code!');
    expect(error.requestId).toBe('req-9');
  });

  it('raises ExchangeAuthError with the status for 401 and 403', async () => {
    const config = await ingestConfig();
    for (const status of [401, 403]) {
      const { fetchImpl } = stubFetch(() => jsonResponse(status, envelope('token_invalid')));
      const error = await captureCliError(() => exchangeToken(config, { fetch: fetchImpl }));
      expect(error).toBeInstanceOf(ExchangeAuthError);
      expect((error as ExchangeAuthError).status).toBe(status);
      expect(error.code).toBe('token_invalid');
    }
  });

  it('refuses to start an exchange whose deadline has passed', async () => {
    // `attemptSignal` falls back to the plain per-request timeout once nothing is left, so a
    // refresh that begins after its deadline must not open a socket at all: a hung one would
    // keep the process alive long after the caller has decided its exit code.
    const config = await ingestConfig();
    const stub = stubFetch(() => jsonResponse(200, successBody()));

    const error = await captureCliError(() =>
      exchangeToken(config, { fetch: stub.fetchImpl, deadlineAt: 1_000, now: () => 2_000 }),
    );

    expect(error.code).toBe('request_failed');
    expect(stub.calls).toHaveLength(0);
  });

  it('exchanges when the deadline has time left', async () => {
    const config = await ingestConfig();
    const stub = stubFetch(() => jsonResponse(200, successBody()));

    const result = await exchangeToken(config, {
      fetch: stub.fetchImpl,
      deadlineAt: 5_000,
      now: () => 2_000,
    });

    expect(result.token).toBe('egt_ci_token');
    expect(stub.calls).toHaveLength(1);
  });

  it('exchangeAndPurge purges the cache on 401', async () => {
    const config = await ingestConfig();
    await writeCachedToken(config, { token: 'stale-token', expiresAt: '2030-01-01T00:00:00Z' });
    const { fetchImpl } = stubFetch(() => jsonResponse(401, envelope('token_invalid')));

    await expect(exchangeAndPurge(config, { fetch: fetchImpl })).rejects.toMatchObject({
      code: 'token_invalid',
    });
    await expect(readCachedToken(config)).resolves.toBeUndefined();
  });

  it('exchangeAndPurge keeps the cache on a non-auth failure', async () => {
    const config = await ingestConfig();
    await writeCachedToken(config, { token: 'stale-token', expiresAt: '2030-01-01T00:00:00Z' });
    const { fetchImpl } = stubFetch(() => jsonResponse(500, envelope('server_error')));

    await expect(exchangeAndPurge(config, { fetch: fetchImpl })).rejects.toMatchObject({
      code: 'server_error',
    });
    await expect(readCachedToken(config)).resolves.toEqual({
      token: 'stale-token',
      expiresAt: '2030-01-01T00:00:00Z',
    });
  });

  it('exchangeAndPurge swallows a purge failure and surfaces the server code', async () => {
    const config = await ingestConfig();
    // A directory at the cache path makes `deleteCachedToken` reject (EISDIR).
    await mkdir(tokenCachePath(config), { recursive: true });
    const { fetchImpl } = stubFetch(() => jsonResponse(401, envelope('token_invalid')));

    const error = await captureCliError(() => exchangeAndPurge(config, { fetch: fetchImpl }));

    expect(error).toBeInstanceOf(ExchangeAuthError);
    expect(error.code).toBe('token_invalid');
  });

  it('exchanges against a real HTTP server (integration)', async () => {
    const cacheDir = await tempCacheDir();
    const received: Array<{
      method: string | undefined;
      url: string | undefined;
      auth: string | undefined;
      body: string;
    }> = [];
    const server = createServer((request, response) => {
      let body = '';
      request.on('data', (chunk: Buffer) => {
        body += chunk.toString('utf8');
      });
      request.on('end', () => {
        received.push({
          method: request.method,
          url: request.url,
          auth: request.headers.authorization,
          body,
        });
        response.writeHead(200, { 'content-type': 'application/json' });
        response.end(JSON.stringify(successBody()));
      });
    });
    servers.push(server);
    const port = await listen(server);
    const config = resolveConfig({
      env: {
        EVIDGATE_API_URL: `http://127.0.0.1:${port}`,
        EVIDGATE_PROJECT: 'proj-1',
        EVIDGATE_CACHE_DIR: cacheDir,
        EVIDGATE_INGEST_TOKEN: 'ingest-value',
      },
    });

    const result = await exchangeToken(config);

    expect(result.token).toBe('egt_ci_token');
    expect(received).toHaveLength(1);
    expect(received[0]?.method).toBe('POST');
    expect(received[0]?.url).toBe('/v1/ci/token');
    expect(received[0]?.auth).toBeUndefined();
    expect(JSON.parse(received[0]?.body ?? '{}')).toEqual({ ingest_token: 'ingest-value' });
  });
});
