// The dev-token guard: returns `EVIDGATE_DEV_TOKEN` for the ingest exchange (`getToken`), with no cache
// of its own, and refuses unless `EVIDGATE_ENV` is exactly `dev` AND the API URL is
// loopback — fail-closed, because a dev token sent anywhere else is a leaked credential.
import { isLoopbackApiUrl, type ResolvedConfig } from '../config.js';
import { CliError } from '../errors.js';

/**
 * Returns the dev token as the bearer, or throws `dev_token_refused`/`missing_config`.
 * `getToken` only calls this when the source is `dev` (so the token is set), but the
 * function is part of the package surface (`index.ts` re-exports it), so a direct
 * caller can still pass a config without one — the guard is reachable, not dead code.
 */
export function resolveDevToken(config: ResolvedConfig): string {
  const token = config.devToken;
  if (token === undefined) {
    throw new CliError(`missing configuration: EVIDGATE_DEV_TOKEN`, { code: 'missing_config' });
  }
  if (config.env !== 'dev') {
    throw devTokenRefused(config.env);
  }
  if (!isLoopbackApiUrl(config.apiUrl)) {
    throw new CliError('EVIDGATE_DEV_TOKEN is only sent to a loopback API URL', {
      code: 'dev_token_refused',
      hint: 'point EVIDGATE_API_URL at the local dev stack (http://127.0.0.1:8080)',
    });
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
