// The input rules the writer applies before it touches the filesystem: the path-segment check on
// the ids, the `EVIDGATE_SPOOL_MAX_BYTES` parse and range, the shard slot and the resume rule.
// Each mirrors a rule the CLI applies, so both sides refuse the same inputs.
import {
  DEFAULT_SPOOL_MAX_BYTES,
  MAX_SPOOL_MAX_BYTES,
  SPOOL_MAX_BYTES_ENV,
} from './spoolConfig.js';
import {
  EvidgateInvalidShardError,
  EvidgateInvalidSpoolConfigError,
  EvidgateInvalidSpoolIdError,
} from './spoolErrors.js';
import type { SpoolShard } from './spoolTypes.js';

/** The only characters allowed in a `run_id`/`producer_id` path segment. */
const SPOOL_ID_PATTERN = /^[A-Za-z0-9._-]+$/;

/** Envelope `producer_id` `maxLength` (contracts/schemas/event-envelope.v1.json). */
export const SPOOL_ID_MAX_LENGTH = 128;

/** A bare decimal integer, the only accepted `EVIDGATE_SPOOL_MAX_BYTES` form (cli.md §3). */
const DECIMAL_PATTERN = /^[0-9]{1,20}$/;

/** The manifest slot range `runs finalize --shard` accepts (contracts/cli.md §11.7). */
const MAX_SHARD_INDEX = 256;

const CONTROL_CHAR_PATTERN = /[\u0000-\u001f\u007f]/;

/**
 * Validates a spool path segment. `run_id` and `producer_id` are joined into the spool
 * path, so each must be a single, non-empty, non-dot segment with no path separator, NUL
 * or control character, and at most {@link SPOOL_ID_MAX_LENGTH} characters.
 */
export function assertValidSpoolId(field: 'run_id' | 'producer_id', value: string): void {
  if (value.length === 0) throw new EvidgateInvalidSpoolIdError(field, value, 'must not be empty');
  if (value === '.' || value === '..') {
    throw new EvidgateInvalidSpoolIdError(field, value, 'must not be "." or ".."');
  }
  if (value.includes('/') || value.includes('\\')) {
    throw new EvidgateInvalidSpoolIdError(field, value, 'must not contain a path separator');
  }
  if (CONTROL_CHAR_PATTERN.test(value)) {
    throw new EvidgateInvalidSpoolIdError(field, value, 'must not contain a control character');
  }
  if (!SPOOL_ID_PATTERN.test(value)) {
    throw new EvidgateInvalidSpoolIdError(
      field,
      value,
      'must contain only letters, digits, ".", "_" and "-"',
    );
  }
  if (value.length > SPOOL_ID_MAX_LENGTH) {
    throw new EvidgateInvalidSpoolIdError(
      field,
      value,
      `must be at most ${SPOOL_ID_MAX_LENGTH} characters`,
    );
  }
}

/**
 * `EVIDGATE_SPOOL_MAX_BYTES` (contracts/cli.md §3/§11.4): the run-directory cap, a positive
 * decimal byte count, defaulting to 256 MiB. An empty value counts as unset; zero, a
 * negative or fractional value, a unit suffix, junk and anything above
 * {@link MAX_SPOOL_MAX_BYTES} is `invalid_config` naming the variable and never echoing the
 * value. This mirrors `packages/cli`'s resolver so both sides refuse the same inputs (the
 * reporter package must not depend on the CLI).
 */
export function resolveSpoolMaxBytes(
  env: Readonly<Record<string, string | undefined>> = process.env,
): number {
  const raw = env[SPOOL_MAX_BYTES_ENV];
  const value = raw === undefined ? undefined : raw.trim();
  if (value === undefined || value.length === 0) return DEFAULT_SPOOL_MAX_BYTES;
  const parsed = DECIMAL_PATTERN.test(value) ? Number(value) : Number.NaN;
  assertSpoolMaxBytes(parsed);
  return parsed;
}

/** The shape and range rules {@link resolveSpoolMaxBytes} applies, for an injected cap. */
export function assertSpoolMaxBytes(value: number): void {
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new EvidgateInvalidSpoolConfigError(
      SPOOL_MAX_BYTES_ENV,
      'must be a positive integer number of bytes',
    );
  }
  if (value > MAX_SPOOL_MAX_BYTES) {
    throw new EvidgateInvalidSpoolConfigError(
      SPOOL_MAX_BYTES_ENV,
      `must be at most ${MAX_SPOOL_MAX_BYTES} bytes (1 TiB)`,
    );
  }
}

/**
 * Validates the `shard` option: a 1-based manifest slot index (1..256, the range
 * `runs finalize --shard` accepts) and, when given, a total that contains it.
 */
export function assertValidShardOption(shard: SpoolShard): void {
  if (!Number.isSafeInteger(shard.index) || shard.index < 1 || shard.index > MAX_SHARD_INDEX) {
    throw new EvidgateInvalidShardError(
      `index must be an integer between 1 and ${MAX_SHARD_INDEX}`,
    );
  }
  if (shard.total === undefined) return;
  if (
    !Number.isSafeInteger(shard.total) ||
    shard.total < 1 ||
    shard.total > MAX_SHARD_INDEX ||
    shard.total < shard.index
  ) {
    throw new EvidgateInvalidShardError(
      'total must be an integer between the shard index and ' + String(MAX_SHARD_INDEX),
    );
  }
}

/**
 * The resume rule: `max(record, lastChunkSequence + 1)`. The record is a FLOOR, never a ceiling —
 * a stored value ahead of the chunks is honoured, because reuse is the only sequence-related
 * refusal the server has (`422 sequence_reused`, contracts/events/ordering.md) while a hole is an
 * accepted reconciliation fact ("continues at checkpoint + 1"). Clamping to the chunks would
 * re-emit a sequence the server already accepted the moment a chunk file is lost or truncated. An
 * unusable value (not a safe integer `>= 1`) is dropped while reading the record, so a corrupt
 * value can never move the cursor at all.
 */
export function reconcileSequenceNext(durableNext: number, storedNext: number | undefined): number {
  if (storedNext === undefined) return durableNext;
  return Math.max(durableNext, storedNext);
}
