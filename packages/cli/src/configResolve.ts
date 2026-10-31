// Resolving the `EVIDGATE_*` surface into one frozen object: precedence is `--flag` > env >
// default, an empty env value counts as unset, and no token is accepted from a flag because
// argv is visible in `ps`. Every secret is registered for redaction before validation can
// throw, and every missing required name is collected before the single `missing_config`.
import { homedir as osHomedir } from 'node:os';
import { posix, win32 } from 'node:path';

import { CliError } from './errors.js';
import { redacted } from './redact.js';
import {
  apiUrlNames,
  envNames,
  flagOrEnv,
  invalidConfig,
  projectNames,
  rawEnv,
  registerEnvSecrets,
  tokenSourceNames,
  trimmedEnv,
} from './configEnv.js';
import { resolveSpoolSettings } from './configSpool.js';

import type {
  ResolveConfigInput,
  ResolvedConfig,
  SpoolSettings,
  TokenSource,
  TokenSourceFields,
} from './configTypes.js';

const loopbackHosts: Record<string, true> = {
  localhost: true,
  '127.0.0.1': true,
  '[::1]': true,
};

/** True when `apiUrl` targets one of the loopback hosts `http:` is allowed on. */
export function isLoopbackApiUrl(apiUrl: string): boolean {
  return loopbackHosts[new URL(apiUrl).hostname] === true;
}

/** `EVIDGATE_ENV` after trim + lower-case: a short, log-safe label. */
const envPattern = /^[a-z0-9][a-z0-9._-]{0,31}$/;

/** Whitespace, C0/C1 controls and Unicode format characters are all rejected. */
const projectForbidden = /[\s\p{Cc}\p{Cf}]/u;

/** The secret fields as read from the environment, before any validation. */
interface TokenEnv {
  readonly oidcToken: string | undefined;
  readonly actionsIdTokenRequestUrl: string | undefined;
  readonly actionsIdTokenRequestToken: string | undefined;
  readonly ingestToken: string | undefined;
  readonly devToken: string | undefined;
  readonly bindingChallenge: string | undefined;
}

/**
 * Resolves the whole `EVIDGATE_*` surface into one frozen object. Every missing
 * required name is collected before throwing, so one run reports them all.
 */
export function resolveConfig(input: ResolveConfigInput): ResolvedConfig {
  const { env } = input;
  const flags = input.flags ?? {};

  // Register every secret before any validation can throw, so an error message that
  // happens to embed one is still redacted by the writer in `main`.
  registerEnvSecrets(env);

  const apiUrlRaw = flagOrEnv(flags.apiUrl, trimmedEnv(env, envNames.apiUrl));
  const projectRaw = flagOrEnv(flags.project, trimmedEnv(env, envNames.project));
  const environment = resolveEnvironment(env);
  const tokens = readTokenEnv(env);
  const required = requireConfiguration(input, apiUrlRaw, projectRaw, tokens);

  const cacheDir = resolveCacheDir(
    env,
    input.platform ?? process.platform,
    input.homedir ?? osHomedir(),
  );

  return Object.freeze({
    apiUrl: validateApiUrl(required.apiUrl),
    project: validateProject(required.project),
    cacheDir,
    env: environment,
    oidcToken: tokens.oidcToken,
    actionsIdTokenRequestUrl: tokens.actionsIdTokenRequestUrl,
    actionsIdTokenRequestToken: tokens.actionsIdTokenRequestToken,
    ingestToken: tokens.ingestToken,
    devToken: tokens.devToken,
    bindingChallenge: tokens.bindingChallenge,
    spoolSettings: lazySpoolSettings(env),
    verbose: flags.verbose === true,
    json: flags.json === true,
  });
}

/** `EVIDGATE_ENV` trimmed and lower-cased; a value outside the label shape is refused. */
function resolveEnvironment(env: Readonly<Record<string, string | undefined>>): string | undefined {
  const environment = trimmedEnv(env, envNames.env)?.toLowerCase();
  if (environment !== undefined && !envPattern.test(environment)) {
    throw invalidConfig(envNames.env, 'has an invalid value');
  }
  return environment;
}

/** Every secret-bearing env value, read raw (empty counts as unset). */
function readTokenEnv(env: Readonly<Record<string, string | undefined>>): TokenEnv {
  return {
    oidcToken: rawEnv(env, envNames.oidcToken),
    actionsIdTokenRequestUrl: rawEnv(env, envNames.actionsIdTokenRequestUrl),
    actionsIdTokenRequestToken: rawEnv(env, envNames.actionsIdTokenRequestToken),
    ingestToken: rawEnv(env, envNames.ingestToken),
    devToken: rawEnv(env, envNames.devToken),
    bindingChallenge: rawEnv(env, envNames.bindingChallenge),
  };
}

/**
 * Returns the two required values, or throws the one `missing_config` that names every
 * absent one — the token source only when the caller asked for it.
 */
function requireConfiguration(
  input: ResolveConfigInput,
  apiUrlRaw: string | undefined,
  projectRaw: string | undefined,
  tokens: TokenEnv,
): { readonly apiUrl: string; readonly project: string } {
  const hasTokenSource = resolveTokenSource(tokens) !== undefined;
  const needToken = input.requireToken === true && !hasTokenSource;
  if (apiUrlRaw !== undefined && projectRaw !== undefined && !needToken) {
    return { apiUrl: apiUrlRaw, project: projectRaw };
  }

  const missing: string[] = [];
  if (apiUrlRaw === undefined) {
    missing.push(apiUrlNames);
  }
  if (projectRaw === undefined) {
    missing.push(projectNames);
  }
  if (needToken) {
    missing.push(tokenSourceNames);
  }
  throw new CliError(`missing configuration: ${missing.join(', ')}`, { code: 'missing_config' });
}

/**
 * The spool surface stays unresolved until the first call. The first call resolves all three
 * values and memoises them; a throwing resolution is not cached, so every later call reports
 * exactly the same error.
 */
function lazySpoolSettings(env: Readonly<Record<string, string | undefined>>): () => SpoolSettings {
  let spool: SpoolSettings | undefined;
  return () => {
    spool ??= resolveSpoolSettings(env);
    return spool;
  };
}

/** The token source §5 would use, or `undefined` when none is configured. */
export function resolveTokenSource(config: TokenSourceFields): TokenSource | undefined {
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

/** `--verbose` dump: every secret field is `[REDACTED]` or `unset`. */
export function formatConfigForVerbose(config: ResolvedConfig): string {
  return [
    'evidgate: resolved config',
    `  api_url=${config.apiUrl}`,
    `  project=${config.project}`,
    `  cache_dir=${config.cacheDir}`,
    `  env=${config.env ?? 'unset'}`,
    `  oidc_token=${maskSecret(config.oidcToken)}`,
    `  actions_id_token_request_url=${maskSecret(config.actionsIdTokenRequestUrl)}`,
    `  actions_id_token_request_token=${maskSecret(config.actionsIdTokenRequestToken)}`,
    `  ingest_token=${maskSecret(config.ingestToken)}`,
    `  dev_token=${maskSecret(config.devToken)}`,
    `  binding_challenge=${maskSecret(config.bindingChallenge)}`,
    // Paths and counts are not secrets: the operator needs to see which spool and cap a
    // send will use.
    ...spoolDumpLines(config),
    `  token_source=${resolveTokenSource(config) ?? 'none'}`,
    `  verbose=${String(config.verbose)}`,
    `  json=${String(config.json)}`,
  ].join('\n');
}

/**
 * The spool lines of the `--verbose` dump. The surface is lazy and this dump runs for
 * commands that never touch the spool, so an invalid value is reported as
 * `spool_settings=invalid (<code>)` instead of failing the command; the spool commands
 * themselves already refused with that error before `main` reaches this dump.
 */
function spoolDumpLines(config: ResolvedConfig): string[] {
  let settings: SpoolSettings;
  try {
    settings = config.spoolSettings();
  } catch (error) {
    const code = error instanceof CliError ? error.code : 'invalid_config';
    return [`  spool_settings=invalid (${code})`];
  }
  return [
    `  spool_dir=${settings.spoolDir ?? 'unset'}`,
    `  spool_max_bytes=${String(settings.spoolMaxBytes)}`,
    `  send_deadline_ms=${String(settings.sendDeadlineMs)}`,
  ];
}

function maskSecret(value: string | undefined): string {
  return value === undefined ? 'unset' : redacted;
}

/**
 * The token-cache directory: `EVIDGATE_CACHE_DIR` as-is, else the platform cache dir
 * under `evidgate`. A relative override is `invalid_config` naming the variable only, so
 * the value is never echoed; a relative `XDG_CACHE_HOME`/`LOCALAPPDATA` is ignored per the
 * XDG spec and the next default is used rather than failing.
 */
function resolveCacheDir(
  env: Readonly<Record<string, string | undefined>>,
  platform: NodeJS.Platform,
  home: string,
): string {
  const paths = platform === 'win32' ? win32 : posix;
  const explicit = rawEnv(env, envNames.cacheDir);
  if (explicit !== undefined) {
    return requireAbsolutePath(explicit, envNames.cacheDir, paths);
  }
  if (platform === 'win32') {
    const localAppData = rawEnv(env, 'LOCALAPPDATA');
    if (localAppData !== undefined && paths.isAbsolute(localAppData)) {
      return paths.join(localAppData, 'evidgate');
    }
  }
  const xdgCacheHome = rawEnv(env, 'XDG_CACHE_HOME');
  if (xdgCacheHome !== undefined && paths.isAbsolute(xdgCacheHome)) {
    return paths.join(xdgCacheHome, 'evidgate');
  }
  return paths.join(home, '.cache', 'evidgate');
}

function requireAbsolutePath(value: string, name: string, paths: typeof posix): string {
  if (!paths.isAbsolute(value)) {
    throw invalidConfig(name, 'must be an absolute path');
  }
  return value;
}

/**
 * Absolute URL, `https:` (or `http:` on loopback only), without userinfo, query or
 * fragment; a trailing `/` is normalised away. The raw value is never echoed because it
 * may carry credentials.
 */
function validateApiUrl(value: string): string {
  if (value.includes('?') || value.includes('#')) {
    throw invalidConfig(apiUrlNames, 'must not contain a query or fragment');
  }
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw invalidConfig(apiUrlNames, 'is not an absolute URL');
  }
  if (url.username !== '' || url.password !== '') {
    throw invalidConfig(apiUrlNames, 'must not contain userinfo');
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
  return `${url.origin}${url.pathname.replace(/\/+$/, '')}`;
}

function validateProject(value: string): string {
  if (projectForbidden.test(value)) {
    throw invalidConfig(projectNames, 'must not contain whitespace or control characters');
  }
  return value;
}
