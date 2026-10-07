import fs from 'node:fs';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { resolveConfig } from '../config.js';
import { resetSecrets } from '../redact.js';

import { getToken } from './getToken.js';

const baseEnv = {
  EVIDGATE_API_URL: 'https://api.evidgate.test',
  EVIDGATE_PROJECT: 'proj-1',
};

// 04.2.1-T2.2 / AC8: the dev path performs no exchange, no network call and no file
// write; an OIDC/ingest source is handed over to 04.2.1-T1 instead of using the dev
// token.
describe('getToken', () => {
  const fetchSpy = vi.fn(() => {
    throw new Error('network access is forbidden in tests');
  });

  beforeEach(() => {
    resetSecrets();
    vi.stubGlobal('fetch', fetchSpy);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    fetchSpy.mockClear();
  });

  it('returns the dev token without an exchange, a fetch or a file write (AC8)', async () => {
    const writeFileSync = vi.spyOn(fs, 'writeFileSync');
    const writeFile = vi.spyOn(fs.promises, 'writeFile');
    const config = resolveConfig({
      env: { ...baseEnv, EVIDGATE_ENV: 'dev', EVIDGATE_DEV_TOKEN: 'dev-token-value' },
    });

    const result = await getToken(config);

    expect(result).toEqual({ token: 'dev-token-value', source: 'dev' });
    expect(fetchSpy).not.toHaveBeenCalled();
    expect(writeFileSync).not.toHaveBeenCalled();
    expect(writeFile).not.toHaveBeenCalled();
  });

  it('does not use the dev token when an OIDC source is set (AC8)', async () => {
    const config = resolveConfig({
      env: {
        ...baseEnv,
        EVIDGATE_ENV: 'dev',
        EVIDGATE_DEV_TOKEN: 'dev-token-value',
        EVIDGATE_OIDC_TOKEN: 'oidc-jwt',
      },
    });
    await expect(getToken(config)).rejects.toMatchObject({ code: 'exchange_unavailable' });
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('hands the ACTIONS_ID_TOKEN_REQUEST pair over to 04.2.1-T1', async () => {
    const config = resolveConfig({
      env: {
        ...baseEnv,
        ACTIONS_ID_TOKEN_REQUEST_URL: 'https://actions.test',
        ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'actions-token',
      },
    });
    await expect(getToken(config)).rejects.toMatchObject({ code: 'exchange_unavailable' });
  });

  it('hands the ingest token over to 04.2.1-T1', async () => {
    const config = resolveConfig({ env: { ...baseEnv, EVIDGATE_INGEST_TOKEN: 'ingest-token' } });
    await expect(getToken(config)).rejects.toMatchObject({ code: 'exchange_unavailable' });
  });

  it('raises missing_config when no source is set', async () => {
    await expect(getToken(resolveConfig({ env: baseEnv }))).rejects.toMatchObject({
      code: 'missing_config',
    });
  });
});
