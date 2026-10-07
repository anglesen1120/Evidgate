import { beforeEach, describe, expect, it } from 'vitest';

import {
  formatConfigForVerbose,
  registerEnvSecrets,
  resolveConfig,
  resolveTokenSource,
  tokenSourceNames,
} from './config.js';
import { CliError } from './errors.js';
import { redact, redacted, resetSecrets } from './redact.js';

const baseEnv = {
  EVIDGATE_API_URL: 'https://api.evidgate.test',
  EVIDGATE_PROJECT: 'proj-1',
};

function captureCliError(run: () => unknown): CliError {
  try {
    run();
  } catch (error) {
    if (error instanceof CliError) {
      return error;
    }
    throw error;
  }
  throw new Error('expected the call to throw a CliError');
}

// 04.2.1-T2.1 / AC5: `EVIDGATE_*` resolution — precedence, validation and the
// all-missing-names-at-once message.
describe('resolveConfig', () => {
  beforeEach(() => {
    resetSecrets();
  });

  it('names every missing variable at once (AC5)', () => {
    const error = captureCliError(() => resolveConfig({ env: {}, requireToken: true }));
    expect(error.code).toBe('missing_config');
    expect(error.message).toContain('EVIDGATE_API_URL (or --api-url)');
    expect(error.message).toContain('EVIDGATE_PROJECT (or --project)');
    expect(error.message).toContain(tokenSourceNames);
    expect(error.message).toContain('EVIDGATE_OIDC_TOKEN');
    expect(error.message).toContain(
      'ACTIONS_ID_TOKEN_REQUEST_URL + ACTIONS_ID_TOKEN_REQUEST_TOKEN',
    );
    expect(error.message).toContain('EVIDGATE_INGEST_TOKEN');
    expect(error.message).toContain('EVIDGATE_DEV_TOKEN');
  });

  it('omits the token source when it is not required', () => {
    const error = captureCliError(() => resolveConfig({ env: {} }));
    expect(error.message).not.toContain('a token source');
  });

  it('treats an empty or blank env value as unset', () => {
    const error = captureCliError(() =>
      resolveConfig({ env: { EVIDGATE_API_URL: '', EVIDGATE_PROJECT: '   ' } }),
    );
    expect(error.message).toContain('EVIDGATE_API_URL (or --api-url)');
    expect(error.message).toContain('EVIDGATE_PROJECT (or --project)');
  });

  it('prefers an explicit flag over the env value (AC5)', () => {
    const config = resolveConfig({
      env: baseEnv,
      flags: { apiUrl: 'https://flag.evidgate.test', project: 'flag-project' },
    });
    expect(config.apiUrl).toBe('https://flag.evidgate.test');
    expect(config.project).toBe('flag-project');
  });

  it('falls back to the env value when no flag is given (AC5)', () => {
    const config = resolveConfig({ env: baseEnv });
    expect(config.apiUrl).toBe('https://api.evidgate.test');
    expect(config.project).toBe('proj-1');
  });

  it('returns a frozen config', () => {
    expect(Object.isFrozen(resolveConfig({ env: baseEnv }))).toBe(true);
  });

  it('normalises a trailing slash away', () => {
    const config = resolveConfig({
      env: { ...baseEnv, EVIDGATE_API_URL: 'https://api.evidgate.test/' },
    });
    expect(config.apiUrl).toBe('https://api.evidgate.test');
  });

  it('accepts http only on loopback hosts', () => {
    for (const host of ['localhost', '127.0.0.1', '[::1]']) {
      const config = resolveConfig({
        env: { ...baseEnv, EVIDGATE_API_URL: `http://${host}:8080` },
      });
      expect(config.apiUrl).toBe(`http://${host}:8080`);
    }
    const error = captureCliError(() =>
      resolveConfig({ env: { ...baseEnv, EVIDGATE_API_URL: 'http://api.evidgate.test' } }),
    );
    expect(error.code).toBe('invalid_config');
  });

  it('rejects userinfo, a query and a fragment', () => {
    for (const url of [
      'https://user:pass@api.evidgate.test',
      'https://api.evidgate.test/?x=1',
      'https://api.evidgate.test/#frag',
    ]) {
      const error = captureCliError(() =>
        resolveConfig({ env: { ...baseEnv, EVIDGATE_API_URL: url } }),
      );
      expect(error.code).toBe('invalid_config');
      expect(error.message).toContain('EVIDGATE_API_URL (or --api-url)');
    }
  });

  it('rejects a relative URL', () => {
    const error = captureCliError(() =>
      resolveConfig({ env: { ...baseEnv, EVIDGATE_API_URL: 'api.evidgate.test' } }),
    );
    expect(error.code).toBe('invalid_config');
  });

  it('rejects whitespace in the project', () => {
    const error = captureCliError(() =>
      resolveConfig({ env: { ...baseEnv, EVIDGATE_PROJECT: 'my project' } }),
    );
    expect(error.code).toBe('invalid_config');
  });

  it('lower-cases the environment name', () => {
    expect(resolveConfig({ env: { ...baseEnv, EVIDGATE_ENV: '  DEV ' } }).env).toBe('dev');
  });

  it('detects the token source in §5 order', () => {
    const cases: ReadonlyArray<readonly [Record<string, string>, string | undefined]> = [
      [{ ...baseEnv, EVIDGATE_DEV_TOKEN: 'd' }, 'dev'],
      [{ ...baseEnv, EVIDGATE_INGEST_TOKEN: 'i' }, 'ingest'],
      [
        {
          ...baseEnv,
          ACTIONS_ID_TOKEN_REQUEST_URL: 'https://actions.test',
          ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'a',
        },
        'actions-id-token',
      ],
      [{ ...baseEnv, EVIDGATE_OIDC_TOKEN: 'o' }, 'oidc'],
    ];
    for (const [env, expected] of cases) {
      expect(resolveTokenSource(resolveConfig({ env }))).toBe(expected);
    }
    expect(resolveTokenSource(resolveConfig({ env: baseEnv }))).toBeUndefined();
  });

  it('does not treat a lone ACTIONS_ID_TOKEN_REQUEST_URL as a source', () => {
    const config = resolveConfig({
      env: { ...baseEnv, ACTIONS_ID_TOKEN_REQUEST_URL: 'https://actions.test' },
    });
    expect(resolveTokenSource(config)).toBeUndefined();
  });

  it('registers every secret for redaction (T78)', () => {
    resolveConfig({
      env: {
        ...baseEnv,
        EVIDGATE_OIDC_TOKEN: 'oidc-secret',
        ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'actions-secret',
        EVIDGATE_INGEST_TOKEN: 'ingest-secret',
        EVIDGATE_DEV_TOKEN: 'dev-secret',
        EVIDGATE_BINDING_CHALLENGE: 'challenge-secret',
      },
    });
    for (const secret of [
      'oidc-secret',
      'actions-secret',
      'ingest-secret',
      'dev-secret',
      'challenge-secret',
    ]) {
      expect(redact(`value=${secret}`)).toBe(`value=${redacted}`);
    }
  });

  it('registers secrets even when the resolution fails', () => {
    captureCliError(() =>
      resolveConfig({ env: { EVIDGATE_DEV_TOKEN: 'dev-secret' }, requireToken: true }),
    );
    expect(redact('dev-secret')).toBe(redacted);
  });

  it('rejects an EVIDGATE_ENV that is not a short log-safe label (SEC4)', () => {
    for (const value of [
      'x\n::stop-commands::tok',
      'has space',
      'UPPER!',
      'a'.repeat(33),
      '-lead',
    ]) {
      const error = captureCliError(() =>
        resolveConfig({ env: { ...baseEnv, EVIDGATE_ENV: value } }),
      );
      expect(error.code).toBe('invalid_config');
      expect(error.message).toContain('EVIDGATE_ENV');
      expect(error.message).not.toContain(value);
    }
  });

  it('accepts a log-safe EVIDGATE_ENV', () => {
    for (const value of ['dev', 'staging', 'prod', 'qa-1', 'team.a_b']) {
      expect(resolveConfig({ env: { ...baseEnv, EVIDGATE_ENV: value } }).env).toBe(value);
    }
  });

  it('rejects control and format characters in the project (SEC4)', () => {
    for (const value of ['proj\u0000', 'proj\u009b', 'proj\u200b', 'proj\u202e']) {
      const error = captureCliError(() =>
        resolveConfig({ env: { ...baseEnv, EVIDGATE_PROJECT: value } }),
      );
      expect(error.code).toBe('invalid_config');
      expect(error.message).toContain('EVIDGATE_PROJECT');
      expect(error.message).not.toContain(value);
    }
  });

  it('rejects an empty query or fragment left dangling by a trailing ? or # (CR4)', () => {
    for (const url of ['https://api.evidgate.test/?', 'https://api.evidgate.test/#']) {
      const error = captureCliError(() =>
        resolveConfig({ env: { ...baseEnv, EVIDGATE_API_URL: url } }),
      );
      expect(error.code).toBe('invalid_config');
      expect(error.message).toContain('must not contain a query or fragment');
    }
  });

  it('does not echo an unparseable API URL (SEC3)', () => {
    const value = 'https://ci:hunter2@host:99999';
    const error = captureCliError(() =>
      resolveConfig({ env: { ...baseEnv, EVIDGATE_API_URL: value } }),
    );
    expect(error.code).toBe('invalid_config');
    expect(error.message).toContain('is not an absolute URL');
    expect(error.message).not.toContain('hunter2');
    expect(error.message).not.toContain(value);
  });

  it('normalises a base path without a trailing slash', () => {
    const config = resolveConfig({
      env: { ...baseEnv, EVIDGATE_API_URL: 'https://api.evidgate.test/base/' },
    });
    expect(config.apiUrl).toBe('https://api.evidgate.test/base');
  });
});

// QA-D1: `main` registers the env secrets before parsing argv, so a usage error that
// echoes a secret value is still redacted. The helper is the single place that knows
// which env names are secret.
describe('registerEnvSecrets', () => {
  beforeEach(() => {
    resetSecrets();
  });

  it('registers the five secret env values for redaction (QA-D1)', () => {
    registerEnvSecrets({
      EVIDGATE_OIDC_TOKEN: 'oidc-secret',
      ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'actions-secret',
      EVIDGATE_INGEST_TOKEN: 'ingest-secret',
      EVIDGATE_DEV_TOKEN: 'dev-secret',
      EVIDGATE_BINDING_CHALLENGE: 'challenge-secret',
    });
    for (const secret of [
      'oidc-secret',
      'actions-secret',
      'ingest-secret',
      'dev-secret',
      'challenge-secret',
    ]) {
      expect(redact(`value=${secret}`)).toBe(`value=${redacted}`);
    }
  });

  it('skips empty and unset values', () => {
    registerEnvSecrets({ EVIDGATE_DEV_TOKEN: '', EVIDGATE_INGEST_TOKEN: undefined });
    expect(redact('nothing to hide')).toBe('nothing to hide');
  });
});

// 04.2.1-T1.1 / NFR-04-2: the token-cache directory — `EVIDGATE_CACHE_DIR` as-is,
// else the platform default, and a relative env value is refused without echoing it.
describe('resolveConfig cacheDir', () => {
  beforeEach(() => {
    resetSecrets();
  });

  it('uses EVIDGATE_CACHE_DIR as-is', () => {
    const config = resolveConfig({
      env: { ...baseEnv, EVIDGATE_CACHE_DIR: '/tmp/evidgate-cache' },
    });
    expect(config.cacheDir).toBe('/tmp/evidgate-cache');
  });

  it('falls back to $XDG_CACHE_HOME/evidgate', () => {
    const config = resolveConfig({
      env: { ...baseEnv, XDG_CACHE_HOME: '/home/u/.cache' },
      platform: 'linux',
      homedir: '/home/u',
    });
    expect(config.cacheDir).toBe('/home/u/.cache/evidgate');
  });

  it('falls back to <home>/.cache/evidgate when XDG_CACHE_HOME is unset', () => {
    const config = resolveConfig({ env: baseEnv, platform: 'linux', homedir: '/home/u' });
    expect(config.cacheDir).toBe('/home/u/.cache/evidgate');
  });

  it('uses <home>/.cache/evidgate on darwin', () => {
    const config = resolveConfig({ env: baseEnv, platform: 'darwin', homedir: '/Users/u' });
    expect(config.cacheDir).toBe('/Users/u/.cache/evidgate');
  });

  it('uses %LOCALAPPDATA%\\evidgate on win32', () => {
    const config = resolveConfig({
      env: { ...baseEnv, LOCALAPPDATA: 'C:\\Users\\u\\AppData\\Local' },
      platform: 'win32',
      homedir: 'C:\\Users\\u',
    });
    expect(config.cacheDir).toBe('C:\\Users\\u\\AppData\\Local\\evidgate');
  });

  it('prefers LOCALAPPDATA over XDG_CACHE_HOME on win32', () => {
    const config = resolveConfig({
      env: { ...baseEnv, LOCALAPPDATA: 'C:\\Local', XDG_CACHE_HOME: 'C:\\xdg' },
      platform: 'win32',
      homedir: 'C:\\Users\\u',
    });
    expect(config.cacheDir).toBe('C:\\Local\\evidgate');
  });

  it('refuses a relative EVIDGATE_CACHE_DIR without echoing it', () => {
    const error = captureCliError(() =>
      resolveConfig({ env: { ...baseEnv, EVIDGATE_CACHE_DIR: 'relative/dir' } }),
    );
    expect(error.code).toBe('invalid_config');
    expect(error.message).toContain('EVIDGATE_CACHE_DIR');
    expect(error.message).not.toContain('relative/dir');
  });

  it('refuses a relative XDG_CACHE_HOME', () => {
    const error = captureCliError(() =>
      resolveConfig({ env: { ...baseEnv, XDG_CACHE_HOME: 'rel' }, platform: 'linux' }),
    );
    expect(error.code).toBe('invalid_config');
    expect(error.message).toContain('XDG_CACHE_HOME');
  });

  it('refuses a relative LOCALAPPDATA on win32', () => {
    const error = captureCliError(() =>
      resolveConfig({ env: { ...baseEnv, LOCALAPPDATA: 'rel' }, platform: 'win32' }),
    );
    expect(error.code).toBe('invalid_config');
    expect(error.message).toContain('LOCALAPPDATA');
  });

  it('prints cache_dir in the verbose dump', () => {
    const config = resolveConfig({
      env: { ...baseEnv, EVIDGATE_CACHE_DIR: '/tmp/evidgate-cache' },
    });
    expect(formatConfigForVerbose(config)).toContain('cache_dir=/tmp/evidgate-cache');
  });
});
