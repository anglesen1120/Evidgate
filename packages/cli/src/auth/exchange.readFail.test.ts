import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import {
  captureCliErrorAsync as captureCliError,
  stubRequestFetch as stubFetch,
} from '../commands/cliSpec.testing.js';
import { resolveConfig } from '../config.js';
import { resetSecrets } from '../redact.js';

import { exchangeToken } from './exchange.js';

const bodyPrefix = 'ghp_SECRETBODYPREFIX';
const readMessage = 'the server answer could not be read to the end';
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
