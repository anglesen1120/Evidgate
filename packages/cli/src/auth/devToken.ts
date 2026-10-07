// 04.2.1-T2.2: the M1 dev-token path (04.2.1 §5 `auth/dev-token.ts`, AC8). It returns
// `EVIDGATE_DEV_TOKEN` as the bearer with no exchange, no cache and no file write, and
// is refused unless `EVIDGATE_ENV` is exactly `dev` — a fail-closed decision stricter
// than AC8's minimum (04.2.1-T2 contract §Token sources). 04.2.1-T1 replaces the
// caller's branch with the real exchange.
import type { ResolvedConfig } from '../config.js';
import { CliError } from '../errors.js';

/** Returns the dev token as the bearer, or throws `dev_token_refused`/`missing_config`. */
export function resolveDevToken(config: ResolvedConfig): string {
  const token = config.devToken;
  if (token === undefined) {
    throw new CliError(`missing configuration: EVIDGATE_DEV_TOKEN`, { code: 'missing_config' });
  }
  if (config.env !== 'dev') {
    throw devTokenRefused(config.env);
  }
  return token;
}

function devTokenRefused(environment: string | undefined): CliError {
  if (environment === undefined) {
    return new CliError('EVIDGATE_DEV_TOKEN is refused when EVIDGATE_ENV is unset', {
      code: 'dev_token_refused',
      hint: 'set EVIDGATE_ENV=dev',
    });
  }
  if (environment === 'staging' || environment === 'prod') {
    // No hint: pointing a staging/prod run at `dev` is not a remediation.
    return new CliError(`EVIDGATE_DEV_TOKEN is refused when EVIDGATE_ENV=${environment}`, {
      code: 'dev_token_refused',
    });
  }
  return new CliError(`EVIDGATE_DEV_TOKEN is refused when EVIDGATE_ENV=${environment}`, {
    code: 'dev_token_refused',
    hint: 'set EVIDGATE_ENV=dev',
  });
}
