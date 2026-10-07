import { beforeEach, describe, expect, it } from 'vitest';

import { resolveConfig, type ResolvedConfig } from '../config.js';
import { CliError } from '../errors.js';
import { resetSecrets } from '../redact.js';

import { resolveDevToken } from './devToken.js';

const baseEnv = {
  EVIDGATE_API_URL: 'http://127.0.0.1:8080',
  EVIDGATE_PROJECT: 'proj-1',
  EVIDGATE_DEV_TOKEN: 'dev-token-value',
};

function configFor(env: Record<string, string | undefined>): ResolvedConfig {
  return resolveConfig({ env });
}

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

// 04.2.1-T2.2 / AC8: the dev token is the bearer only on `EVIDGATE_ENV=dev`.
describe('resolveDevToken', () => {
  beforeEach(() => {
    resetSecrets();
  });

  it('returns the dev token as the bearer when EVIDGATE_ENV=dev (AC8)', () => {
    expect(resolveDevToken(configFor({ ...baseEnv, EVIDGATE_ENV: 'dev' }))).toBe('dev-token-value');
  });

  it('accepts a case-insensitive dev environment', () => {
    expect(resolveDevToken(configFor({ ...baseEnv, EVIDGATE_ENV: 'DEV' }))).toBe('dev-token-value');
  });

  it('refuses the dev token on staging and prod (AC8)', () => {
    for (const environment of ['staging', 'prod']) {
      const error = captureCliError(() =>
        resolveDevToken(configFor({ ...baseEnv, EVIDGATE_ENV: environment })),
      );
      expect(error.code).toBe('dev_token_refused');
      expect(error.message).toContain(
        `EVIDGATE_DEV_TOKEN is refused when EVIDGATE_ENV=${environment}`,
      );
      expect(error.hint).toBeUndefined();
    }
  });

  it('refuses the dev token when EVIDGATE_ENV is unset, with a hint', () => {
    const error = captureCliError(() => resolveDevToken(configFor(baseEnv)));
    expect(error.code).toBe('dev_token_refused');
    expect(error.message).toContain('EVIDGATE_ENV is unset');
    expect(error.hint).toBe('set EVIDGATE_ENV=dev');
  });

  it('refuses the dev token on any other environment, with a hint', () => {
    const error = captureCliError(() =>
      resolveDevToken(configFor({ ...baseEnv, EVIDGATE_ENV: 'test' })),
    );
    expect(error.code).toBe('dev_token_refused');
    expect(error.message).toContain('EVIDGATE_ENV=test');
    expect(error.hint).toBe('set EVIDGATE_ENV=dev');
  });

  it('refuses the dev token when the API URL is not loopback (SEC2)', () => {
    const error = captureCliError(() =>
      resolveDevToken(
        configFor({
          ...baseEnv,
          EVIDGATE_API_URL: 'https://api.evidgate.test',
          EVIDGATE_ENV: 'dev',
        }),
      ),
    );
    expect(error.code).toBe('dev_token_refused');
    expect(error.message).toBe('EVIDGATE_DEV_TOKEN is only sent to a loopback API URL');
    expect(error.hint).toBe(
      'point EVIDGATE_API_URL at the local dev stack (http://127.0.0.1:8080)',
    );
  });

  it('checks EVIDGATE_ENV before the API target (SEC2)', () => {
    const error = captureCliError(() =>
      resolveDevToken(
        configFor({
          ...baseEnv,
          EVIDGATE_API_URL: 'https://api.evidgate.test',
          EVIDGATE_ENV: 'staging',
        }),
      ),
    );
    expect(error.message).toContain('EVIDGATE_ENV=staging');
    expect(error.hint).toBeUndefined();
  });

  it('raises missing_config when no dev token is set', () => {
    const error = captureCliError(() =>
      resolveDevToken(
        configFor({
          EVIDGATE_API_URL: baseEnv.EVIDGATE_API_URL,
          EVIDGATE_PROJECT: baseEnv.EVIDGATE_PROJECT,
        }),
      ),
    );
    expect(error.code).toBe('missing_config');
  });
});
