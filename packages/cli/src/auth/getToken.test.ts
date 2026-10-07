import type * as NodeFs from 'node:fs';
import type * as NodeFsPromises from 'node:fs/promises';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { resolveConfig } from '../config.js';
import { redact, redacted, resetSecrets } from '../redact.js';

import { getToken } from './getToken.js';

// CR7: the AC8 "no file write" guard must also see named-import writes, so the whole
// `node:fs` / `node:fs/promises` modules are mocked instead of spying on the default
// export object (a `import { writeFile } from 'node:fs/promises'` would bypass a spy).
// The real module is spread in, so any other member a later task imports still works.
const fsSpies = vi.hoisted(() => ({ writeFileSync: vi.fn(), writeFile: vi.fn() }));

vi.mock('node:fs', async (importOriginal) => {
  const actual = await importOriginal<typeof NodeFs>();
  return {
    ...actual,
    default: { ...actual, writeFileSync: fsSpies.writeFileSync },
    writeFileSync: fsSpies.writeFileSync,
  };
});

vi.mock('node:fs/promises', async (importOriginal) => {
  const actual = await importOriginal<typeof NodeFsPromises>();
  return {
    ...actual,
    default: { ...actual, writeFile: fsSpies.writeFile },
    writeFile: fsSpies.writeFile,
  };
});

const baseEnv = {
  EVIDGATE_API_URL: 'http://127.0.0.1:8080',
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
    fsSpies.writeFileSync.mockClear();
    fsSpies.writeFile.mockClear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    fetchSpy.mockClear();
  });

  it('returns the dev token without an exchange, a fetch or a file write (AC8)', async () => {
    const config = resolveConfig({
      env: { ...baseEnv, EVIDGATE_ENV: 'dev', EVIDGATE_DEV_TOKEN: 'dev-token-value' },
    });

    const result = await getToken(config);

    expect(result).toEqual({ token: 'dev-token-value', source: 'dev' });
    expect(fetchSpy).not.toHaveBeenCalled();
    expect(fsSpies.writeFileSync).not.toHaveBeenCalled();
    expect(fsSpies.writeFile).not.toHaveBeenCalled();
  });

  it('registers the returned token for redaction (SEC6)', async () => {
    const config = resolveConfig({
      env: { ...baseEnv, EVIDGATE_ENV: 'dev', EVIDGATE_DEV_TOKEN: 'dev-token-value' },
    });
    // Drop what `resolveConfig` registered, so only `getToken` can re-register it.
    resetSecrets();

    await getToken(config);

    expect(redact('bearer dev-token-value')).toBe(`bearer ${redacted}`);
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
