import { beforeEach, describe, expect, it } from 'vitest';

import { resolveConfig, resolveTokenSource, tokenSourceNames } from './config.js';
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
    expect(error.exitCode).toBe(3);
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
});
