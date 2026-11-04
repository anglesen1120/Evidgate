// The redirect refusal for `evidgate manifests upload`: `redirect: 'error'` lives in the
// transport layer, so this case needs a real 127.0.0.1 server — an injected `fetch` stub
// that returns a 302 never performs the redirect.
import { createServer } from 'node:http';

import { beforeEach, describe, expect, it } from 'vitest';

import { main } from '../main.js';
import { resetSecrets } from '../redact.js';

import { exchangeBody, mintedToken } from './devExchange.testing.js';
import { capture, exchangePath, project, useManifestSpec } from './manifestsUpload.testing.js';

describe('manifests upload — boundaries', () => {
  const { manifestFile, devEnv } = useManifestSpec('evidgate-cli-qa-');

  beforeEach(() => {
    resetSecrets();
  });

  // The redirect refusal lives in the transport layer (`redirect: 'error'` on the
  // outgoing Request), so it needs a real 127.0.0.1 server: an injected `fetch` stub
  // that returns a 302 Response never performs the redirect.
  it('redirect_is_refused_as_transport', async () => {
    const served: string[] = [];
    const server = createServer((request, response) => {
      served.push(request.url ?? '');
      if (request.url === exchangePath) {
        response.writeHead(200, { 'content-type': 'application/json' });
        response.end(JSON.stringify(exchangeBody(mintedToken)));
        return;
      }
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
      // The exchange was answered, so the refused 302 is the upload's own redirect; the
      // redirect refusal is a transport failure and is retried like one.
      expect(served[0]).toBe(exchangePath);
      expect(served.length).toBeGreaterThan(1);
      for (const path of served.slice(1)) {
        expect(path).toBe(`/v1/projects/${project}/manifests`);
      }
    } finally {
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
  });
});
