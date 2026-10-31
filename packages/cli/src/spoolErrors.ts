// Every way a spool call can fail, with the hint a caller needs: a cap that was not
// crossed, a chunk line that cannot be trusted, a missing producer spool, an id that cannot
// be a path segment, or a path that is not a real owned file of ours.
import { CliError } from './errors.js';

/**
 * `spool_cap_exceeded`: the run's spool cannot take another byte without crossing
 * `EVIDGATE_SPOOL_MAX_BYTES`. Nothing was written by the call that throws it.
 */
export class SpoolCapExceededError extends CliError {
  readonly runId: string;
  readonly producerId: string;
  readonly capBytes: number;
  readonly usedBytes: number;
  readonly unsentFirstSequence: number;
  readonly unsentLastSequence: number;

  constructor(o: {
    readonly runId: string;
    readonly producerId: string;
    readonly capBytes: number;
    readonly usedBytes: number;
    readonly requiredBytes: number;
    readonly unsentFirstSequence: number;
    readonly unsentLastSequence: number;
  }) {
    super(
      `spool cap exceeded for run ${o.runId} producer ${o.producerId}: ` +
        `${o.usedBytes} bytes in use + ${o.requiredBytes} bytes to append > ${o.capBytes} bytes; ` +
        `unsent sequences ${o.unsentFirstSequence}..${o.unsentLastSequence}`,
      {
        code: 'spool_cap_exceeded',
        hint:
          `raise EVIDGATE_SPOOL_MAX_BYTES (currently ${o.capBytes}) or send the spooled ` +
          'events and finalize the producer before queueing more',
      },
    );
    this.name = 'SpoolCapExceededError';
    this.runId = o.runId;
    this.producerId = o.producerId;
    this.capBytes = o.capBytes;
    this.usedBytes = o.usedBytes;
    this.unsentFirstSequence = o.unsentFirstSequence;
    this.unsentLastSequence = o.unsentLastSequence;
  }
}

/** `spool_corrupt` (exit 3): a spool line that is neither a torn tail nor valid. */
export class SpoolCorruptError extends CliError {
  /** The chunk file name, e.g. `000002.jsonl`. */
  readonly chunk: string;
  /** 1-based line number inside that chunk. */
  readonly lineNumber: number;

  constructor(o: { readonly chunk: string; readonly lineNumber: number; readonly reason: string }) {
    super(`spool file ${o.chunk} line ${o.lineNumber} is corrupt: ${o.reason}`, {
      code: 'spool_corrupt',
      hint: 'the producer spool for this run is damaged; re-run the reporter for that producer',
    });
    this.name = 'SpoolCorruptError';
    this.chunk = o.chunk;
    this.lineNumber = o.lineNumber;
  }
}

/** `spool_missing` (exit 3): there is no spool for this run/producer. */
export class SpoolMissingError extends CliError {
  readonly runId: string;
  readonly producerId: string;

  constructor(o: {
    readonly runId: string;
    readonly producerId: string;
    readonly directory: string;
  }) {
    super(`no spool for run ${o.runId} producer ${o.producerId} (${o.directory})`, {
      code: 'spool_missing',
      hint: "run 'evidgate events send' (or the reporter) before finalizing",
    });
    this.name = 'SpoolMissingError';
    this.runId = o.runId;
    this.producerId = o.producerId;
  }
}

/**
 * `run_id`/`producer_id` are joined into the spool path verbatim, so each must be one
 * safe segment. The rules are the merged writer's, verbatim (`adapter-playwright` spool):
 * non-empty, not `.`/`..`, no separator, no control character, `[A-Za-z0-9._-]`, ≤ 128.
 */
export class InvalidSpoolIdError extends CliError {
  readonly field: 'run_id' | 'producer_id';
  readonly value: string;

  constructor(field: 'run_id' | 'producer_id', value: string, reason: string) {
    super(`${field} ${JSON.stringify(value)} is not a safe spool path segment: ${reason}`, {
      code: 'invalid_spool_id',
      hint:
        'use only letters, digits, ".", "_" and "-", at most 128 characters, ' +
        'and never "." or ".."',
    });
    this.name = 'InvalidSpoolIdError';
    this.field = field;
    this.value = value;
  }
}

export function invalidAck(reason: string): CliError {
  return new CliError(`cannot advance the spool cursor: ${reason}`, {
    code: 'invalid_ack',
    hint: 'ack only a contiguous prefix that is already durable in the spool',
  });
}

/** A spool path that is not a real, owned directory/file (symlink, foreign owner, file). */
export function unsafePath(message: string): CliError {
  return new CliError(message, {
    code: 'spool_unsafe_path',
    hint: 'the spool directory must be owned by the current user and contain no symlinks',
  });
}
