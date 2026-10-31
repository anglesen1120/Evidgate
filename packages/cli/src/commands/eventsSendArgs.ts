// The flag shapes and validators of `events send`. Both ids become spool path segments
// verbatim, so the wire shape is checked here and the spool-id rules on top of it — before
// `getToken()`, so a bad flag makes no request and no token exchange at all.
import { usageError } from './eventsSendErrors.js';

import type { ParsedValues } from './index.js';

/** `--from -` reads the envelopes from stdin. */
export const stdinName = '-';
/**
 * openapi `RunId`: the path parameter `r` of `POST /v1/runs/{r}/events` and the `run_id`
 * the reporter writes `<spool_dir>/<run_id>/…` with. Validated here exactly like
 * `runs finalize` does, so the two spool commands agree on the same flag.
 */
export const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
/** The producer id shape every producer-writing command accepts (same as `runs finalize`). */
export const producerPattern = /^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$/;

/**
 * The single value of one option, or `undefined` when it was absent. Every option is
 * declared `multiple: true`, so `parseArgs` reports a repetition as an array instead of
 * silently keeping the last value; two or more entries (or a non-string entry) are a
 * `usage` error that names the flag and never its value.
 */
export function flagValue(values: ParsedValues, flag: string): string | undefined {
  const value = values[flag];
  if (value === undefined) {
    return undefined;
  }
  if (typeof value === 'string') {
    return value;
  }
  if (!Array.isArray(value)) {
    throw usageError(`--${flag} must be a single value`);
  }
  if (value.length !== 1) {
    throw usageError(`--${flag} may be given only once`);
  }
  const [only] = value;
  if (typeof only !== 'string') {
    throw usageError(`--${flag} must be a single value`);
  }
  return only;
}

/** A required string option; absent is a `usage` error, a repetition is refused. */
export function requiredFlag(values: ParsedValues, flag: string): string {
  const value = flagValue(values, flag);
  if (value === undefined) {
    throw usageError(`missing required flag --${flag}`);
  }
  if (value.length === 0) {
    throw usageError(`--${flag} must be a single, non-empty value`);
  }
  return value;
}
