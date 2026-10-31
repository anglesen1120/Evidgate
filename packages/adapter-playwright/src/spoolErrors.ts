// The refusal types of the spool writer. Each carries a fixed `code` so the reporter's failure
// mapping never has to read a message, and each message names a path segment, a number or an
// errno only — never file content.
import { SPOOL_MAX_BYTES_ENV } from './spoolConfig.js';

/**
 * Thrown at construction when `run_id` or `producer_id` is not a safe single path segment. Both
 * are joined into the spool path verbatim, so an id containing `..`, a path separator, a NUL or a
 * control character could escape `spool_dir`.
 */
export class EvidgateInvalidSpoolIdError extends Error {
  readonly field: 'run_id' | 'producer_id';
  readonly value: string;

  constructor(field: 'run_id' | 'producer_id', value: string, reason: string) {
    super(`@evidgate/playwright-reporter: invalid ${field} ${JSON.stringify(value)}: ${reason}`);
    this.name = 'EvidgateInvalidSpoolIdError';
    this.field = field;
    this.value = value;
  }
}

/**
 * `invalid_config`: `EVIDGATE_SPOOL_MAX_BYTES` is not a positive decimal byte count (or the
 * injected cap is not one). The message names the variable and never echoes the value
 * (contracts/cli.md §3), so a malformed value cannot forge a log line.
 */
export class EvidgateInvalidSpoolConfigError extends Error {
  readonly code = 'invalid_config';
  readonly variable: string;

  constructor(variable: string, reason: string) {
    super(`@evidgate/playwright-reporter: invalid configuration: ${variable}: ${reason}`);
    this.name = 'EvidgateInvalidSpoolConfigError';
    this.variable = variable;
  }
}

/**
 * `spool_cap_exceeded`: the run's spool cannot take the pending batch without crossing
 * `EVIDGATE_SPOOL_MAX_BYTES`. Nothing of that batch was written, so the caller sees a refusal
 * rather than a silent drop or a partial line; the reporter turns it into the same spool-write
 * failure it reports for any other write error.
 */
export class EvidgateSpoolCapExceededError extends Error {
  readonly code = 'spool_cap_exceeded';
  readonly runId: string;
  readonly producerId: string;
  readonly capBytes: number;
  readonly usedBytes: number;
  readonly requiredBytes: number;

  constructor(usage: {
    runId: string;
    producerId: string;
    capBytes: number;
    usedBytes: number;
    requiredBytes: number;
  }) {
    super(
      `@evidgate/playwright-reporter: spool cap exceeded for run ${usage.runId}: ` +
        `${usage.usedBytes} bytes in use + ${usage.requiredBytes} pending > ` +
        `${usage.capBytes} bytes (${SPOOL_MAX_BYTES_ENV}); producer ${usage.producerId} wrote nothing`,
    );
    this.name = 'EvidgateSpoolCapExceededError';
    this.runId = usage.runId;
    this.producerId = usage.producerId;
    this.capBytes = usage.capBytes;
    this.usedBytes = usage.usedBytes;
    this.requiredBytes = usage.requiredBytes;
  }
}

/**
 * `spool_shard_mismatch`: the producer directory already carries a `shards.json` that names
 * a different producer or shard slot than the caller passed. That is a client bug (two runs
 * sharing a spool dir, or a recomputed shard index), so the writer refuses at construction
 * and never overwrites the record.
 */
export class EvidgateShardRecordMismatchError extends Error {
  readonly code = 'spool_shard_mismatch';
  readonly recordPath: string;
  readonly stored: { producer_id: string; shard_index?: number };
  readonly requested: { producer_id: string; shard_index?: number };

  constructor(mismatch: {
    recordPath: string;
    stored: { producer_id: string; shard_index?: number };
    requested: { producer_id: string; shard_index?: number };
  }) {
    super(
      `@evidgate/playwright-reporter: ${mismatch.recordPath} records producer ` +
        `${JSON.stringify(mismatch.stored.producer_id)} shard ` +
        `${String(mismatch.stored.shard_index ?? '-')}, but this writer was opened for producer ` +
        `${JSON.stringify(mismatch.requested.producer_id)} shard ` +
        `${String(mismatch.requested.shard_index ?? '-')}; refusing to overwrite the record`,
    );
    this.name = 'EvidgateShardRecordMismatchError';
    this.recordPath = mismatch.recordPath;
    this.stored = mismatch.stored;
    this.requested = mismatch.requested;
  }
}

/** `invalid_shard`: the `shard` option is not a 1-based manifest slot index. */
export class EvidgateInvalidShardError extends Error {
  readonly code = 'invalid_shard';

  constructor(reason: string) {
    super(`@evidgate/playwright-reporter: invalid shard option: ${reason}`);
    this.name = 'EvidgateInvalidShardError';
  }
}

/**
 * `spool_writer_locked`: a live process already writes this `(run_id, producer_id)`.
 * The error names the holder's pid when the lock file named one.
 */
export class EvidgateSpoolWriterLockedError extends Error {
  readonly code = 'spool_writer_locked';
  /** The lock file that is held. */
  readonly lockPath: string;
  /** The live holder's pid, when the lock file named one. */
  readonly holderPid?: number;

  constructor(lockPath: string, holderPid?: number) {
    super(
      holderPid === undefined
        ? `@evidgate/playwright-reporter: the producer spool is locked by another process (${lockPath})`
        : `@evidgate/playwright-reporter: the producer spool is locked by live process ${holderPid} (${lockPath})`,
    );
    this.name = 'EvidgateSpoolWriterLockedError';
    this.lockPath = lockPath;
    if (holderPid !== undefined) this.holderPid = holderPid;
  }
}

/**
 * `spool_unsafe_path`: the lock path cannot be inspected (I/O, permissions, descriptors) or
 * the filesystem cannot hard-link the record into place. Nothing was taken over.
 */
export class EvidgateSpoolUnsafePathError extends Error {
  readonly code = 'spool_unsafe_path';
  readonly path: string;
  readonly errno: string | undefined;

  constructor(path: string, message: string, errno?: string) {
    super(`@evidgate/playwright-reporter: ${message} (${path})`);
    this.name = 'EvidgateSpoolUnsafePathError';
    this.path = path;
    if (errno !== undefined) this.errno = errno;
  }
}
