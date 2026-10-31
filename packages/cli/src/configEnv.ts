// The environment surface: the variable names, the three read helpers every resolver uses
// (empty counts as unset, env values are trimmed, an explicit flag wins) and the secret
// registration that must run before any validation can throw.
import { CliError } from './errors.js';
import { registerSecret } from './redact.js';

/** Every environment variable the CLI reads. */
export const envNames = {
  apiUrl: 'EVIDGATE_API_URL',
  project: 'EVIDGATE_PROJECT',
  env: 'EVIDGATE_ENV',
  oidcToken: 'EVIDGATE_OIDC_TOKEN',
  actionsIdTokenRequestUrl: 'ACTIONS_ID_TOKEN_REQUEST_URL',
  actionsIdTokenRequestToken: 'ACTIONS_ID_TOKEN_REQUEST_TOKEN',
  ingestToken: 'EVIDGATE_INGEST_TOKEN',
  devToken: 'EVIDGATE_DEV_TOKEN',
  bindingChallenge: 'EVIDGATE_BINDING_CHALLENGE',
  cacheDir: 'EVIDGATE_CACHE_DIR',
  spoolDir: 'EVIDGATE_SPOOL_DIR',
  spoolMaxBytes: 'EVIDGATE_SPOOL_MAX_BYTES',
  sendDeadline: 'EVIDGATE_SEND_DEADLINE',
} as const;

export const apiUrlNames = `${envNames.apiUrl} (or --api-url)`;
export const projectNames = `${envNames.project} (or --project)`;

/** The single `missing_config` entry for "no token source is set". */
export const tokenSourceNames =
  `a token source: ${envNames.oidcToken}, ${envNames.actionsIdTokenRequestUrl} + ` +
  `${envNames.actionsIdTokenRequestToken}, ${envNames.ingestToken} or ${envNames.devToken}`;

/**
 * Registers every secret env value for redaction. `main` calls it before parsing argv
 * so a usage error that echoes a secret value is still redacted; `resolveConfig`
 * calls it too, so a direct caller is covered.
 */
export function registerEnvSecrets(env: Readonly<Record<string, string | undefined>>): void {
  registerSecret(rawEnv(env, envNames.oidcToken));
  // The request URL carries a signed query, so it is a secret too.
  registerSecret(rawEnv(env, envNames.actionsIdTokenRequestUrl));
  registerSecret(rawEnv(env, envNames.actionsIdTokenRequestToken));
  registerSecret(rawEnv(env, envNames.ingestToken));
  registerSecret(rawEnv(env, envNames.devToken));
  registerSecret(rawEnv(env, envNames.bindingChallenge));
}

/** Raw env read: an empty string counts as unset. */
export function rawEnv(
  env: Readonly<Record<string, string | undefined>>,
  name: string,
): string | undefined {
  const value = env[name];
  return value === undefined || value === '' ? undefined : value;
}

export function trimmedEnv(
  env: Readonly<Record<string, string | undefined>>,
  name: string,
): string | undefined {
  const value = rawEnv(env, name);
  if (value === undefined) {
    return undefined;
  }
  const trimmed = value.trim();
  return trimmed.length === 0 ? undefined : trimmed;
}

/** Explicit flag wins; a flag that is absent or blank falls back to the env value. */
export function flagOrEnv(
  flag: string | undefined,
  envValue: string | undefined,
): string | undefined {
  const trimmedFlag = flag?.trim();
  if (trimmedFlag !== undefined && trimmedFlag.length > 0) {
    return trimmedFlag;
  }
  return envValue;
}

export function invalidConfig(names: string, reason: string): CliError {
  return new CliError(`invalid configuration: ${names}: ${reason}`, { code: 'invalid_config' });
}
