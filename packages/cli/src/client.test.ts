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

    expect(clientConfig.baseUrl).toBe('https://api.evidgate.test');
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
});
