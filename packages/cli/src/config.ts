// 04.2.1-T2.1: `EVIDGATE_*` configuration resolution (04.2.1 §5 `config.ts`, AC5).
// Precedence is explicit `--flag` > `EVIDGATE_*` env > built-in default; an empty env
// value counts as unset and a token value is never accepted from a flag (argv is
// visible in `ps`, 04.2.1 §5). Every secret read here is registered for redaction.
import { CliError } from './errors.js';
import { redacted, registerSecret } from './redact.js';

/** Flags the parser accepts from argv; token values are deliberately absent. */
export interface ConfigFlags {
  apiUrl?: string;
  project?: string;
  verbose?: boolean;
  json?: boolean;
}

export interface ResolveConfigInput {
  readonly env: Readonly<Record<string, string | undefined>>;
  readonly flags?: ConfigFlags;
  /** When true, a token source must be present or `missing_config` is raised. */
  readonly requireToken?: boolean;
}

/** Where the bearer token comes from, in §5 precedence order. */
export type TokenSource = 'oidc' | 'actions-id-token' | 'ingest' | 'dev';

export interface ResolvedConfig {
  readonly apiUrl: string;
  readonly project: string;
  readonly env: string | undefined;
  readonly oidcToken: string | undefined;
  readonly actionsIdTokenRequestUrl: string | undefined;
  readonly actionsIdTokenRequestToken: string | undefined;
  readonly ingestToken: string | undefined;
  readonly devToken: string | undefined;
  readonly bindingChallenge: string | undefined;
  readonly verbose: boolean;
  readonly json: boolean;
}

/** Every environment variable the CLI reads (04.2.1 §5). */
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
} as const;

const apiUrlNames = `${envNames.apiUrl} (or --api-url)`;
const projectNames = `${envNames.project} (or --project)`;

/** The single `missing_config` entry for "no token source is set" (04.2.1-T2 §Config). */
export const tokenSourceNames =
  `a token source: ${envNames.oidcToken}, ${envNames.actionsIdTokenRequestUrl} + ` +
  `${envNames.actionsIdTokenRequestToken}, ${envNames.ingestToken} or ${envNames.devToken}`;

const loopbackHosts: Record<string, true> = {
  localhost: true,
  '127.0.0.1': true,
  '[::1]': true,
};

const projectForbidden = /[\s\u0000-\u001f\u007f]/;

/**
 * Resolves the whole `EVIDGATE_*` surface into one frozen object. Every missing
 * required name is collected before throwing, so one run reports them all (AC5).
 */
export function resolveConfig(input: ResolveConfigInput): ResolvedConfig {
  const { env } = input;
  const flags = input.flags ?? {};

  const apiUrlRaw = flagOrEnv(flags.apiUrl, trimmedEnv(env, envNames.apiUrl));
  const projectRaw = flagOrEnv(flags.project, trimmedEnv(env, envNames.project));
  const environment = trimmedEnv(env, envNames.env)?.toLowerCase();
  const oidcToken = rawEnv(env, envNames.oidcToken);
  const actionsIdTokenRequestUrl = rawEnv(env, envNames.actionsIdTokenRequestUrl);
  const actionsIdTokenRequestToken = rawEnv(env, envNames.actionsIdTokenRequestToken);
  const ingestToken = rawEnv(env, envNames.ingestToken);
  const devToken = rawEnv(env, envNames.devToken);
  const bindingChallenge = rawEnv(env, envNames.bindingChallenge);

  // Register every secret before any validation can throw, so an error message that
  // happens to embed one is still redacted by the writer in `main` (T78).
  registerSecret(oidcToken);
  registerSecret(actionsIdTokenRequestToken);
  registerSecret(ingestToken);
  registerSecret(devToken);
  registerSecret(bindingChallenge);

  const hasTokenSource =
    oidcToken !== undefined ||
    (actionsIdTokenRequestUrl !== undefined && actionsIdTokenRequestToken !== undefined) ||
    ingestToken !== undefined ||
    devToken !== undefined;

  const missing: string[] = [];
  if (apiUrlRaw === undefined) {
    missing.push(apiUrlNames);
  }
  if (projectRaw === undefined) {
    missing.push(projectNames);
  }
  if (input.requireToken === true && !hasTokenSource) {
    missing.push(tokenSourceNames);
  }
  if (missing.length > 0) {
    throw new CliError(`missing configuration: ${missing.join(', ')}`, { code: 'missing_config' });
  }

  return Object.freeze({
    apiUrl: validateApiUrl(assertDefined(apiUrlRaw, apiUrlNames)),
    project: validateProject(assertDefined(projectRaw, projectNames)),
    env: environment,
    oidcToken,
    actionsIdTokenRequestUrl,
    actionsIdTokenRequestToken,
    ingestToken,
    devToken,
    bindingChallenge,
    verbose: flags.verbose === true,
    json: flags.json === true,
  });
}

/** The token source §5 would use, or `undefined` when none is configured. */
export function resolveTokenSource(config: ResolvedConfig): TokenSource | undefined {
  if (config.oidcToken !== undefined) {
    return 'oidc';
  }
  if (
    config.actionsIdTokenRequestUrl !== undefined &&
    config.actionsIdTokenRequestToken !== undefined
  ) {
    return 'actions-id-token';
  }
  if (config.ingestToken !== undefined) {
    return 'ingest';
  }
  if (config.devToken !== undefined) {
    return 'dev';
  }
  return undefined;
}

/** `--verbose` dump: every secret field is `[REDACTED]` or `unset` (T78). */
export function formatConfigForVerbose(config: ResolvedConfig): string {
  return [
    'evidgate: resolved config',
    `  api_url=${config.apiUrl}`,
    `  project=${config.project}`,
    `  env=${config.env ?? 'unset'}`,
    `  oidc_token=${maskSecret(config.oidcToken)}`,
    `  actions_id_token_request_url=${config.actionsIdTokenRequestUrl ?? 'unset'}`,
    `  actions_id_token_request_token=${maskSecret(config.actionsIdTokenRequestToken)}`,
    `  ingest_token=${maskSecret(config.ingestToken)}`,
    `  dev_token=${maskSecret(config.devToken)}`,
    `  binding_challenge=${maskSecret(config.bindingChallenge)}`,
    `  token_source=${resolveTokenSource(config) ?? 'none'}`,
    `  verbose=${String(config.verbose)}`,
    `  json=${String(config.json)}`,
  ].join('\n');
}

function maskSecret(value: string | undefined): string {
  return value === undefined ? 'unset' : redacted;
}

/** Raw env read: an empty string counts as unset (04.2.1-T2 §Config). */
function rawEnv(
  env: Readonly<Record<string, string | undefined>>,
  name: string,
): string | undefined {
  const value = env[name];
  return value === undefined || value === '' ? undefined : value;
}

function trimmedEnv(
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
function flagOrEnv(flag: string | undefined, envValue: string | undefined): string | undefined {
  const trimmedFlag = flag?.trim();
  if (trimmedFlag !== undefined && trimmedFlag.length > 0) {
    return trimmedFlag;
  }
  return envValue;
}

function assertDefined<T>(value: T | undefined, names: string): T {
  if (value === undefined) {
    // Unreachable: the missing-name check above already threw for this value.
    throw new CliError(`missing configuration: ${names}`, { code: 'missing_config' });
  }
  return value;
}

function invalidConfig(names: string, reason: string): CliError {
  return new CliError(`invalid configuration: ${names}: ${reason}`, { code: 'invalid_config' });
}

/**
 * Absolute URL, `https:` (or `http:` on loopback only), no userinfo, no query or
 * fragment; a trailing `/` is normalised away (04.2.1-T2 §Config).
 */
function validateApiUrl(value: string): string {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw invalidConfig(apiUrlNames, `'${value}' is not an absolute URL`);
  }
  if (url.username !== '' || url.password !== '') {
    throw invalidConfig(apiUrlNames, 'must not contain userinfo');
  }
  if (url.search !== '' || url.hash !== '') {
    throw invalidConfig(apiUrlNames, 'must not contain a query or fragment');
  }
  if (url.protocol === 'http:') {
    if (loopbackHosts[url.hostname] !== true) {
      throw invalidConfig(
        apiUrlNames,
        'http: is only allowed for loopback hosts (localhost, 127.0.0.1, [::1])',
      );
    }
  } else if (url.protocol !== 'https:') {
    throw invalidConfig(apiUrlNames, `unsupported scheme '${url.protocol}' (expected https:)`);
  }
  return url.href.replace(/\/+$/, '');
}

function validateProject(value: string): string {
  if (projectForbidden.test(value)) {
    throw invalidConfig(projectNames, 'must not contain whitespace or control characters');
  }
  return value;
}
