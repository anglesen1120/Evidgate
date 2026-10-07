import { once } from 'node:events';
import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import { createServer, type Server } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { resolveConfig } from '../config.js';
import { CliError } from '../errors.js';
import { resetSecrets } from '../redact.js';

import { ExchangeAuthError, exchangeAndPurge, exchangeToken } from './exchange.js';
import { readCachedToken, tokenCachePath, writeCachedToken } from './tokenCache.js';

/** A valid `project_id` (openapi `format: uuid`); the exchange refuses anything else (S1). */
const projectId = '3f2504e0-4f89-41d3-9a0c-0305e82c3301';

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

async function captureCliError(run: () => Promise<unknown>): Promise<CliError> {
  try {
    await run();
  } catch (error) {
    if (error instanceof CliError) {
      return error;
    }
    throw error;
  }
  throw new Error('expected the call to throw a CliError');
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

// 04.2.1-T1.1 / AC1, AC2, AC4, AC7: the exchange itself — source resolution, the
// GitHub Actions ID-token fetch, the `binding_challenge` body, the 429 back-off, the
// refused redirect and the envelope mapping (contracts/cli.md §5).
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

  async function oidcConfig(extra: Record<string, string> = {}) {
    const cacheDir = await tempCacheDir();
    return resolveConfig({
      env: {
        EVIDGATE_API_URL: 'https://api.evidgate.test',
        EVIDGATE_PROJECT: 'proj-1',
        EVIDGATE_CACHE_DIR: cacheDir,
        EVIDGATE_OIDC_TOKEN: 'jwt-value',
        ...extra,
      },
    });
  }

  it('actions_id_token_fetch_exchanges', async () => {
    const cacheDir = await tempCacheDir();
    const idTokenRequests: Array<{ url: string; auth: string | null }> = [];
    const { fetchImpl, calls } = stubFetch((request) => {
      if (request.url.startsWith('https://actions.example/oidc')) {
        idTokenRequests.push({ url: request.url, auth: request.headers.get('authorization') });
        return jsonResponse(200, { value: 'actions-id-token-value' });
      }
      return jsonResponse(200, successBody());
    });
    const config = resolveConfig({
      env: {
        EVIDGATE_API_URL: 'https://api.evidgate.test',
        EVIDGATE_PROJECT: 'proj-1',
        EVIDGATE_CACHE_DIR: cacheDir,
        ACTIONS_ID_TOKEN_REQUEST_URL: 'https://actions.example/oidc',
        ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'request-token-value',
      },
    });

    const result = await exchangeToken(config, { fetch: fetchImpl });

    expect(result.source).toBe('actions-id-token');
    expect(result.token).toBe('egt_ci_token');
    expect(idTokenRequests).toHaveLength(1);
    const idUrl = new URL(idTokenRequests[0]?.url ?? '');
    expect(idUrl.searchParams.get('audience')).toBe('evidgate');
    expect(idTokenRequests[0]?.auth).toBe('bearer request-token-value');
    const exchange = calls.find((call) => call.request.url.endsWith('/ci/token'));
    expect(exchange?.request.headers.get('authorization')).toBe('Bearer actions-id-token-value');
  });

  it('retry_after_honoured_once', async () => {
    const config = await oidcConfig();

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
    const config = await oidcConfig();
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
    const config = await oidcConfig();
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
    const config = await oidcConfig();
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
    const config = await oidcConfig();
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
    const config = await oidcConfig();
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

  it('omits binding_challenge when the env value is empty', async () => {
    const config = await oidcConfig({ EVIDGATE_BINDING_CHALLENGE: '' });
    const { fetchImpl, calls } = stubFetch(() => jsonResponse(200, successBody()));

    await exchangeToken(config, { fetch: fetchImpl });

    expect(JSON.parse(calls[0]?.body ?? '{}')).toEqual({});
  });

  it('binding_challenge_sent', async () => {
    const withChallenge = stubFetch(() => jsonResponse(200, successBody()));
    const configWith = await oidcConfig({ EVIDGATE_BINDING_CHALLENGE: 'challenge-value' });
    await exchangeToken(configWith, { fetch: withChallenge.fetchImpl });
    expect(JSON.parse(withChallenge.calls[0]?.body ?? '{}')).toEqual({
      binding_challenge: 'challenge-value',
    });

    const without = stubFetch(() => jsonResponse(200, successBody()));
    const configWithout = await oidcConfig();
    await exchangeToken(configWithout, { fetch: without.fetchImpl });
    expect(JSON.parse(without.calls[0]?.body ?? '{}')).toEqual({});
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
        EVIDGATE_OIDC_TOKEN: 'jwt-value',
      },
    });

    const error = await captureCliError(() => exchangeToken(config));

    expect(error.code).toBe('transport');
  });

  it('malformed_server_code_not_echoed', async () => {
    const config = await oidcConfig();
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
    const config = await oidcConfig();
    for (const status of [401, 403]) {
      const { fetchImpl } = stubFetch(() => jsonResponse(status, envelope('token_invalid')));
      const error = await captureCliError(() => exchangeToken(config, { fetch: fetchImpl }));
      expect(error).toBeInstanceOf(ExchangeAuthError);
      expect((error as ExchangeAuthError).status).toBe(status);
      expect(error.code).toBe('token_invalid');
    }
  });

  it('rejects an actions id-token URL with credentials or a fragment', async () => {
    const cacheDir = await tempCacheDir();
    const urls = [
      'https://user:secret@actions.example/oidc',
      'https://actions.example/oidc#fragment',
    ];
    for (const requestUrl of urls) {
      const config = resolveConfig({
        env: {
          EVIDGATE_API_URL: 'https://api.evidgate.test',
          EVIDGATE_PROJECT: 'proj-1',
          EVIDGATE_CACHE_DIR: cacheDir,
          ACTIONS_ID_TOKEN_REQUEST_URL: requestUrl,
          ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'request-token-value',
        },
      });
      const { fetchImpl, calls } = stubFetch(() => jsonResponse(200, successBody()));

      const error = await captureCliError(() => exchangeToken(config, { fetch: fetchImpl }));

      expect(error.code).toBe('invalid_config');
      expect(error.message).not.toContain('secret');
      expect(error.message).not.toContain('actions.example');
      expect(calls).toHaveLength(0);
    }
  });

  it('cancels the ID-token response body on a non-ok reply', async () => {
    const cacheDir = await tempCacheDir();
    let cancelled = false;
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(new TextEncoder().encode('nope'));
      },
      cancel() {
        cancelled = true;
      },
    });
    const { fetchImpl } = stubFetch((request) =>
      request.url.startsWith('https://actions.example/oidc')
        ? new Response(body, { status: 500 })
        : jsonResponse(200, successBody()),
    );
    const config = resolveConfig({
      env: {
        EVIDGATE_API_URL: 'https://api.evidgate.test',
        EVIDGATE_PROJECT: 'proj-1',
        EVIDGATE_CACHE_DIR: cacheDir,
        ACTIONS_ID_TOKEN_REQUEST_URL: 'https://actions.example/oidc',
        ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'request-token-value',
      },
    });

    const error = await captureCliError(() => exchangeToken(config, { fetch: fetchImpl }));

    expect(error.code).toBe('oidc_request_failed');
    expect(cancelled).toBe(true);
  });

  it('exchangeAndPurge purges the cache on 401', async () => {
    const config = await oidcConfig();
    await writeCachedToken(config, { token: 'stale-token', expiresAt: '2030-01-01T00:00:00Z' });
    const { fetchImpl } = stubFetch(() => jsonResponse(401, envelope('token_invalid')));

    await expect(exchangeAndPurge(config, { fetch: fetchImpl })).rejects.toMatchObject({
      code: 'token_invalid',
    });
    await expect(readCachedToken(config)).resolves.toBeUndefined();
  });

  it('exchangeAndPurge keeps the cache on a non-auth failure', async () => {
    const config = await oidcConfig();
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
    const config = await oidcConfig();
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
        EVIDGATE_OIDC_TOKEN: 'jwt-value',
      },
    });

    const result = await exchangeToken(config);

    expect(result.token).toBe('egt_ci_token');
    expect(received).toHaveLength(1);
    expect(received[0]?.method).toBe('POST');
    expect(received[0]?.url).toBe('/ci/token');
    expect(received[0]?.auth).toBe('Bearer jwt-value');
    expect(JSON.parse(received[0]?.body ?? '{}')).toEqual({});
  });
});
