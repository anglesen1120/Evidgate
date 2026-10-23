// 04.2.1-T2.1: `EVIDGATE_*` configuration resolution (04.2.1 §5 `config.ts`, AC5).
// Precedence is explicit `--flag` > `EVIDGATE_*` env > built-in default; an empty env
// value counts as unset and a token value is never accepted from a flag (argv is
// visible in `ps`, 04.2.1 §5). Every secret read here is registered for redaction.
// 04.2.1-T1.1: adds `cacheDir` (the token-cache location, §5 `auth/token-cache.ts`).
// 04.2.3-T1.4: adds the spool surface — `spoolDir` (`EVIDGATE_SPOOL_DIR`, D5, optional
// here because only the spool commands need it and they raise `missing_config`
// themselves), `spoolMaxBytes` (`EVIDGATE_SPOOL_MAX_BYTES`, D4) and `sendDeadlineMs`
// (`EVIDGATE_SEND_DEADLINE`, D5). All three are env-only: no flag can carry them.
import { homedir as osHomedir } from 'node:os';
import { posix, resolve, win32 } from 'node:path';

import { CliError } from './errors.js';
import { redacted, registerSecret } from './redact.js';
import { defaultSpoolMaxBytes } from './spool.js';

// The cap's default is part of this module's surface: `EVIDGATE_SPOOL_MAX_BYTES` is
// resolved here, and the tests document the default without importing the spool module.
export { defaultSpoolMaxBytes };

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
  /** Target platform for the cache-dir default; defaults to `process.platform`. */
  readonly platform?: NodeJS.Platform;
  /** Home directory for the cache-dir default; defaults to `os.homedir()`. */
  readonly homedir?: string;
}

/** Where the bearer token comes from, in §5 precedence order. */
export type TokenSource = 'oidc' | 'actions-id-token' | 'ingest' | 'dev';

export interface ResolvedConfig {
  readonly apiUrl: string;
  readonly project: string;
  /** Absolute token-cache directory (04.2.1-T1.1); the platform default ends in `evidgate`. */
  readonly cacheDir: string;
  readonly env: string | undefined;
  readonly oidcToken: string | undefined;
  readonly actionsIdTokenRequestUrl: string | undefined;
  readonly actionsIdTokenRequestToken: string | undefined;
  readonly ingestToken: string | undefined;
  readonly devToken: string | undefined;
  readonly bindingChallenge: string | undefined;
  /**
   * Absolute spool root (`EVIDGATE_SPOOL_DIR`, 04.2.3 D5), or `undefined` when the
   * variable is unset. Optional on purpose: only `events send` / `runs finalize` need it,
   * and each raises its own `missing_config` (via {@link resolveSpoolDir}), so a command
   * that never touches the spool is unaffected.
   */
  readonly spoolDir: string | undefined;
  /** The run-directory byte cap (`EVIDGATE_SPOOL_MAX_BYTES`, 04.2.3 D4). */
  readonly spoolMaxBytes: number;
  /** The one send deadline in milliseconds (`EVIDGATE_SEND_DEADLINE`, 04.2.3 D5). */
  readonly sendDeadlineMs: number;
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
  cacheDir: 'EVIDGATE_CACHE_DIR',
  spoolDir: 'EVIDGATE_SPOOL_DIR',
  spoolMaxBytes: 'EVIDGATE_SPOOL_MAX_BYTES',
  sendDeadline: 'EVIDGATE_SEND_DEADLINE',
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

/** True when `apiUrl` targets one of the loopback hosts `http:` is allowed on. */
export function isLoopbackApiUrl(apiUrl: string): boolean {
  return loopbackHosts[new URL(apiUrl).hostname] === true;
}

/** `EVIDGATE_ENV` after trim + lower-case: a short, log-safe label (04.2.1-T2 §Config). */
const envPattern = /^[a-z0-9][a-z0-9._-]{0,31}$/;

/** Whitespace, C0/C1 controls and Unicode format characters are all rejected. */
const projectForbidden = /[\s\p{Cc}\p{Cf}]/u;

/**
 * Paths may contain spaces (`/Users/x/My Spool` is legal), but never a control or format
 * character: such a value could forge a line in any log or cursor file it lands in.
 */
const pathForbidden = /[\p{Cc}\p{Cf}]/u;

/**
 * The largest accepted `EVIDGATE_SPOOL_MAX_BYTES`: 2^40 bytes (1 TiB). Above it the
 * value is a typo (a 32-bit overflow or a unit mix-up), and the spool's own accounting
 * could no longer be reasoned about; the spool then fails with `invalid_config`.
 */
export const maxSpoolMaxBytes = 1_099_511_627_776;

/** `EVIDGATE_SEND_DEADLINE` default: 30 minutes for the whole send (04.2.3 D5). */
export const defaultSendDeadlineMs = 1_800_000;

/** `EVIDGATE_SEND_DEADLINE` ceiling: 24 hours (04.2.3 D5). */
export const maxSendDeadlineMs = 86_400_000;

/** A bare decimal integer, used for the byte count and for bare deadline seconds. */
const decimalPattern = /^[0-9]{1,20}$/;

/**
 * `EVIDGATE_SEND_DEADLINE`: a bare integer (seconds) or `<n>s`, `<n>m`, `<n>h`. The
 * amount is bounded to 9 digits so `amount x 3 600 000` can never leave the safe range.
 */
const deadlinePattern = /^([0-9]{1,9})([smh]?)$/;

/**
 * Registers every secret env value for redaction. `main` calls it before parsing argv
 * so a usage error that echoes a secret value is still redacted (QA-D1); `resolveConfig`
 * calls it too, so a direct caller is covered.
 */
export function registerEnvSecrets(env: Readonly<Record<string, string | undefined>>): void {
  registerSecret(rawEnv(env, envNames.oidcToken));
  // 04.2.1-T1.1 (D4/S2): the request URL carries a signed query, so it is a secret too.
  registerSecret(rawEnv(env, envNames.actionsIdTokenRequestUrl));
  registerSecret(rawEnv(env, envNames.actionsIdTokenRequestToken));
  registerSecret(rawEnv(env, envNames.ingestToken));
  registerSecret(rawEnv(env, envNames.devToken));
  registerSecret(rawEnv(env, envNames.bindingChallenge));
}

/**
 * Resolves the whole `EVIDGATE_*` surface into one frozen object. Every missing
 * required name is collected before throwing, so one run reports them all (AC5).
 */
export function resolveConfig(input: ResolveConfigInput): ResolvedConfig {
  const { env } = input;
  const flags = input.flags ?? {};

  // Register every secret before any validation can throw, so an error message that
  // happens to embed one is still redacted by the writer in `main` (T78).
  registerEnvSecrets(env);

  const apiUrlRaw = flagOrEnv(flags.apiUrl, trimmedEnv(env, envNames.apiUrl));
  const projectRaw = flagOrEnv(flags.project, trimmedEnv(env, envNames.project));
  const environment = trimmedEnv(env, envNames.env)?.toLowerCase();
  if (environment !== undefined && !envPattern.test(environment)) {
    throw invalidConfig(envNames.env, 'has an invalid value');
  }
  const oidcToken = rawEnv(env, envNames.oidcToken);
  const actionsIdTokenRequestUrl = rawEnv(env, envNames.actionsIdTokenRequestUrl);
  const actionsIdTokenRequestToken = rawEnv(env, envNames.actionsIdTokenRequestToken);
  const ingestToken = rawEnv(env, envNames.ingestToken);
  const devToken = rawEnv(env, envNames.devToken);
  const bindingChallenge = rawEnv(env, envNames.bindingChallenge);

  const hasTokenSource =
    resolveTokenSource({
      oidcToken,
      actionsIdTokenRequestUrl,
      actionsIdTokenRequestToken,
      ingestToken,
      devToken,
    }) !== undefined;

  if (
    apiUrlRaw === undefined ||
    projectRaw === undefined ||
    (input.requireToken === true && !hasTokenSource)
  ) {
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
    throw new CliError(`missing configuration: ${missing.join(', ')}`, { code: 'missing_config' });
  }

  const cacheDir = resolveCacheDir(
    env,
    input.platform ?? process.platform,
    input.homedir ?? osHomedir(),
  );

  return Object.freeze({
    apiUrl: validateApiUrl(apiUrlRaw),
    project: validateProject(projectRaw),
    cacheDir,
    env: environment,
    oidcToken,
    actionsIdTokenRequestUrl,
    actionsIdTokenRequestToken,
    ingestToken,
    devToken,
    bindingChallenge,
    spoolDir: resolveOptionalSpoolDir(env),
    spoolMaxBytes: resolveSpoolMaxBytes(env),
    sendDeadlineMs: resolveSendDeadlineMs(env),
    verbose: flags.verbose === true,
    json: flags.json === true,
  });
}

/** The fields `resolveTokenSource` reads; `ResolvedConfig` satisfies it structurally. */
export type TokenSourceFields = Pick<
  ResolvedConfig,
  | 'oidcToken'
  | 'actionsIdTokenRequestUrl'
  | 'actionsIdTokenRequestToken'
  | 'ingestToken'
  | 'devToken'
>;

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

/** `--verbose` dump: every secret field is `[REDACTED]` or `unset` (T78). */
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
    // Paths and counts are not secrets (04.2.3 T78 scope): the operator needs to see
    // which spool and cap a send will use.
    `  spool_dir=${config.spoolDir ?? 'unset'}`,
    `  spool_max_bytes=${String(config.spoolMaxBytes)}`,
    `  send_deadline_ms=${String(config.sendDeadlineMs)}`,
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

function invalidConfig(names: string, reason: string): CliError {
  return new CliError(`invalid configuration: ${names}: ${reason}`, { code: 'invalid_config' });
}

/**
 * The token-cache directory (04.2.1 §5 `auth/token-cache.ts`): `EVIDGATE_CACHE_DIR`
 * used as-is, else `%LOCALAPPDATA%\evidgate` on Windows, else
 * `$XDG_CACHE_HOME/evidgate`, else `<home>/.cache/evidgate`. `EVIDGATE_CACHE_DIR` must
 * be an absolute path; a relative one is `invalid_config` naming the variable and never
 * echoing the value (SEC3). A relative `XDG_CACHE_HOME` / `LOCALAPPDATA` is ignored and
 * the next default is used (XDG Base Directory spec), never an error.
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
 * The `missing_config` entry for "no spool root is set" (04.2.3 D5). Exported so a
 * command can refuse the configuration in its `validateConfig` pre-flight, before the
 * `--verbose` dump and before any token work.
 */
export function spoolDirMissingError(): CliError {
  return new CliError(`missing configuration: ${envNames.spoolDir}`, {
    code: 'missing_config',
    hint: 'set EVIDGATE_SPOOL_DIR to the directory your reporter writes the spool to',
  });
}

/**
 * `EVIDGATE_SPOOL_DIR` (04.2.3 D5): required by the spool commands, with no default.
 * The value is trimmed and absolute-resolved, so a relative one is anchored at the
 * process's current directory — the same rule the merged reporter applies when it joins
 * `<spool_dir>/<run_id>/<producer_id>`. An unset, empty or blank value is
 * `missing_config` naming the variable, never a path; a control character is
 * `invalid_config`, so a value can neither forge a log line nor reach the filesystem.
 */
export function resolveSpoolDir(env: Readonly<Record<string, string | undefined>>): string {
  const directory = resolveOptionalSpoolDir(env);
  if (directory === undefined) {
    throw spoolDirMissingError();
  }
  return directory;
}

/** The same read as {@link resolveSpoolDir}, but an unset variable is `undefined`. */
function resolveOptionalSpoolDir(
  env: Readonly<Record<string, string | undefined>>,
): string | undefined {
  const value = trimmedEnv(env, envNames.spoolDir);
  if (value === undefined) {
    return undefined;
  }
  if (pathForbidden.test(value)) {
    throw invalidConfig(envNames.spoolDir, 'must not contain control characters');
  }
  return resolve(value);
}

/**
 * `EVIDGATE_SPOOL_MAX_BYTES` (04.2.3 D4): the run-directory cap, a positive decimal byte
 * count, defaulting to 256 MiB ({@link defaultSpoolMaxBytes}). Zero, a negative or
 * fractional value, a unit suffix, junk and anything above {@link maxSpoolMaxBytes} is
 * `invalid_config` naming the variable; the raw value is never echoed (it could forge a
 * log line and is never needed to fix the value).
 */
export function resolveSpoolMaxBytes(env: Readonly<Record<string, string | undefined>>): number {
  const value = trimmedEnv(env, envNames.spoolMaxBytes);
  if (value === undefined) {
    return defaultSpoolMaxBytes;
  }
  const parsed = decimalPattern.test(value) ? Number(value) : Number.NaN;
  if (!Number.isSafeInteger(parsed) || parsed <= 0) {
    throw invalidConfig(envNames.spoolMaxBytes, 'must be a positive integer number of bytes');
  }
  if (parsed > maxSpoolMaxBytes) {
    throw invalidConfig(
      envNames.spoolMaxBytes,
      `must be at most ${maxSpoolMaxBytes} bytes (1 TiB)`,
    );
  }
  return parsed;
}

/**
 * `EVIDGATE_SEND_DEADLINE` (04.2.3 D5): one deadline for the whole send, as a bare
 * integer number of seconds or `<n>s` / `<n>m` / `<n>h`. Default 30 minutes, ceiling
 * 24 hours; zero, a fraction, an unknown unit and junk are `invalid_config`.
 */
export function resolveSendDeadlineMs(env: Readonly<Record<string, string | undefined>>): number {
  const value = trimmedEnv(env, envNames.sendDeadline);
  if (value === undefined) {
    return defaultSendDeadlineMs;
  }
  const match = deadlinePattern.exec(value);
  if (match === null) {
    throw invalidConfig(
      envNames.sendDeadline,
      'must be an integer number of seconds, or <n>s, <n>m or <n>h',
    );
  }
  const amount = Number(match[1]);
  const unit = match[2] ?? 's';
  const factor = unit === 'h' ? 3_600_000 : unit === 'm' ? 60_000 : 1_000;
  const milliseconds = amount * factor;
  if (!Number.isSafeInteger(milliseconds) || milliseconds <= 0) {
    throw invalidConfig(envNames.sendDeadline, 'must be greater than zero');
  }
  if (milliseconds > maxSendDeadlineMs) {
    throw invalidConfig(envNames.sendDeadline, 'must be at most 24h');
  }
  return milliseconds;
}

/**
 * Absolute URL, `https:` (or `http:` on loopback only), no userinfo, no query or
 * fragment; a trailing `/` is normalised away (04.2.1-T2 §Config). The raw value is
 * never echoed — it may carry credentials (SEC3).
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
