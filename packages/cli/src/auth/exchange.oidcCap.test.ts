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

const oidcUrl = 'https://actions.example/oidc';
const dirs: string[] = [];

afterEach(async () => {
  resetSecrets();
  await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
});

async function oidcConfig() {
  const cacheDir = await mkdtemp(join(tmpdir(), 'evidgate-cli-'));
  dirs.push(cacheDir);
  return resolveConfig({
    env: {
      EVIDGATE_API_URL: 'https://api.evidgate.test',
      EVIDGATE_PROJECT: 'proj-1',
      EVIDGATE_CACHE_DIR: cacheDir,
      ACTIONS_ID_TOKEN_REQUEST_URL: oidcUrl,
      ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'request-token-value',
    },
  });
}

describe('GitHub ID-token answer cap', () => {
  it('refuses an over-cap answer with response_too_large and no token byte in the text', async () => {
    const secret = 'eyJ.header.payload-SECRET';
    const oversize = JSON.stringify({ count: 1, value: `${secret}${'a'.repeat(70 * 1024)}` });
    const config = await oidcConfig();
    const { fetchImpl, calls } = stubFetch((request) =>
      request.url.startsWith(oidcUrl)
        ? new Response(oversize, { status: 200 })
        : jsonResponse(200, {}),
    );

    const error = await captureCliError(() => exchangeToken(config, { fetch: fetchImpl }));

    expect(error.code).toBe('response_too_large');
    expect(`${error.message} ${error.hint ?? ''}`).not.toContain('SECRET');
    expect(calls).toHaveLength(1);
  });
});
