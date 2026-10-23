import { resolve } from 'node:path';

import { beforeEach, describe, expect, it } from 'vitest';

import {
  defaultSendDeadlineMs,
  defaultSpoolMaxBytes,
  formatConfigForVerbose,
  maxSendDeadlineMs,
  maxSpoolMaxBytes,
  registerEnvSecrets,
  resolveConfig,
  resolveSendDeadlineMs,
  resolveSpoolDir,
  resolveSpoolMaxBytes,
  resolveSpoolSettings,
  resolveTokenSource,
  spoolDirMissingError,
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
        ACTIONS_ID_TOKEN_REQUEST_URL: 'https://actions.test/oidc?token=url-secret',
        ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'actions-secret',
        EVIDGATE_INGEST_TOKEN: 'ingest-secret',
        EVIDGATE_DEV_TOKEN: 'dev-secret',
        EVIDGATE_BINDING_CHALLENGE: 'challenge-secret',
      },
    });
    for (const secret of [
      'oidc-secret',
      'https://actions.test/oidc?token=url-secret',
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

  it('registers the six secret env values for redaction (QA-D1, D4/S2)', () => {
    registerEnvSecrets({
      EVIDGATE_OIDC_TOKEN: 'oidc-secret',
      ACTIONS_ID_TOKEN_REQUEST_URL: 'https://actions.test/oidc?token=url-secret',
      ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'actions-secret',
      EVIDGATE_INGEST_TOKEN: 'ingest-secret',
      EVIDGATE_DEV_TOKEN: 'dev-secret',
      EVIDGATE_BINDING_CHALLENGE: 'challenge-secret',
    });
    for (const secret of [
      'oidc-secret',
      'https://actions.test/oidc?token=url-secret',
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
// else the platform default; only a relative `EVIDGATE_CACHE_DIR` is refused, a
// relative `XDG_CACHE_HOME` / `LOCALAPPDATA` is ignored (C3).
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

  it('ignores a relative XDG_CACHE_HOME and falls back to <home>/.cache/evidgate (C3)', () => {
    const config = resolveConfig({
      env: { ...baseEnv, XDG_CACHE_HOME: 'rel' },
      platform: 'linux',
      homedir: '/home/u',
    });
    expect(config.cacheDir).toBe('/home/u/.cache/evidgate');
  });

  it('ignores a relative LOCALAPPDATA on win32 and falls through to XDG_CACHE_HOME (C3)', () => {
    const config = resolveConfig({
      env: { ...baseEnv, LOCALAPPDATA: 'rel', XDG_CACHE_HOME: 'C:\\xdg' },
      platform: 'win32',
      homedir: 'C:\\Users\\u',
    });
    expect(config.cacheDir).toBe('C:\\xdg\\evidgate');
  });

  it('ignores a relative XDG_CACHE_HOME on win32 and falls back to <home>/.cache (C3)', () => {
    const config = resolveConfig({
      env: { ...baseEnv, XDG_CACHE_HOME: 'rel' },
      platform: 'win32',
      homedir: 'C:\\Users\\u',
    });
    expect(config.cacheDir).toBe('C:\\Users\\u\\.cache\\evidgate');
  });

  it('prints cache_dir in the verbose dump', () => {
    const config = resolveConfig({
      env: { ...baseEnv, EVIDGATE_CACHE_DIR: '/tmp/evidgate-cache' },
    });
    expect(formatConfigForVerbose(config)).toContain('cache_dir=/tmp/evidgate-cache');
  });
});

// 04.2.1-T1.1 / D4, S2: the Actions ID-token request URL is a secret (it carries a
// signed query), so the `--verbose` dump redacts it like every other secret.
describe('formatConfigForVerbose', () => {
  beforeEach(() => {
    resetSecrets();
  });

  it('redacts ACTIONS_ID_TOKEN_REQUEST_URL and never prints it (D4, S2)', () => {
    const url = 'https://actions.test/oidc?token=url-secret';
    const config = resolveConfig({
      env: {
        ...baseEnv,
        ACTIONS_ID_TOKEN_REQUEST_URL: url,
        ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'actions-secret',
      },
    });
    const dump = formatConfigForVerbose(config);
    expect(dump).toContain('actions_id_token_request_url=[REDACTED]');
    expect(dump).not.toContain(url);
    expect(dump).not.toContain('url-secret');
  });

  it('prints unset when ACTIONS_ID_TOKEN_REQUEST_URL is absent', () => {
    const dump = formatConfigForVerbose(resolveConfig({ env: baseEnv }));
    expect(dump).toContain('actions_id_token_request_url=unset');
  });
});

// 04.2.3-T1.4 / D4, D5 / S54 CR8: the spool surface is resolved LAZILY by
// `config.spoolSettings()` (or `resolveSpoolSettings(env)`), never by `resolveConfig`, so a
// malformed value only breaks the spool commands. `EVIDGATE_SPOOL_DIR` stays optional there
// (only the spool commands need it, and each raises its own `missing_config`), the cap
// defaults to 256 MiB and the deadline to 30 minutes; every invalid value is refused as
// `invalid_config` naming the variable, never echoing it.
describe('resolveConfig spool surface', () => {
  beforeEach(() => {
    resetSecrets();
  });

  it('leaves spoolDir undefined when the variable is unset or blank', () => {
    expect(resolveConfig({ env: baseEnv }).spoolSettings().spoolDir).toBeUndefined();
    const blank = resolveConfig({ env: { ...baseEnv, EVIDGATE_SPOOL_DIR: '   ' } });
    expect(blank.spoolSettings().spoolDir).toBeUndefined();
  });

  it('keeps an absolute spool directory as given', () => {
    const config = resolveConfig({
      env: { ...baseEnv, EVIDGATE_SPOOL_DIR: '/var/tmp/evidgate-spool' },
    });
    expect(config.spoolSettings().spoolDir).toBe('/var/tmp/evidgate-spool');
  });

  it('anchors a relative spool directory at the process directory (reporter parity)', () => {
    const config = resolveConfig({ env: { ...baseEnv, EVIDGATE_SPOOL_DIR: 'rel-spool' } });
    expect(config.spoolSettings().spoolDir).toBe(resolve('rel-spool'));
  });

  it('refuses a spool directory carrying a control character on first use', () => {
    // CR8: `resolveConfig` itself must not resolve the surface, so the error appears only
    // when a spool command asks for it.
    const config = resolveConfig({ env: { ...baseEnv, EVIDGATE_SPOOL_DIR: '/tmp/spool\nx' } });
    const error = captureCliError(() => config.spoolSettings());
    expect(error.code).toBe('invalid_config');
    expect(error.message).toContain('EVIDGATE_SPOOL_DIR');
    // A throwing resolution is not cached: the second call reports the same error.
    expect(captureCliError(() => config.spoolSettings()).message).toBe(error.message);
  });

  it('defaults the cap and the deadline, and echoes neither value in an error', () => {
    const settings = resolveConfig({ env: baseEnv }).spoolSettings();
    expect(settings.spoolMaxBytes).toBe(defaultSpoolMaxBytes);
    expect(defaultSpoolMaxBytes).toBe(268_435_456);
    expect(settings.sendDeadlineMs).toBe(defaultSendDeadlineMs);
    expect(defaultSendDeadlineMs).toBe(1_800_000);
  });

  // CR8 (S54): a malformed spool variable must not break a command that never touches the
  // spool, so `resolveConfig` — what every command calls — keeps working; only the lazy
  // resolver the spool commands force raises `invalid_config`.
  it('does not resolve a malformed spool variable until a spool command asks (CR8)', () => {
    const env = { ...baseEnv, EVIDGATE_SPOOL_MAX_BYTES: 'not-a-number' };
    const config = resolveConfig({ env });
    expect(config.apiUrl).toBe('https://api.evidgate.test');
    const error = captureCliError(() => config.spoolSettings());
    expect(error.code).toBe('invalid_config');
    expect(error.message).toContain('EVIDGATE_SPOOL_MAX_BYTES');
    expect(error.message).not.toContain('not-a-number');
    // The standalone resolver reports the identical error for a direct caller.
    expect(captureCliError(() => resolveSpoolSettings(env)).message).toBe(error.message);
  });

  it('resolves the settings together and memoises the successful result', () => {
    const config = resolveConfig({
      env: {
        ...baseEnv,
        EVIDGATE_SPOOL_DIR: '/var/tmp/spool',
        EVIDGATE_SPOOL_MAX_BYTES: '4096',
        EVIDGATE_SEND_DEADLINE: '2m',
      },
    });
    const settings = config.spoolSettings();
    expect(settings).toEqual({
      spoolDir: '/var/tmp/spool',
      spoolMaxBytes: 4096,
      sendDeadlineMs: 120_000,
    });
    expect(config.spoolSettings()).toBe(settings);
  });

  it('prints the spool surface in the verbose dump (paths are not secrets)', () => {
    const dump = formatConfigForVerbose(
      resolveConfig({
        env: {
          ...baseEnv,
          EVIDGATE_SPOOL_DIR: '/var/tmp/evidgate-spool',
          EVIDGATE_SPOOL_MAX_BYTES: '4096',
          EVIDGATE_SEND_DEADLINE: '90s',
        },
      }),
    );
    expect(dump).toContain('spool_dir=/var/tmp/evidgate-spool');
    expect(dump).toContain('spool_max_bytes=4096');
    expect(dump).toContain('send_deadline_ms=90000');
  });

  // CR8: the dump runs for commands that never touch the spool, so it reports an invalid
  // value as a line instead of failing the command (the spool commands already refused
  // with that error in `validateConfig`, which `main` runs before the dump).
  it('reports an invalid spool variable in the dump without throwing', () => {
    const dump = formatConfigForVerbose(
      resolveConfig({ env: { ...baseEnv, EVIDGATE_SEND_DEADLINE: 'soon' } }),
    );
    expect(dump).toContain('spool_settings=invalid (invalid_config)');
    expect(dump).not.toContain('spool_max_bytes=');
    expect(dump).not.toContain('soon');
  });
});

describe('resolveSpoolDir', () => {
  beforeEach(() => {
    resetSecrets();
  });

  it('is missing_config naming EVIDGATE_SPOOL_DIR when absent', () => {
    const error = captureCliError(() => resolveSpoolDir({}));
    expect(error.code).toBe('missing_config');
    expect(error.message).toBe(spoolDirMissingError().message);
    expect(error.message).toContain('EVIDGATE_SPOOL_DIR');
    expect(error.hint).toContain('EVIDGATE_SPOOL_DIR');
  });

  it('treats an empty value as missing, never as the empty path', () => {
    expect(captureCliError(() => resolveSpoolDir({ EVIDGATE_SPOOL_DIR: '' })).code).toBe(
      'missing_config',
    );
    expect(captureCliError(() => resolveSpoolDir({ EVIDGATE_SPOOL_DIR: '  ' })).code).toBe(
      'missing_config',
    );
  });

  it('trims and resolves an absolute value', () => {
    expect(resolveSpoolDir({ EVIDGATE_SPOOL_DIR: '  /var/tmp/spool  ' })).toBe('/var/tmp/spool');
  });
});

describe('resolveSpoolMaxBytes', () => {
  beforeEach(() => {
    resetSecrets();
  });

  it('accepts plain decimal byte counts up to 2^40', () => {
    expect(resolveSpoolMaxBytes({ EVIDGATE_SPOOL_MAX_BYTES: '1' })).toBe(1);
    expect(resolveSpoolMaxBytes({ EVIDGATE_SPOOL_MAX_BYTES: '1048576' })).toBe(1_048_576);
    expect(resolveSpoolMaxBytes({ EVIDGATE_SPOOL_MAX_BYTES: ' 2048 ' })).toBe(2048);
    expect(resolveSpoolMaxBytes({ EVIDGATE_SPOOL_MAX_BYTES: String(maxSpoolMaxBytes) })).toBe(
      maxSpoolMaxBytes,
    );
    expect(maxSpoolMaxBytes).toBe(2 ** 40);
  });

  it('defaults when the variable is unset or empty', () => {
    expect(resolveSpoolMaxBytes({})).toBe(defaultSpoolMaxBytes);
    expect(resolveSpoolMaxBytes({ EVIDGATE_SPOOL_MAX_BYTES: '' })).toBe(defaultSpoolMaxBytes);
  });

  it('refuses zero, negatives, fractions, units, junk and values above 2^40', () => {
    for (const value of [
      '0',
      '-1',
      '1.5',
      '1e3',
      '1024kb',
      'abc',
      String(maxSpoolMaxBytes + 1),
      '99999999999999999999',
    ]) {
      const error = captureCliError(() =>
        resolveSpoolMaxBytes({ EVIDGATE_SPOOL_MAX_BYTES: value }),
      );
      expect(error.code).toBe('invalid_config');
      expect(error.message).toContain('EVIDGATE_SPOOL_MAX_BYTES');
      expect(error.message).not.toContain(value);
    }
  });
});

describe('resolveSendDeadlineMs', () => {
  beforeEach(() => {
    resetSecrets();
  });

  it('reads bare seconds and the s/m/h suffixes', () => {
    expect(resolveSendDeadlineMs({ EVIDGATE_SEND_DEADLINE: '60' })).toBe(60_000);
    expect(resolveSendDeadlineMs({ EVIDGATE_SEND_DEADLINE: '90s' })).toBe(90_000);
    expect(resolveSendDeadlineMs({ EVIDGATE_SEND_DEADLINE: '5m' })).toBe(300_000);
    expect(resolveSendDeadlineMs({ EVIDGATE_SEND_DEADLINE: '2h' })).toBe(7_200_000);
    expect(resolveSendDeadlineMs({ EVIDGATE_SEND_DEADLINE: ' 30m ' })).toBe(1_800_000);
  });

  it('defaults to 30 minutes and allows the 24h ceiling', () => {
    expect(resolveSendDeadlineMs({})).toBe(defaultSendDeadlineMs);
    expect(resolveSendDeadlineMs({ EVIDGATE_SEND_DEADLINE: '' })).toBe(defaultSendDeadlineMs);
    expect(resolveSendDeadlineMs({ EVIDGATE_SEND_DEADLINE: '24h' })).toBe(maxSendDeadlineMs);
    expect(maxSendDeadlineMs).toBe(86_400_000);
  });

  it('refuses zero, fractions, unknown units and anything above 24h', () => {
    for (const value of ['0', '0s', '0m', '-5s', '1.5m', '1d', '1.5', 'abc', '25h', '86401s']) {
      const error = captureCliError(() => resolveSendDeadlineMs({ EVIDGATE_SEND_DEADLINE: value }));
      expect(error.code).toBe('invalid_config');
      expect(error.message).toContain('EVIDGATE_SEND_DEADLINE');
      expect(error.message).not.toContain(value);
    }
  });
});
