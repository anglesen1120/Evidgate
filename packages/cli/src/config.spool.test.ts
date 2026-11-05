import { resolve } from 'node:path';

import { beforeEach, describe, expect, it } from 'vitest';

import {
  defaultSendDeadlineMs,
  defaultSpoolMaxBytes,
  formatConfigForVerbose,
  maxSendDeadlineMs,
  maxSpoolMaxBytes,
  resolveConfig,
  resolveSendDeadlineMs,
  resolveSpoolDir,
  resolveSpoolMaxBytes,
  resolveSpoolSettings,
  spoolDirMissingError,
} from './config.js';
import { baseEnv } from './config.testing.js';
import { captureCliError } from './commands/cliSpec.testing.js';
import { resetSecrets } from './redact.js';

// The spool surface is resolved LAZILY by `config.spoolSettings()` (or
// `resolveSpoolSettings(env)`), never by `resolveConfig`, so a malformed value only breaks the
// spool commands. `EVIDGATE_SPOOL_DIR` stays optional there (only the spool commands need it,
// and each raises its own `missing_config`), the cap defaults to 256 MiB and the deadline to 30
// minutes; every invalid value is refused as `invalid_config` naming the variable, never
// echoing it.
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
    // `resolveConfig` itself must not resolve the surface, so the error appears only when a
    // spool command asks for it.
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

  // A malformed spool variable must not break a command that never touches the spool, so
  // `resolveConfig` — what every command calls — keeps working; only the lazy resolver the
  // spool commands force raises `invalid_config`.
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

  // The dump runs for commands that never touch the spool, so it reports an invalid value as a
  // line instead of failing the command (the spool commands already refused with that error in
  // `validateConfig`, which `main` runs before the dump).
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
