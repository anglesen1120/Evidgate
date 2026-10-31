// `evidgate gate` token recovery: one refresh after a 401, the sources that can and cannot
// mint a second credential, and the reasons a replay is skipped.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { writeCachedToken } from '../auth/tokenCache.js';
import { resetSecrets } from '../redact.js';

import {
  cacheDirs,
  capture,
  decided,
  devEnv,
  exchangeBody,
  gateArgv,
  ingestEnv,
  invoke,
  jsonResponse,
  oidcEnv,
  project,
  refusal,
  runId,
  stubFetch,
} from './gate.kit.test.js';

describe('gate token refresh', () => {
  const dirs = cacheDirs();

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await dirs.reset();
  });

  it('exits 3 when the refreshed token is the one just refused', async () => {
    const env = await devEnv(dirs);
    const stub = stubFetch(() => refusal(401, 'token_invalid'));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    // One refused poll and nothing else: the dev token is handed back unchanged, so the
    // replay would repeat the refusal and is skipped.
    expect(stub.calls.map((call) => new URL(call.request.url).pathname)).toStrictEqual([
      `/v1/runs/${runId}/decision`,
    ]);
    expect(out.stderr.join('')).toContain('error token_invalid');
  });

  it('mints a new token once when a 401 arrives and replays that poll', async () => {
    const env = await ingestEnv(dirs);
    let exchanges = 0;
    const stub = stubFetch((request) => {
      if (request.url.endsWith('/v1/ci/token')) {
        exchanges += 1;
        return jsonResponse(200, exchangeBody(`egt-token-${exchanges}`));
      }
      return request.headers.get('authorization') === 'Bearer egt-token-2'
        ? decided({ decision: 'PASS' })
        : refusal(401, 'token_invalid');
    });
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(0);
    expect(exchanges).toBe(2);
    expect(out.stdout.join('')).toBe(`gate: PASS (run ${runId})\n`);
    expect(stub.calls.map((call) => new URL(call.request.url).pathname)).toStrictEqual([
      '/v1/ci/token',
      `/v1/runs/${runId}/decision`,
      '/v1/ci/token',
      `/v1/runs/${runId}/decision`,
    ]);
    expect(stub.calls.at(-1)?.request.headers.get('authorization')).toBe('Bearer egt-token-2');
  });

  it('exits 3 when the replayed poll is refused again after a refresh', async () => {
    const env = await ingestEnv(dirs);
    let exchanges = 0;
    const stub = stubFetch((request) => {
      if (request.url.endsWith('/v1/ci/token')) {
        exchanges += 1;
        return jsonResponse(200, exchangeBody(`egt-token-${exchanges}`));
      }
      return refusal(401, 'token_invalid');
    });
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    // Exchange, refused poll, one refresh, refused replay: the second 401 is the last word.
    expect(stub.calls.map((call) => new URL(call.request.url).pathname)).toStrictEqual([
      '/v1/ci/token',
      `/v1/runs/${runId}/decision`,
      '/v1/ci/token',
      `/v1/runs/${runId}/decision`,
    ]);
    expect(out.stderr.join('')).toContain('error token_invalid');
  });

  it('refreshes from an unused OIDC credential when a cached token is refused', async () => {
    const env = await oidcEnv(dirs);
    // A cached token proves the JWT the cache was minted from is not spent by this run, so
    // the refused token can be replaced by a fresh exchange.
    await writeCachedToken(
      { apiUrl: 'https://api.evidgate.test', project, cacheDir: env.EVIDGATE_CACHE_DIR },
      // Ten minutes ahead of the virtual clock the command runs on, so the entry reads fresh.
      { token: 'egt-cached', expiresAt: new Date(10 * 60_000).toISOString() },
    );
    const stub = stubFetch((request) =>
      request.url.endsWith('/v1/ci/token')
        ? jsonResponse(200, exchangeBody('egt-token-2'))
        : request.headers.get('authorization') === 'Bearer egt-token-2'
          ? decided({ decision: 'PASS' })
          : refusal(401, 'token_invalid'),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(0);
    expect(stub.calls.map((call) => new URL(call.request.url).pathname)).toStrictEqual([
      `/v1/runs/${runId}/decision`,
      '/v1/ci/token',
      `/v1/runs/${runId}/decision`,
    ]);
    expect(out.stdout.join('')).toBe(`gate: PASS (run ${runId})\n`);
  });

  it('keeps the 401 when the refresh itself is refused', async () => {
    const env = await oidcEnv(dirs);
    let exchanges = 0;
    const stub = stubFetch((request) => {
      if (!request.url.endsWith('/v1/ci/token')) {
        return refusal(401, 'token_invalid');
      }
      exchanges += 1;
      // The second exchange replays a single-use JWT, which the server refuses.
      return exchanges === 1
        ? jsonResponse(200, exchangeBody('egt-token-1'))
        : refusal(403, 'token_replayed', { message: 'this credential was already exchanged' });
    });
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId]), env, out, stub.fetchImpl);

    expect(code).toBe(3);
    // The decision's refusal explains the failure, not the replay the recovery attempted.
    expect(out.stderr.join('')).toContain('error token_invalid');
    expect(out.stderr.join('')).not.toContain('token_replayed');
  });
});
