import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import {
  captureCliErrorAsync as captureCliError,
  jsonResponse,
  stubRequestFetch as stubFetch,
} from '../commands/cliSpec.testing.js';
import { resolveConfig } from '../config.js';
import { resetSecrets } from '../redact.js';

import { exchangeToken } from './exchange.js';

const oidcUrl = 'https://actions.example/oidc?request_value=SECRETQUERY';
const bodyPrefix = 'ghp_SECRETBODYPREFIX';
const requestToken = 'request-token-value';
const readMessage = 'the server answer could not be read to the end';
const readHint = 'the answer could not be read; the command can be run again';
const dirs: string[] = [];

afterEach(async () => {
  resetSecrets();
  await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
});

/** A body that hands out `first` and then fails, so the cut lands after one real chunk. */
function cutAfterChunk(first: string, failure: () => Error): ReadableStream<Uint8Array> {
  let served = false;
  return new ReadableStream<Uint8Array>(
    {
      pull(controller) {
        if (!served) {
          served = true;
          controller.enqueue(new TextEncoder().encode(first));
          return;
        }
        controller.error(failure());
      },
    },
    { highWaterMark: 0 },
  );
}

async function oidcConfig(extra: Record<string, string> = {}) {
  const cacheDir = await mkdtemp(join(tmpdir(), 'evidgate-cli-'));
  dirs.push(cacheDir);
  return resolveConfig({
    env: {
      EVIDGATE_API_URL: 'https://api.evidgate.test',
      EVIDGATE_PROJECT: 'proj-1',
      EVIDGATE_CACHE_DIR: cacheDir,
      ACTIONS_ID_TOKEN_REQUEST_URL: oidcUrl,
      ACTIONS_ID_TOKEN_REQUEST_TOKEN: requestToken,
      ...extra,
    },
  });
}

const cuts: ReadonlyArray<[string, () => Error]> = [
  ['abort', () => new DOMException('the operation was aborted', 'AbortError')],
  ['timeout', () => new DOMException('the operation timed out', 'TimeoutError')],
  ['socket', () => new TypeError('terminated', { cause: new Error('socket closed by peer') })],
];

describe('GitHub ID-token read failure', () => {
  it.each(cuts)(
    'reports a %s cut mid-body as response_read_failed, never as bad JSON, leaking nothing',
    async (_kind, failure) => {
      const config = await oidcConfig();
      const { fetchImpl } = stubFetch((request) =>
        request.url.startsWith('https://actions.example/oidc')
          ? new Response(cutAfterChunk(bodyPrefix, failure), { status: 200 })
          : jsonResponse(200, {}),
      );

      const error = await captureCliError(() => exchangeToken(config, { fetch: fetchImpl }));

      expect(error.code).toBe('response_read_failed');
      expect(error.message).toBe(readMessage);
      expect(error.hint).toBe(readHint);
      const text = `${error.message} ${error.hint ?? ''} ${String(error)}`;
      expect(text).not.toContain('SECRETBODYPREFIX');
      expect(text).not.toContain('SECRETQUERY');
      expect(text).not.toContain('audience');
      expect(text).not.toContain(requestToken);
      expect(text).not.toContain('not valid JSON');
    },
  );

  it('keeps oidc_request_failed for a complete body that is not JSON', async () => {
    const config = await oidcConfig();
    const { fetchImpl } = stubFetch((request) =>
      request.url.startsWith('https://actions.example/oidc')
        ? new Response('this is not json', { status: 200 })
        : jsonResponse(200, {}),
    );

    const error = await captureCliError(() => exchangeToken(config, { fetch: fetchImpl }));

    expect(error.code).toBe('oidc_request_failed');
    expect(error.message).toBe('the GitHub Actions ID token response was not valid JSON');
  });
});

describe('token exchange read failure', () => {
  it('reports a cut 2xx token answer as response_read_failed, not exchange_failed', async () => {
    const cacheDir = await mkdtemp(join(tmpdir(), 'evidgate-cli-'));
    dirs.push(cacheDir);
    const config = resolveConfig({
      env: {
        EVIDGATE_API_URL: 'https://api.evidgate.test',
        EVIDGATE_PROJECT: 'proj-1',
        EVIDGATE_CACHE_DIR: cacheDir,
        EVIDGATE_INGEST_TOKEN: 'ingest-token-value',
      },
    });
    const { fetchImpl, calls } = stubFetch(
      () =>
        new Response(
          cutAfterChunk(bodyPrefix, () => new TypeError('terminated')),
          { status: 200 },
        ),
    );

    const error = await captureCliError(() => exchangeToken(config, { fetch: fetchImpl }));

    expect(error.code).toBe('response_read_failed');
    expect(error.message).toBe(readMessage);
    expect(`${error.message} ${error.hint ?? ''}`).not.toContain('SECRETBODYPREFIX');
    expect(calls).toHaveLength(1);
  });
});
