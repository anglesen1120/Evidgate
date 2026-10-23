import { once } from 'node:events';
import { createServer, type Server } from 'node:http';

import { afterEach, describe, expect, it } from 'vitest';

import { createApiClient } from './client.js';
import { resolveConfig } from './config.js';

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

// 04.2.1-T2.1 / 04.2.1 §8 SSRF hygiene: the client carries the validated base URL, the
// bearer token and `redirect: 'error'`. No request is made here — the config is
// inspected directly.
describe('createApiClient', () => {
  const servers: Server[] = [];

  afterEach(async () => {
    await Promise.all(servers.splice(0).map((server) => close(server)));
  });

  it('sets the base URL, the bearer header and redirect: error', () => {
    const config = resolveConfig({
      env: { EVIDGATE_API_URL: 'https://api.evidgate.test', EVIDGATE_PROJECT: 'proj-1' },
    });

    const client = createApiClient(config, 'bearer-token-value');
    const clientConfig = client.getConfig();

    expect(clientConfig.baseUrl).toBe('https://api.evidgate.test/v1');
    expect(clientConfig.redirect).toBe('error');
    expect(clientConfig.headers).toBeInstanceOf(Headers);
    expect((clientConfig.headers as Headers).get('authorization')).toBe(
      'Bearer bearer-token-value',
    );
  });

  it('refuses to follow a redirect, so the target never sees the bearer (SEC7)', async () => {
    const targetRequests: Array<string | undefined> = [];
    const target = createServer((request, response) => {
      targetRequests.push(request.headers.authorization);
      response.writeHead(200, { 'content-type': 'application/json' }).end('{}');
    });
    servers.push(target);
    const targetPort = await listen(target);

    const redirector = createServer((_request, response) => {
      response.writeHead(302, { location: `http://127.0.0.1:${targetPort}/` }).end();
    });
    servers.push(redirector);
    const port = await listen(redirector);

    const env = { EVIDGATE_API_URL: `http://127.0.0.1:${port}`, EVIDGATE_PROJECT: 'proj-1' };
    const config = resolveConfig({ env });
    const client = createApiClient(config, 'bearer-token-value');

    // A per-call `redirect: 'follow'` would win over the config without the wrapper.
    const pending = client.get({ url: '/', redirect: 'follow', throwOnError: true });
    await expect(pending).rejects.toThrow();
    expect(targetRequests).toEqual([]);
  });

  it('omits the Authorization header when no token is given (ingest exchange)', () => {
    const config = resolveConfig({
      env: { EVIDGATE_API_URL: 'https://api.evidgate.test', EVIDGATE_PROJECT: 'proj-1' },
    });

    const client = createApiClient(config);
    const clientConfig = client.getConfig();

    expect(clientConfig.headers).toBeInstanceOf(Headers);
    expect((clientConfig.headers as Headers).get('authorization')).toBeNull();
  });

  it('uses the injected fetch and forces redirect: error on every call', async () => {
    const config = resolveConfig({
      env: { EVIDGATE_API_URL: 'https://api.evidgate.test', EVIDGATE_PROJECT: 'proj-1' },
    });
    const seen: Request[] = [];
    const fetchImpl: typeof fetch = (input) => {
      const request = input as Request;
      seen.push(request);
      return Promise.resolve(
        new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } }),
      );
    };

    const client = createApiClient(config, 'bearer-token-value', fetchImpl);
    await client.get({ url: '/', redirect: 'follow' });

    expect(seen).toHaveLength(1);
    expect(seen[0]?.redirect).toBe('error');
    expect(seen[0]?.headers.get('authorization')).toBe('Bearer bearer-token-value');
  });

  // D-1 / AC1: the openapi document declares `servers: - url: /v1`, so every SDK call
  // must land on `<EVIDGATE_API_URL>/v1/<operation path>`; the env value is the API root
  // without the version (contracts/cli.md §3).
  it('sends every call to <apiUrl>/v1/<operation path>', async () => {
    const config = resolveConfig({
      env: { EVIDGATE_API_URL: 'https://api.evidgate.test', EVIDGATE_PROJECT: 'proj-1' },
    });
    const seen: Request[] = [];
    const fetchImpl: typeof fetch = (input) => {
      seen.push(input as Request);
      return Promise.resolve(
        new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } }),
      );
    };

    const client = createApiClient(config, 'bearer-token-value', fetchImpl);
    await client.get({ url: '/ci/token' });

    expect(seen[0]?.url).toBe('https://api.evidgate.test/v1/ci/token');
  });

  it('keeps a path prefix in EVIDGATE_API_URL before the version (/api -> /api/v1)', async () => {
    const config = resolveConfig({
      env: { EVIDGATE_API_URL: 'http://127.0.0.1:8080/api', EVIDGATE_PROJECT: 'proj-1' },
    });
    const seen: Request[] = [];
    const fetchImpl: typeof fetch = (input) => {
      seen.push(input as Request);
      return Promise.resolve(
        new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } }),
      );
    };

    const client = createApiClient(config, 'bearer-token-value', fetchImpl);
    await client.get({ url: '/ci/token' });

    expect(new URL(seen[0]?.url ?? '').pathname).toBe('/api/v1/ci/token');
  });
});
