// The spool half of the configuration: the root, the run-directory byte cap and the send
// deadline. It is resolved on demand — never by `resolveConfig` — so a malformed spool value
// can only break the commands that use the spool, and the value is never echoed in an error.
import { resolve } from 'node:path';

import { parseDuration } from './duration.js';
import { defaultSpoolMaxBytes } from './spool.js';
import { envNames, invalidConfig, trimmedEnv } from './configEnv.js';
import { CliError } from './errors.js';

import type { SpoolSettings } from './configTypes.js';

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

/** `EVIDGATE_SEND_DEADLINE` default: 30 minutes for the whole send. */
export const defaultSendDeadlineMs = 1_800_000;

/** `EVIDGATE_SEND_DEADLINE` ceiling: 24 hours. */
export const maxSendDeadlineMs = 86_400_000;

/** A bare decimal integer, used for the byte count. */
const decimalPattern = /^[0-9]{1,20}$/;

/**
 * The spool surface `events send` and `runs finalize` need, resolved in the order root,
 * cap, deadline. Errors name the variable and never echo the value, and only the commands
 * that use the spool report them, so a malformed value cannot break any other command.
 */
export function resolveSpoolSettings(
  env: Readonly<Record<string, string | undefined>>,
): SpoolSettings {
  return {
    spoolDir: resolveOptionalSpoolDir(env),
    spoolMaxBytes: resolveSpoolMaxBytes(env),
    sendDeadlineMs: resolveSendDeadlineMs(env),
  };
}

/**
 * The `missing_config` entry for "no spool root is set". Exported so a command can refuse
 * the configuration in its `validateConfig` pre-flight, before the `--verbose` dump and
 * any token work.
 */
export function spoolDirMissingError(): CliError {
  return new CliError(`missing configuration: ${envNames.spoolDir}`, {
    code: 'missing_config',
    hint: 'set EVIDGATE_SPOOL_DIR to the directory your reporter writes the spool to',
  });
}

/**
 * `EVIDGATE_SPOOL_DIR`: required by the spool commands, with no default. The value is
 * trimmed and absolute-resolved, the same rule the reporter applies when it joins
 * `<spool_dir>/<run_id>/<producer_id>`. An unset or blank value is `missing_config` naming
 * the variable, never a path; a control character is `invalid_config`, so a value can
 * neither forge a log line nor reach the filesystem.
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
 * `EVIDGATE_SPOOL_MAX_BYTES`: the run-directory cap, a positive decimal byte count
 * defaulting to 256 MiB. Zero, a fraction, a unit suffix, junk or a value above the 1 TiB
 * ceiling is `invalid_config` naming the variable, and the raw value is never echoed
 * because it could forge a log line.
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
 * `EVIDGATE_SEND_DEADLINE`: one deadline for the whole send, as bare seconds or `<n>s`,
 * `<n>m`, `<n>h`. Default 30 minutes, ceiling 24 hours; anything else is `invalid_config`.
 */
export function resolveSendDeadlineMs(env: Readonly<Record<string, string | undefined>>): number {
  const value = trimmedEnv(env, envNames.sendDeadline);
  if (value === undefined) {
    return defaultSendDeadlineMs;
  }
  const parsed = parseDuration(value, maxSendDeadlineMs);
  if (parsed.ok) {
    return parsed.milliseconds;
  }
  throw invalidConfig(
    envNames.sendDeadline,
    parsed.reason === 'grammar'
      ? 'must be an integer number of seconds, or <n>s, <n>m or <n>h'
      : parsed.reason === 'not_positive'
        ? 'must be greater than zero'
        : 'must be at most 24h',
  );
}
