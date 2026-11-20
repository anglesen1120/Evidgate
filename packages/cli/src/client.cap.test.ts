import { once } from 'node:events';
import { createServer, type Server, type ServerResponse } from 'node:http';
import { gzipSync } from 'node:zlib';

import { getSuiteRevisionExecutionPlan } from '@evidgate/sdk';
import { afterEach, describe, expect, it } from 'vitest';

import { maxResponseBytes, ResponseTooLargeError } from './boundedResponse.js';
import { callApi } from './callApi.js';
import { createApiClient } from './client.js';
import { resolveConfig } from './config.js';
import { serverErrorFromResponse } from './serverError.js';

const revision = '11111111-1111-4111-8111-111111111111';
type Handler = (response: ServerResponse) => void;

describe('createApiClient response cap', () => {
  const servers: Server[] = [];
  afterEach(async () => {
    for (const server of servers.splice(0)) {
      const closed = once(server, 'close');
      server.close();
      server.closeAllConnections();
      await closed;
    }
  });

  /** A real local server, so the stack under test is undici's own (decompression included). */
  async function start(
    handler: Handler,
  ): Promise<{ call: () => Promise<unknown>; hits: () => number }> {
    let hits = 0;
    const server = createServer((_request, response) => {
      hits += 1;
      handler(response);
    });
    servers.push(server);
    server.listen(0, '127.0.0.1');
    await once(server, 'listening');
    const address = server.address();
    if (address === null || typeof address === 'string') {
      throw new Error('expected a TCP address');
    }
    const config = resolveConfig({
      env: { EVIDGATE_API_URL: `http://127.0.0.1:${address.port}`, EVIDGATE_PROJECT: 'p' },
    });
    const client = createApiClient(config, 'tok');
    return {
      hits: () => hits,
      call: () =>
        callApi(
          (signal) =>
            getSuiteRevisionExecutionPlan({ client, path: { r: revision }, query: {}, signal }),
          { policy: 'replay-safe', deps: { sleep: () => Promise.resolve(), random: () => 0 } },
        ),
    };
  }

  const bigJson = (status: number): string =>
    JSON.stringify({ pad: 'x'.repeat(maxResponseBytes + 10), status });

  it('refuses an over-cap ok answer once, without a retry', async () => {
    const api = await start((response) => {
      response.writeHead(200, { 'content-type': 'application/json' });
      response.end(bigJson(200));
    });

    await expect(api.call()).rejects.toBeInstanceOf(ResponseTooLargeError);

    expect(api.hits()).toBe(1);
  });

  it('refuses an over-cap 5xx error body once, without a retry', async () => {
    const api = await start((response) => {
      response.writeHead(503, { 'content-type': 'application/json' });
      response.end(bigJson(503));
    });

    await expect(api.call()).rejects.toBeInstanceOf(ResponseTooLargeError);

    expect(api.hits()).toBe(1);
  });

  it('caps the decoded body of a gzip answer whose wire size is under the cap', async () => {
    const compressed = gzipSync(Buffer.from(bigJson(200)));
    expect(compressed.byteLength).toBeLessThan(maxResponseBytes / 100);
    const api = await start((response) => {
      response.writeHead(200, {
        'content-type': 'application/json',
        'content-encoding': 'gzip',
        'content-length': String(compressed.byteLength),
      });
      response.end(compressed);
    });

    await expect(api.call()).rejects.toBeInstanceOf(ResponseTooLargeError);
  });

  it('still maps a normal error envelope exactly as before, and retries a retryable 503', async () => {
    const envelope = {
      error: { code: 'plan_unavailable', message: 'try later', request_id: 'r1', retryable: true },
    };
    const api = await start((response) => {
      response.writeHead(503, { 'content-type': 'application/json' });
      response.end(JSON.stringify(envelope));
    });

    const result = (await api.call()) as { error: unknown; response?: Response };

    expect(api.hits()).toBe(3);
    expect(result.response?.status).toBe(503);
    const mapped = serverErrorFromResponse(result.error, result.response as Response, {
      code: 'request_failed',
      message: 'fallback',
    });
    expect([mapped.code, mapped.message, mapped.requestId]).toEqual([
      'plan_unavailable',
      'try later',
      'r1',
    ]);
  });

  it('passes an answer under the cap', async () => {
    const api = await start((response) => {
      response.writeHead(200, { 'content-type': 'application/json' });
      response.end(JSON.stringify({ suite_revision_id: revision, members: [], next_cursor: null }));
    });

    const result = (await api.call()) as { response: Response };

    expect(result.response.status).toBe(200);
  });
});
