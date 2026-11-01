import { mkdtemp, readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { resolveConfig, type ResolvedConfig } from '../config.js';
import { CliError } from '../errors.js';
import { redact, redacted, resetSecrets } from '../redact.js';

import { ExchangeAuthError } from './exchange.js';
import { getToken } from './getToken.js';
import { readCachedToken, writeCachedToken } from './tokenCache.js';

// 04.2.1-T1.2: `exchangeAndPurge` is mocked (the purge branch itself is covered in
// exchange.test.ts); `ExchangeAuthError` is the real class, so getToken's propagation is
// exercised against the shape `exchange.ts` throws. The cache and the freshness logic
// are exercised for real.
const exchangeMock = vi.hoisted(() => ({ exchangeAndPurge: vi.fn() }));

vi.mock('./exchange.js', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  exchangeAndPurge: exchangeMock.exchangeAndPurge,
}));

const baseEnv = {
  EVIDGATE_API_URL: 'http://127.0.0.1:8080',
  EVIDGATE_PROJECT: 'proj-1',
};

const now = Date.parse('2030-01-01T00:00:00.000Z');
const iso = (offsetMs: number): string => new Date(now + offsetMs).toISOString();
/** A valid `project_id` (openapi `format: uuid`); the exchange refuses anything else (S1). */
const projectId = '3f2504e0-4f89-41d3-9a0c-0305e82c3301';

async function cacheEntries(cacheDir: string): Promise<string[]> {
  try {
    return await readdir(cacheDir);
  } catch {
    return [];
  }
}

// The dev token is guarded, then exchanged like an ingest token; an
// OIDC/ingest source reuses a fresh cached token, refreshes within the 60 s margin and
// purges the entry on a 401/403.
describe('getToken', () => {
  let cacheDir: string;
  const fetchSpy = vi.fn(() => {
    throw new Error('network access is forbidden in tests');
  });

  function configFor(env: Record<string, string | undefined>): ResolvedConfig {
    return resolveConfig({ env: { ...baseEnv, EVIDGATE_CACHE_DIR: cacheDir, ...env } });
  }

  beforeEach(async () => {
    resetSecrets();
    vi.stubGlobal('fetch', fetchSpy);
    exchangeMock.exchangeAndPurge.mockReset();
    cacheDir = await mkdtemp(join(tmpdir(), 'evidgate-gettoken-'));
  });

  afterEach(async () => {
    vi.unstubAllGlobals();
    fetchSpy.mockClear();
    await rm(cacheDir, { recursive: true, force: true });
  });

  it('exchanges the dev token as an ingest credential and returns the ci_producer token', async () => {
    const config = configFor({ EVIDGATE_ENV: 'dev', EVIDGATE_DEV_TOKEN: 'dev-token-value' });
    exchangeMock.exchangeAndPurge.mockResolvedValue({
      token: 'ci-token',
      expiresAt: iso(20 * 60_000),
      projectId,
      source: 'ingest',
    });

    const result = await getToken(config, { now: () => now });

    expect(result).toEqual({ token: 'ci-token', source: 'dev', expiresAt: iso(20 * 60_000) });
    expect(result.token).not.toBe('dev-token-value');
    expect(exchangeMock.exchangeAndPurge).toHaveBeenCalledTimes(1);
    const exchangeConfig = exchangeMock.exchangeAndPurge.mock.calls[0]?.[0] as ResolvedConfig;
    expect(exchangeConfig.ingestToken).toBe('dev-token-value');
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it.each([
    ['staging', 'http://127.0.0.1:8080'],
    [undefined, 'http://127.0.0.1:8080'],
    ['dev', 'https://api.example.com'],
  ])('refuses the dev token before any exchange (env=%s, url=%s)', async (env, apiUrl) => {
    const config = configFor({
      EVIDGATE_ENV: env,
      EVIDGATE_API_URL: apiUrl,
      EVIDGATE_DEV_TOKEN: 'dev-token-value',
    });

    await expect(getToken(config, { now: () => now })).rejects.toMatchObject({
      code: 'dev_token_refused',
    });
    expect(exchangeMock.exchangeAndPurge).not.toHaveBeenCalled();
  });

  it('surfaces an exchange failure for the dev token without echoing it', async () => {
    const config = configFor({ EVIDGATE_ENV: 'dev', EVIDGATE_DEV_TOKEN: 'dev-token-value' });
    exchangeMock.exchangeAndPurge.mockRejectedValue(
      new CliError('the ingest token was refused', { code: 'auth_failed' }),
    );

    const failure = await getToken(config, { now: () => now }).catch((e: unknown) => e);

    expect(failure).toMatchObject({ code: 'auth_failed' });
    expect(String((failure as Error).message)).not.toContain('dev-token-value');
  });

  it('registers the returned token for redaction (SEC6)', async () => {
    const config = configFor({ EVIDGATE_ENV: 'dev', EVIDGATE_DEV_TOKEN: 'dev-token-value' });
    exchangeMock.exchangeAndPurge.mockResolvedValue({
      token: 'ci-token',
      expiresAt: iso(20 * 60_000),
      projectId,
      source: 'ingest',
    });
    // Drop what `resolveConfig` registered, so only `getToken` can re-register it.
    resetSecrets();

    await getToken(config, { now: () => now });

    expect(redact('bearer ci-token')).toBe(`bearer ${redacted}`);
  });

  it('does not use the dev token when an OIDC source is set (AC8)', async () => {
    const config = configFor({
      EVIDGATE_ENV: 'dev',
      EVIDGATE_DEV_TOKEN: 'dev-token-value',
      EVIDGATE_OIDC_TOKEN: 'oidc-jwt',
    });
    exchangeMock.exchangeAndPurge.mockResolvedValue({
      token: 'ci-token',
      expiresAt: iso(20 * 60_000),
      projectId,
      source: 'oidc',
    });

    const result = await getToken(config, { now: () => now });

    expect(result).toEqual({ token: 'ci-token', source: 'oidc', expiresAt: iso(20 * 60_000) });
    expect(exchangeMock.exchangeAndPurge).toHaveBeenCalledTimes(1);
  });

  it('cache_reuse_and_60s_refresh', async () => {
    const config = configFor({ EVIDGATE_OIDC_TOKEN: 'oidc-jwt' });
    const deps = { now: () => now };

    // Fresh cache → no exchange.
    await writeCachedToken(config, { token: 'cached-token', expiresAt: iso(10 * 60_000) });
    await expect(getToken(config, deps)).resolves.toEqual({
      token: 'cached-token',
      source: 'oidc',
      expiresAt: iso(10 * 60_000),
    });
    expect(exchangeMock.exchangeAndPurge).not.toHaveBeenCalled();

    // Within the 60 s margin → exchange and rewrite the cache.
    await writeCachedToken(config, { token: 'stale-token', expiresAt: iso(30_000) });
    exchangeMock.exchangeAndPurge.mockResolvedValue({
      token: 'fresh-token',
      expiresAt: iso(20 * 60_000),
      projectId,
      source: 'oidc',
    });
    await expect(getToken(config, deps)).resolves.toEqual({
      token: 'fresh-token',
      source: 'oidc',
      expiresAt: iso(20 * 60_000),
    });
    expect(exchangeMock.exchangeAndPurge).toHaveBeenCalledTimes(1);
    await expect(readCachedToken(config)).resolves.toEqual({
      token: 'fresh-token',
      expiresAt: iso(20 * 60_000),
    });

    // Exactly at the margin → refresh (not fresh).
    exchangeMock.exchangeAndPurge.mockClear();
    await writeCachedToken(config, { token: 'boundary-token', expiresAt: iso(60_000) });
    await getToken(config, deps);
    expect(exchangeMock.exchangeAndPurge).toHaveBeenCalledTimes(1);
  });

  it('propagates an auth error from the exchange', async () => {
    const config = configFor({ EVIDGATE_OIDC_TOKEN: 'oidc-jwt' });
    exchangeMock.exchangeAndPurge.mockRejectedValue(
      new ExchangeAuthError('token rejected', { code: 'token_invalid', status: 401 }),
    );

    await expect(getToken(config, { now: () => now })).rejects.toMatchObject({
      code: 'token_invalid',
    });
    expect(exchangeMock.exchangeAndPurge).toHaveBeenCalledTimes(1);
    expect(await cacheEntries(cacheDir)).toEqual([]);
  });

  it('keeps the cache on a non-auth failure', async () => {
    const config = configFor({ EVIDGATE_OIDC_TOKEN: 'oidc-jwt' });
    await writeCachedToken(config, { token: 'stale-token', expiresAt: iso(30_000) });
    exchangeMock.exchangeAndPurge.mockRejectedValue(
      new CliError('network down', { code: 'transport' }),
    );

    await expect(getToken(config, { now: () => now })).rejects.toMatchObject({ code: 'transport' });
    await expect(readCachedToken(config)).resolves.toEqual({
      token: 'stale-token',
      expiresAt: iso(30_000),
    });
  });

  it('raises missing_config when no source is set', async () => {
    await expect(getToken(configFor({}))).rejects.toMatchObject({ code: 'missing_config' });
  });
});
