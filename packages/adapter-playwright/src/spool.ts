// The spool writer. The on-disk layout is a contract with the CLI reader:
//
//   <spool_dir>/<run_id>/<producer_id>/NNNNNN.jsonl     (6-digit zero-padded chunks)
//   <spool_dir>/<run_id>/<producer_id>/shards.json      { producer_id, shard_index,
//                                                         shard_total?, sequence_next,
//                                                         last_restart_at? }
//   <spool_dir>/<run_id>/<producer_id>/writer.lock      { pid, started_at } (writerLock.ts)
//   <spool_dir>/<run_id>/<producer_id>/checkpoint.json  legacy, read-only fallback
//
// `cursor.json` and `spool.lock` belong to the CLI (contracts/cli.md §11), never to this writer.
//
// `shards.json` is the single reporter-owned sequence record, written atomically at rotation,
// close and restart detection only — a per-callback fsync costs measurable time. The chunk scan
// stays authoritative, so the record may be sparse and may lag: `sequence_next` is the sequence
// after the last line that reached a chunk, never one only buffered here (a rotation mid-batch
// would leave a hole after a crash that the reader refuses as `spool_corrupt`).
//
// A value already on disk at open is a floor and is never raised; a failed record write is retried
// at the next rotation or close instead of failing the flush, because the record is advisory.
//
// Two advisory conditions are queued once per writer for `takeNotices()`: a failed record write at
// rotation (the flush succeeded, so nothing else would report it), and a record floor read at open
// that is ahead of `last chunk sequence + 1` (a hand-edited or legacy file — the resume is
// unchanged). The queue never throws and never carries a path.
//
// Hardening: `O_NOFOLLOW` and, outside Windows, `O_NONBLOCK` on every open, and a write target is
// `lstat`-refused unless regular, so a planted symlink, FIFO or device can neither be followed nor
// block the writer.
//
// Durability: per-callback appends go to a small pending batch, so O(1) per callback and no
// whole-suite buffering; a flush writes it and fsyncs, so a crash loses at most
// `flushThreshold - 1` envelopes and those sequences are minted again. On restart the record is
// reconciled against the highest sequence on disk (the last complete line of the highest chunk,
// backwards, then lower chunks), so a sequence is never reused, and a torn tail is truncated to the
// last newline and fsynced before the first append.
//
// A restart keeps the SAME `producer_id`, records `last_restart_at` and resumes where the durable
// chunks end. `writer.lock` is held for the writer's whole life, so two live processes sharing
// `(run_id, producer_id)` cannot interleave lines; the second fails with `spool_writer_locked` and
// a dead holder's lock is taken over (writerLock.ts, ported from the CLI).
//
// Cap and rotation: `EVIDGATE_SPOOL_MAX_BYTES` caps one run directory (scanned at open, incremented
// by our appends, re-scanned at rotation) and a batch that would cross it is refused with
// `spool_cap_exceeded` BEFORE a byte is written. A chunk rotates at `chunkMaxBytes` (1 MiB) or
// `chunkMaxLines` (1000), always to `highest + 1`; only a successful finalize purges the directory.
import {
  chmodSync,
  closeSync,
  constants as fsConstants,
  fchmodSync,
  fsyncSync,
  fstatSync,
  ftruncateSync,
  lstatSync,
  mkdirSync,
  openSync,
  readFileSync,
  readdirSync,
  renameSync,
  type Stats,
  unlinkSync,
  writeSync,
} from 'node:fs';
import { join, resolve, sep } from 'node:path';

import type { EventEnvelope, UnsignedEnvelope } from './envelope.js';
import {
  acquireWriterLock,
  releaseWriterLock,
  type HeldWriterLock,
  type WriterLockOptions,
} from './writerLock.js';

export {
  EvidgateSpoolUnsafePathError,
  EvidgateSpoolWriterLockedError,
  isProcessAlive,
  readWriterLock,
  WRITER_LOCK_FILE,
  WRITER_LOCK_FILE_MODE,
  type WriterLockFile,
} from './writerLock.js';

export const CHUNK_FILE_DIGITS = 6;
export const DEFAULT_FLUSH_THRESHOLD = 64;
export const DEFAULT_CHUNK_MAX_BYTES = 1024 * 1024;
export const DEFAULT_CHUNK_MAX_LINES = 1000;
export const SPOOL_DIR_MODE = 0o700;
export const SPOOL_FILE_MODE = 0o600;
/** The single reporter-owned sequence record. */
export const SHARDS_FILE = 'shards.json';
export const SHARDS_TMP_FILE = 'shards.json.tmp';
/**
 * The legacy sequence store: read as a fallback when `shards.json` is absent and never written
 * again; a leftover staging name is cleaned up at open.
 */
export const CHECKPOINT_FILE = 'checkpoint.json';
export const CHECKPOINT_TMP_FILE = 'checkpoint.json.tmp';
/** `EVIDGATE_SPOOL_MAX_BYTES` default: 256 MiB per run directory (contracts/cli.md §11.4). */
export const DEFAULT_SPOOL_MAX_BYTES = 268_435_456;
/** The largest accepted `EVIDGATE_SPOOL_MAX_BYTES`: 2^40 bytes, i.e. 1 TiB (cli.md §3). */
export const MAX_SPOOL_MAX_BYTES = 1_099_511_627_776;
export const SPOOL_MAX_BYTES_ENV = 'EVIDGATE_SPOOL_MAX_BYTES';

const CHUNK_FILE_PATTERN = /^(\d{6})\.jsonl$/;

/** A bare decimal integer, the only accepted `EVIDGATE_SPOOL_MAX_BYTES` form (cli.md §3). */
const DECIMAL_PATTERN = /^[0-9]{1,20}$/;

/** The manifest slot range `runs finalize --shard` accepts (contracts/cli.md §11.7). */
const MAX_SHARD_INDEX = 256;

/**
 * `O_NOFOLLOW` is undefined on Windows; degrade to a plain open there, where the POSIX
 * symlink/ownership hardening does not apply.
 */
const O_NOFOLLOW: number = process.platform === 'win32' ? 0 : fsConstants.O_NOFOLLOW;

/**
 * A planted FIFO must never block a spool open. `O_NONBLOCK` is a no-op on a regular file; on a
 * FIFO it makes a read open return immediately (the descriptor is then refused by the
 * regular-file check) and a write open fail with `ENXIO` instead of waiting for a reader. The
 * writer paths refuse a non-regular target by `lstat` before the open, so they never get that far.
 */
const O_NONBLOCK: number = process.platform === 'win32' ? 0 : fsConstants.O_NONBLOCK;

/** The only characters allowed in a `run_id`/`producer_id` path segment. */
const SPOOL_ID_PATTERN = /^[A-Za-z0-9._-]+$/;

/** Envelope `producer_id` `maxLength` (contracts/schemas/event-envelope.v1.json). */
export const SPOOL_ID_MAX_LENGTH = 128;

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

const CONTROL_CHAR_PATTERN = /[\u0000-\u001f\u007f]/;

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
 * Defence in depth: even after id validation, the resolved writer directory must stay inside the
 * resolved spool root.
 */
function assertWithinSpoolDir(spoolDir: string, directory: string): void {
  const root = resolve(spoolDir);
  const target = resolve(directory);
  if (target !== root && !target.startsWith(root + sep)) {
    throw new Error(
      `@evidgate/playwright-reporter: the spool directory ${target} escapes spool_dir ${root}`,
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
function reconcileSequenceNext(durableNext: number, storedNext: number | undefined): number {
  if (storedNext === undefined) return durableNext;
  return Math.max(durableNext, storedNext);
}

/** Total bytes of every regular file under `directory` (the `EVIDGATE_SPOOL_MAX_BYTES` base). */
function directoryBytes(directory: string): number {
  let total = 0;
  let entries;
  try {
    entries = readdirSync(directory, { withFileTypes: true });
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return 0;
    throw error;
  }
  for (const entry of entries) {
    const path = join(directory, entry.name);
    // Symlinks are never followed: a link out of the run directory must not be counted (or
    // read) as spool bytes.
    if (entry.isSymbolicLink()) continue;
    if (entry.isDirectory()) {
      total += directoryBytes(path);
      continue;
    }
    if (!entry.isFile()) continue;
    let fd = -1;
    try {
      fd = openSync(path, fsConstants.O_RDONLY | O_NOFOLLOW | O_NONBLOCK);
      total += fstatSync(fd).size;
    } catch {
      // A file that vanished or cannot be inspected contributes no bytes; the cap is a
      // guard rail, not an inventory.
    } finally {
      if (fd >= 0) closeSync(fd);
    }
  }
  return total;
}

/** The shard slot a producer stands for; `index` is 1-based. */
export interface SpoolShard {
  index: number;
  total?: number;
}

/**
 * `shards.json` — the reporter-owned sequence record. It is written at rotation, close and
 * restart detection only; the chunk scan stays authoritative for the resume sequence, so the
 * record may lag behind the chunks after a crash.
 */
export interface ShardsRecord {
  producer_id: string;
  shard_index?: number;
  shard_total?: number;
  sequence_next: number;
  last_restart_at?: string;
}

/** The legacy `checkpoint.json` shape, read-only. */
export interface SpoolCheckpoint {
  producer_id: string;
  next_sequence: number;
}

/**
 * What a readable `shards.json` yielded: the identity is required, the sequence may be
 * unusable.
 */
interface StoredShards {
  producer_id: string;
  shard_index?: number;
  shard_total?: number;
  sequence_next?: number;
  last_restart_at?: string;
}

export interface SpoolWriterOptions {
  spoolDir: string;
  runId: string;
  producerId: string;
  /** The manifest shard slot this producer stands for. */
  shard?: SpoolShard;
  /** Envelopes buffered before an automatic flush. Default {@link DEFAULT_FLUSH_THRESHOLD}. */
  flushThreshold?: number;
  chunkMaxBytes?: number;
  chunkMaxLines?: number;
  /**
   * The run-directory byte cap. Defaults to `env.EVIDGATE_SPOOL_MAX_BYTES` (256 MiB),
   * resolved once in the constructor with the CLI's parsing rules (contracts/cli.md
   * §3/§11.4).
   */
  maxRunBytes?: number;
  /** Environment for the cap resolution; defaults to `process.env` (test seam). */
  env?: Readonly<Record<string, string | undefined>>;
  /** Clock for `last_restart_at`; defaults to `new Date()` (injected by tests). */
  now?: () => Date;
  /** Single-writer lock seams (pid, liveness, probe pause); the lock keeps its own clock. */
  lock?: Omit<WriterLockOptions, 'now'>;
}

/** The advisory conditions {@link SpoolWriter.takeNotices} can report. */
export type SpoolNoticeCode = 'shards_write_failed' | 'shards_floor_ahead';

/**
 * One non-fatal spool condition, queued once per writer. `message` is safe to log or attach
 * to an event: it names base names, numbers and errno codes only — never a full path, an
 * error message body or any file content.
 */
export interface SpoolNotice {
  code: SpoolNoticeCode;
  message: string;
}

/**
 * Writers this process still holds a lock for. `close()` releases normally; an unclosed
 * writer (a Playwright process that exits without `onEnd`, or a test) is released by ONE
 * module-level `exit` hook — best effort, since only synchronous work is allowed there. A
 * single hook instead of one listener per writer also keeps `MaxListenersExceededWarning`
 * away when a process opens many producers in a row.
 */
const liveLocks = new Set<HeldWriterLock>();
let exitHook: (() => void) | undefined;

function trackLock(held: HeldWriterLock): void {
  liveLocks.add(held);
  if (exitHook !== undefined) return;
  exitHook = () => {
    for (const lock of liveLocks) {
      try {
        releaseWriterLock(lock);
      } catch {
        // Nothing can be reported from an exit handler; the lock goes stale with the pid.
      }
    }
    liveLocks.clear();
  };
  process.once('exit', exitHook);
}

function untrackLock(held: HeldWriterLock): void {
  liveLocks.delete(held);
  if (liveLocks.size > 0 || exitHook === undefined) return;
  process.off('exit', exitHook);
  exitHook = undefined;
}

/**
 * Append-only spool writer for one `(run_id, producer_id)` pair. It owns the
 * per-producer sequence counter: `append` assigns the next contiguous sequence. It holds
 * the producer directory's `writer.lock` from construction to `close()`.
 */
export class SpoolWriter {
  readonly directory: string;
  readonly runId: string;
  private readonly spoolDir: string;
  private readonly producerId: string;
  private readonly flushThreshold: number;
  private readonly chunkMaxBytes: number;
  private readonly chunkMaxLines: number;
  /** The resolved run-directory cap (`EVIDGATE_SPOOL_MAX_BYTES` unless overridden). */
  readonly maxRunBytes: number;
  private readonly now: () => Date;
  private readonly shardIndex: number | undefined;
  private readonly shardTotal: number | undefined;
  /** The sequence after the last line that reached a chunk (the `shards.json` value). */
  private durableNextSequence: number;
  /**
   * The floor this producer directory already carried when the writer opened:
   * `max(durableNextSequence, stored sequence_next | next_sequence at open)`. The record is
   * never written below it, so a floor that arrived on disk is not thrown away by the first
   * rewrite, while nothing this writer only buffered can push the record ahead of the chunks.
   */
  private recordFloor: number;
  private nextSequence: number;
  /** The last `shards.json` write failure: the record is advisory, so the failure is kept. */
  private shardsFailure: Error | undefined;
  /** Queued {@link SpoolNotice}s not yet taken; each code is queued at most once per writer. */
  private notices: SpoolNotice[] = [];
  private readonly noticedCodes = new Set<SpoolNoticeCode>();
  private chunkIndex: number;
  private chunkBytes = 0;
  private chunkLines = 0;
  private fd = -1;
  private pending: EventEnvelope[] = [];
  private closed = false;
  private lock: HeldWriterLock | undefined;
  /**
   * The run directory's byte total: scanned at open, incremented per line, re-scanned at
   * rotation.
   */
  private runBytes = 0;
  /** Size of the `shards.json` counted in {@link runBytes}, so a rewrite adds only its delta. */
  private recordBytes = 0;
  private recordWrites = 0;
  private restartAt: string | undefined;

  constructor(options: SpoolWriterOptions) {
    // `run_id`/`producer_id` are joined into the spool path verbatim, so reject any
    // value that could escape `spool_dir` before the first filesystem write.
    assertValidSpoolId('run_id', options.runId);
    assertValidSpoolId('producer_id', options.producerId);
    this.directory = join(options.spoolDir, options.runId, options.producerId);
    assertWithinSpoolDir(options.spoolDir, this.directory);
    this.runId = options.runId;
    this.spoolDir = options.spoolDir;
    this.producerId = options.producerId;
    this.flushThreshold = options.flushThreshold ?? DEFAULT_FLUSH_THRESHOLD;
    this.chunkMaxBytes = options.chunkMaxBytes ?? DEFAULT_CHUNK_MAX_BYTES;
    this.chunkMaxLines = options.chunkMaxLines ?? DEFAULT_CHUNK_MAX_LINES;
    this.now = options.now ?? (() => new Date());
    // The cap is resolved once, here: a malformed value must refuse the writer instead of
    // resurfacing mid-run (contracts/cli.md §3/§11.4).
    this.maxRunBytes = options.maxRunBytes ?? resolveSpoolMaxBytes(options.env ?? process.env);
    assertSpoolMaxBytes(this.maxRunBytes);
    if (options.shard !== undefined) {
      assertValidShardOption(options.shard);
      this.shardIndex = options.shard.index;
      this.shardTotal = options.shard.total;
    }
    mkdirSync(this.directory, { recursive: true, mode: SPOOL_DIR_MODE });
    // The run and producer directories are ours to create or reuse; reject a planted
    // symlink or a foreign-owned directory and force 0700 on an owned one. `spool_dir`
    // itself is left as given (a symlinked spool root is allowed, and a pre-existing
    // spool root keeps its mode).
    this.hardenDirectory(join(options.spoolDir, options.runId));
    this.hardenDirectory(this.directory);
    // The record is read before the lock so a mismatch is a clean refusal with nothing to undo;
    // the chunk scan below stays authoritative for the sequence.
    const record = this.readShardsRecord();
    const legacy = record === null ? this.readCheckpoint() : null;
    // One live writer per producer directory. A second live process fails with
    // `spool_writer_locked` naming the holder; a dead one is taken over.
    this.lock = acquireWriterLock(this.directory, options.lock ?? {});
    trackLock(this.lock);
    try {
      this.runBytes = directoryBytes(join(options.spoolDir, options.runId));
      this.recordBytes = this.existingRecordBytes();
      this.chunkIndex = this.highestChunkIndex();
      // Read the highest chunk once and share the buffer with the torn-tail repair, the
      // sequence scan and the open, instead of reading it three times.
      const highestChunk =
        this.chunkIndex < 1 ? null : this.readChunkIfExists(this.chunkPath(this.chunkIndex));
      const repairedChunk = this.truncateTornTail(highestChunk);
      const lastSequence = this.lastSequenceInChunks(repairedChunk);
      this.durableNextSequence = lastSequence + 1;
      // `max(record, lastChunkSequence + 1)` — the record is a floor, so a record ahead of the
      // chunks can never be reused (see `reconcileSequenceNext`).
      this.nextSequence = reconcileSequenceNext(
        this.durableNextSequence,
        record?.sequence_next ?? legacy?.next_sequence,
      );
      // The reconciled value is the floor this directory already carried. It is the only thing
      // that may hold the record above the chunks, and it never grows: what this writer only
      // buffered must not reach the record (a crash would then skip sequences, which the CLI
      // reader refuses as a hole in `spool_corrupt`).
      this.recordFloor = this.nextSequence;
      // A record ahead of `last chunk sequence + 1` is a floor the resume above honours, and no
      // writer of ours mints such a file — so reading one means a hand-edited or legacy record.
      // Say so once; the floor itself still wins.
      if (record?.sequence_next !== undefined && record.sequence_next > this.durableNextSequence) {
        const floor = String(record.sequence_next);
        const highest = String(lastSequence);
        this.pushNotice(
          'shards_floor_ahead',
          `shards.json floor ${floor} is ahead of the highest chunk sequence ${highest}`,
        );
      }
      this.openChunk(repairedChunk);
      // Restart detection: any durable state means this producer ran before. The same
      // `producer_id` is kept (no suffix is ever minted); the restart is recorded so the
      // continuity is provable from the spool, and a stale record is corrected.
      if (highestChunk !== null || record !== null || legacy !== null) {
        this.restartAt = this.now().toISOString();
        this.writeShardsRecord();
      }
    } catch (error) {
      // A failed construction must not leave a lock naming a live pid with no writer.
      this.releaseLock();
      throw error;
    }
  }

  /** The sequence the next appended envelope will receive. */
  get nextSequenceNumber(): number {
    return this.nextSequence;
  }

  /** Number of envelopes buffered but not yet flushed (test seam for the O(1) buffer bound). */
  get pendingCount(): number {
    return this.pending.length;
  }

  /**
   * How many times `shards.json` has been written (cadence seam: rotation, close and restart
   * detection only — a per-callback flush must never bump it).
   */
  get shardsWriteCount(): number {
    return this.recordWrites;
  }

  /** The run directory's accounted bytes, including this writer's appends (cap seam). */
  get runBytesInUse(): number {
    return this.runBytes;
  }

  /** The `last_restart_at` recorded when this writer opened an existing producer directory. */
  get restartRecordedAt(): string | undefined {
    return this.restartAt;
  }

  /**
   * The last advisory `shards.json` write failure. A rotation that could not write the record
   * keeps going — the chunk scan is the resume authority — and the next rotation or `close`
   * retries; this seam says whether the last attempt failed, so a caller (or a test) can tell a
   * refused record from a written one. `close` still throws when its own retry fails, because
   * that is the last chance to record the state. The first such failure at rotation also surfaces
   * as a `shards_write_failed` notice from {@link takeNotices}, so a caller need not poll this
   * getter mid-run (a later successful write clears it, which would hide the failure from a
   * caller that reads it late).
   */
  get lastShardsWriteFailure(): Error | undefined {
    return this.shardsFailure;
  }

  /**
   * Returns the notices queued since the last call and clears them. Each code is queued AT MOST
   * ONCE per writer, and this method never throws — a notice is a best-effort report, so a caller
   * may drain it at any point (boot, rotation, end of run) without affecting the spool.
   */
  takeNotices(): readonly SpoolNotice[] {
    const notices = this.notices;
    this.notices = [];
    return notices;
  }

  /** Queues one notice per code; a repeat of an already-queued code is dropped. */
  private pushNotice(code: SpoolNoticeCode, message: string): void {
    if (this.noticedCodes.has(code)) return;
    this.noticedCodes.add(code);
    this.notices.push({ code, message });
  }

  /** Assigns the next sequence, buffers the envelope, and flushes when the batch is full. */
  append(event: UnsignedEnvelope): EventEnvelope {
    if (this.closed) throw new Error('spool writer is closed');
    const envelope: EventEnvelope = { ...event, sequence: this.nextSequence };
    this.nextSequence += 1;
    this.pending.push(envelope);
    if (this.pending.length >= this.flushThreshold) this.flush();
    return envelope;
  }

  /** Appends a callback's whole batch; the sequence order follows the array order. */
  appendMany(events: readonly UnsignedEnvelope[]): EventEnvelope[] {
    return events.map((event) => this.append(event));
  }

  /**
   * Writes the pending batch and fsyncs the chunk. The `EVIDGATE_SPOOL_MAX_BYTES` check runs
   * first, so a refused batch leaves the spool byte-identical — no partial line and no silent
   * drop — and `shards.json` is deliberately not touched here (the write cadence above).
   */
  flush(): void {
    if (this.pending.length === 0) return;
    const batch = this.pending;
    this.pending = [];
    const lines = batch.map((envelope) => Buffer.from(`${JSON.stringify(envelope)}\n`, 'utf8'));
    let written = 0;
    try {
      this.assertRunCap(lines.reduce((total, line) => total + line.byteLength, 0));
      for (const [index, line] of lines.entries()) {
        if (this.chunkLines >= this.chunkMaxLines || this.chunkBytes >= this.chunkMaxBytes) {
          this.rotate();
        }
        this.writeBuffer(line);
        this.durableNextSequence = (batch[index]?.sequence ?? this.durableNextSequence) + 1;
        written += 1;
      }
      this.fsyncChunk();
    } catch (error) {
      // An envelope that never reached the chunk stays queued so a later flush can retry it
      // instead of silently dropping evidence; the error still propagates. Envelopes already
      // written (and fsynced) are not re-queued, so a retry cannot duplicate a durable line.
      if (written < batch.length) {
        this.pending = batch.slice(written).concat(this.pending);
      }
      throw error;
    }
  }

  /**
   * Flushes, closes the chunk, writes the `shards.json` record and releases `writer.lock`.
   * Idempotent. A flush failure still releases the descriptor and the lock, then rethrows
   * (the record write is skipped: it would only record what the failed flush undid).
   */
  close(): void {
    if (this.closed) return;
    let failure: unknown;
    try {
      this.flush();
    } catch (error) {
      failure = error;
    }
    try {
      if (this.fd >= 0) {
        closeSync(this.fd);
        this.fd = -1;
      }
      if (failure === undefined) {
        try {
          this.writeShardsRecord();
        } catch (error) {
          failure = error;
        }
      }
    } finally {
      this.closed = true;
      this.releaseLock();
    }
    if (failure !== undefined) throw failure as Error;
  }

  private writeBuffer(buffer: Buffer): void {
    if (this.fd < 0) throw new Error('spool chunk is not open');
    const startOffset = this.chunkBytes;
    try {
      this.writeAll(this.fd, buffer);
    } catch (error) {
      // Roll the chunk back to the last complete line so the JSONL stays parseable and the
      // reader never sees a fragment; the original error still propagates.
      try {
        ftruncateSync(this.fd, startOffset);
      } catch {
        // Best effort: the write error is the one that matters.
      }
      throw error;
    }
    this.chunkBytes += buffer.byteLength;
    this.chunkLines += 1;
    // The cap cache follows what actually reached the chunk, so a rolled-back line is not
    // counted as spool bytes.
    this.runBytes += buffer.byteLength;
  }

  /** `spool_cap_exceeded` before the first byte of a batch, so an append is never partial. */
  private assertRunCap(requiredBytes: number): void {
    if (this.runBytes + requiredBytes <= this.maxRunBytes) return;
    throw new EvidgateSpoolCapExceededError({
      runId: this.runId,
      producerId: this.producerId,
      capBytes: this.maxRunBytes,
      usedBytes: this.runBytes,
      requiredBytes,
    });
  }

  /**
   * `writeSync` may write fewer bytes than asked (ENOSPC, EINTR, quota); loop until the whole
   * buffer is written so a short write cannot leave a torn line.
   */
  private writeAll(fd: number, buffer: Buffer): void {
    let written = 0;
    while (written < buffer.byteLength) {
      const n = writeSync(fd, buffer, written, buffer.byteLength - written);
      if (n <= 0) throw new Error('spool write made no progress');
      written += n;
    }
  }

  private fsyncChunk(): void {
    if (this.fd < 0) throw new Error('spool chunk is not open');
    fsyncSync(this.fd);
  }

  /**
   * Writes `shards.json` atomically (tmp + fsync + rename + directory fsync). Called at rotation,
   * close and restart detection ONLY: the chunk scan recovers the sequence, so this record never
   * has to be on the per-callback path. The value is the sequence after the last line that
   * reached a chunk, never one the writer only buffered — an ahead value would read as a gap
   * after a crash. Only a floor that was already on disk when this writer opened survives.
   */
  private writeShardsRecord(): void {
    const record: ShardsRecord = {
      producer_id: this.producerId,
      ...(this.shardIndex === undefined ? {} : { shard_index: this.shardIndex }),
      ...(this.shardTotal === undefined ? {} : { shard_total: this.shardTotal }),
      // `durableNextSequence`, not `nextSequence`. A rotation happens mid-batch, so
      // `nextSequence` already covers envelopes still buffered; writing that value leaves a
      // record ahead of the chunks after a crash, and the resumed writer skips sequences
      // straight into a hole the CLI reader rejects (`spool_corrupt`, "skips the expected").
      // `recordFloor` keeps a floor that arrived on disk without ever raising it.
      sequence_next: Math.max(this.durableNextSequence, this.recordFloor),
      ...(this.restartAt === undefined ? {} : { last_restart_at: this.restartAt }),
    };
    const body = Buffer.from(`${JSON.stringify(record)}\n`, 'utf8');
    const tmpPath = join(this.directory, SHARDS_TMP_FILE);
    const targetPath = join(this.directory, SHARDS_FILE);
    // Clear a leftover tmp (a crash between write and rename) and re-create it with
    // O_EXCL|O_NOFOLLOW, so a planted `shards.json.tmp -> victim` symlink is never truncated or
    // followed.
    try {
      unlinkSync(tmpPath);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
    }
    this.assertWritableTargetIsRegularFile(tmpPath);
    const fd = this.openVerified(
      tmpPath,
      fsConstants.O_WRONLY | fsConstants.O_CREAT | fsConstants.O_EXCL | O_NOFOLLOW,
      SPOOL_FILE_MODE,
    );
    try {
      this.writeAll(fd, body);
      fsyncSync(fd);
    } finally {
      closeSync(fd);
    }
    renameSync(tmpPath, targetPath);
    this.fsyncDirectory();
    this.recordWrites += 1;
    this.shardsFailure = undefined;
    // The cap cache stays honest about our own metadata: the rename replaced the previous
    // record, so only the size delta is new bytes.
    this.runBytes += Math.max(0, body.byteLength - this.recordBytes);
    this.recordBytes = body.byteLength;
  }

  private fsyncDirectory(): void {
    if (process.platform === 'win32') return;
    try {
      const fd = openSync(this.directory, 'r');
      try {
        fsyncSync(fd);
      } finally {
        closeSync(fd);
      }
    } catch {
      // Directory fsync is a durability nicety; a filesystem that refuses it (some
      // network mounts) must not fail the run.
    }
  }

  private rotate(): void {
    if (this.fd >= 0) {
      fsyncSync(this.fd);
      closeSync(this.fd);
      this.fd = -1;
    }
    const previousIndex = this.chunkIndex;
    const previousBytes = this.chunkBytes;
    const previousLines = this.chunkLines;
    this.chunkIndex += 1;
    this.chunkBytes = 0;
    this.chunkLines = 0;
    try {
      this.openChunk(null);
    } catch (error) {
      // A failed rotation must not leave the writer unable to flush. Restore the previous
      // chunk's index and counters and reopen it, so the envelopes re-queued by `flush` can be
      // written on a later call once the obstacle is gone (the README promise). The rotation
      // error still propagates.
      this.chunkIndex = previousIndex;
      this.chunkBytes = previousBytes;
      this.chunkLines = previousLines;
      try {
        this.openChunk(null);
      } catch {
        // The previous chunk cannot be reopened either; the rotation error is the one
        // that matters and the writer stays closed (fd = -1).
      }
      throw error;
    }
    // The write cadence: the closed chunk is durable, so this is the moment the record can say
    // `sequence_next` for it. Rotation is also the only place the cap cache is re-read
    // (other producers may have written since the open), which keeps the hot path O(batch).
    this.refreshRunBytes();
    // The record is ADVISORY — the chunk scan is authoritative on resume — so an ENOSPC/EIO
    // while writing it must never fail the flush that just made the batch durable. The failure
    // is remembered (`lastShardsWriteFailure`) and retried at the next write point (the next
    // rotation or close), which re-derives the value from live counters, so nothing has to be
    // replayed; the FIRST such failure also queues a `shards_write_failed` notice, because this
    // path has no other reporter. An identity refusal is a client bug and is still raised:
    // `EvidgateShardRecordMismatchError` comes from the constructor's record READ, never from
    // this write, and this catch never covers it.
    try {
      this.writeShardsRecord();
    } catch (error) {
      if (error instanceof EvidgateShardRecordMismatchError) throw error;
      const failure = error instanceof Error ? error : new Error(String(error));
      this.shardsFailure = failure;
      // The flush above already succeeded, so this failure has no other reporter until `close`
      // — where it arrives as a thrown error, not as a reason. Queue it once (a second failed
      // rotation adds nothing) and name the code only: the message travels to the caller, so it
      // must never carry the record path. A `code` that is not a short bare token (or no code at
      // all) falls back to the error class name.
      const errno = (failure as NodeJS.ErrnoException).code;
      const reason =
        typeof errno === 'string' && /^[A-Za-z0-9_.-]{1,64}$/.test(errno) ? errno : failure.name;
      this.pushNotice('shards_write_failed', `shards.json write failed at rotation (${reason})`);
    }
  }

  /** Re-reads the run directory's byte total; called at rotation, never per batch. */
  private refreshRunBytes(): void {
    this.runBytes = directoryBytes(join(this.spoolDir, this.runId));
    this.recordBytes = this.existingRecordBytes();
  }

  private openChunk(content: Buffer | null): void {
    if (this.chunkIndex < 1) this.chunkIndex = 1;
    const path = this.chunkPath(this.chunkIndex);
    const existing = content ?? this.readChunkIfExists(path);
    if (existing !== null) {
      this.chunkBytes = existing.byteLength;
      this.chunkLines = countLines(existing.toString('utf8'));
    } else {
      this.chunkBytes = 0;
      this.chunkLines = 0;
    }
    // O_NOFOLLOW so a planted chunk symlink is refused; openVerified then rejects a non-regular
    // or foreign-owned file and forces 0600 on the descriptor. The append open is the one that
    // would BLOCK on a planted FIFO, so the target is refused before the open when it is not a
    // regular file.
    this.assertWritableTargetIsRegularFile(path);
    this.fd = this.openVerified(
      path,
      fsConstants.O_WRONLY | fsConstants.O_APPEND | fsConstants.O_CREAT | O_NOFOLLOW,
      SPOOL_FILE_MODE,
    );
  }

  private chunkPath(index: number): string {
    return join(this.directory, `${String(index).padStart(CHUNK_FILE_DIGITS, '0')}.jsonl`);
  }

  private highestChunkIndex(): number {
    let highest = 0;
    for (const entry of readdirSync(this.directory)) {
      const match = CHUNK_FILE_PATTERN.exec(entry);
      if (match) highest = Math.max(highest, Number(match[1] ?? '0'));
    }
    return highest;
  }

  /**
   * A crash mid-`writeSync` can leave a partial line with no trailing newline at the end of the
   * highest chunk. Appending after it would concatenate the new JSON onto the fragment and
   * corrupt the JSONL for the reader, so truncate the fragment back to the last newline and
   * fsync before the first append. Returns the repaired content so the caller can share it with
   * the sequence scan and the chunk open.
   */
  private truncateTornTail(content: Buffer | null): Buffer | null {
    if (content === null) return null;
    if (content.byteLength === 0 || content[content.byteLength - 1] === 0x0a) return content;
    const lastNewline = content.lastIndexOf(0x0a);
    const path = this.chunkPath(this.chunkIndex);
    // O_NOFOLLOW so a planted chunk symlink is refused.
    const fd = this.openVerified(path, fsConstants.O_RDWR | O_NOFOLLOW);
    try {
      ftruncateSync(fd, lastNewline + 1);
      fsyncSync(fd);
    } finally {
      closeSync(fd);
    }
    return content.subarray(0, lastNewline + 1);
  }

  /**
   * The highest sequence already durable on disk. Scans chunks from the highest index down, and
   * within each chunk from the last line backwards, so a torn or corrupt final line falls back to
   * the previous COMPLETE parseable line instead of dropping the whole chunk — otherwise the
   * counter restarts at 1 and reuses durable sequences. The highest chunk's already-read (and
   * repaired) buffer is reused instead of re-read.
   */
  private lastSequenceInChunks(highestContent: Buffer | null): number {
    const indices = readdirSync(this.directory)
      .map((entry) => CHUNK_FILE_PATTERN.exec(entry))
      .filter((match): match is RegExpExecArray => match !== null)
      .map((match) => Number(match[1] ?? '0'))
      .sort((a, b) => b - a);
    for (const index of indices) {
      const content =
        index === this.chunkIndex && highestContent !== null
          ? highestContent
          : this.readChunk(this.chunkPath(index));
      const lines = content.toString('utf8').split('\n');
      for (let i = lines.length - 1; i >= 0; i -= 1) {
        const line = lines[i];
        if (line === undefined || line.length === 0) continue;
        try {
          const parsed = JSON.parse(line) as { sequence?: unknown };
          // Only a safe integer `>= 1` is a sequence, exactly as the `shards.json` and
          // `checkpoint.json` readers require. `1e300` (a float past the safe range) or a
          // negative/float value is not a sequence this writer may resume after: it would move
          // the counter to an imprecise number and the record would carry it. Such a line is
          // unreadable, and — like the `catch` below for a non-JSON tail — the scan keeps going
          // backwards to the last line that IS a sequence.
          const sequence = parsed.sequence;
          if (typeof sequence === 'number' && Number.isSafeInteger(sequence) && sequence >= 1) {
            return sequence;
          }
        } catch {
          // A torn or corrupt line (crash mid-write) is skipped; keep scanning backwards.
        }
      }
    }
    return 0;
  }

  /**
   * Reads `shards.json`. Returns `null` when the file is absent or unusable — a torn/corrupt
   * record is ignored, because the chunk scan alone recovers the sequence — and refuses
   * (`spool_shard_mismatch`, no overwrite) when the record names another producer or shard slot
   * than the caller passed: that is a client bug, not a resume.
   */
  private readShardsRecord(): StoredShards | null {
    const path = join(this.directory, SHARDS_FILE);
    const text = this.readRecordFile(path);
    if (text === null) return null;
    let parsed: Partial<ShardsRecord>;
    try {
      parsed = JSON.parse(text) as Partial<ShardsRecord>;
    } catch {
      // A torn record (crash between write and rename) is ignored: the chunk scan recovers
      // the sequence, and the next rotation/close/restart rewrites the record.
      return null;
    }
    if (typeof parsed !== 'object' || parsed === null) return null;
    if (typeof parsed.producer_id !== 'string') return null;
    const shardIndex =
      typeof parsed.shard_index === 'number' && Number.isSafeInteger(parsed.shard_index)
        ? parsed.shard_index
        : undefined;
    // A record for another producer or shard slot is a client bug (two runs sharing a spool
    // dir, or a recomputed shard index), not a resume: refuse and never overwrite.
    const indexDisagrees =
      this.shardIndex !== undefined && shardIndex !== undefined && shardIndex !== this.shardIndex;
    if (parsed.producer_id !== this.producerId || indexDisagrees) {
      throw new EvidgateShardRecordMismatchError({
        recordPath: path,
        stored: {
          producer_id: parsed.producer_id,
          ...(shardIndex === undefined ? {} : { shard_index: shardIndex }),
        },
        requested: {
          producer_id: this.producerId,
          ...(this.shardIndex === undefined ? {} : { shard_index: this.shardIndex }),
        },
      });
    }
    // A tampered/corrupt record must not jump the sequence to an imprecise value (1e300,
    // 2**53), so require a safe integer; the chunk scan then decides.
    const sequenceNext =
      typeof parsed.sequence_next === 'number' &&
      Number.isSafeInteger(parsed.sequence_next) &&
      parsed.sequence_next >= 1
        ? parsed.sequence_next
        : undefined;
    const shardTotal =
      typeof parsed.shard_total === 'number' && Number.isSafeInteger(parsed.shard_total)
        ? parsed.shard_total
        : undefined;
    const lastRestartAt =
      typeof parsed.last_restart_at === 'string' && parsed.last_restart_at.length > 0
        ? parsed.last_restart_at
        : undefined;
    return {
      producer_id: parsed.producer_id,
      ...(shardIndex === undefined ? {} : { shard_index: shardIndex }),
      ...(shardTotal === undefined ? {} : { shard_total: shardTotal }),
      ...(sequenceNext === undefined ? {} : { sequence_next: sequenceNext }),
      ...(lastRestartAt === undefined ? {} : { last_restart_at: lastRestartAt }),
    };
  }

  /**
   * Reads the legacy `checkpoint.json` as a fallback when `shards.json` is absent. It is never
   * written again; a leftover staging name is removed here, so an old crash between write and
   * rename cannot leave one behind forever.
   */
  private readCheckpoint(): SpoolCheckpoint | null {
    try {
      // Best-effort cleanup of the legacy staging name: we never write it again, and a crash
      // between its write and rename could otherwise leave it behind forever. `unlink` never
      // follows a symlink, and a non-file entry is left alone (it is not ours to remove).
      unlinkSync(join(this.directory, CHECKPOINT_TMP_FILE));
    } catch {
      // ENOENT (nothing to clean) and every other errno: cleanup must not fail the writer.
    }
    const text = this.readRecordFile(join(this.directory, CHECKPOINT_FILE));
    if (text === null) return null;
    let parsed: Partial<SpoolCheckpoint>;
    try {
      parsed = JSON.parse(text) as Partial<SpoolCheckpoint>;
    } catch {
      // A truncated checkpoint (crash between write and rename) is ignored; the chunk
      // scan recovers the sequence.
      return null;
    }
    if (typeof parsed !== 'object' || parsed === null) return null;
    if (
      typeof parsed.next_sequence === 'number' &&
      // A tampered/corrupt checkpoint must not jump the sequence to an imprecise value
      // (1e300, 2**53), so require a safe integer.
      Number.isSafeInteger(parsed.next_sequence) &&
      parsed.next_sequence >= 1
    ) {
      return {
        producer_id: typeof parsed.producer_id === 'string' ? parsed.producer_id : this.producerId,
        next_sequence: parsed.next_sequence,
      };
    }
    return null;
  }

  /** Reads a small record file through an O_NOFOLLOW|O_NONBLOCK, ownership-checked descriptor. */
  private readRecordFile(path: string): string | null {
    let fd: number;
    try {
      // `O_NONBLOCK` so a planted FIFO opens without waiting for a writer; the `fstat` below
      // then refuses the non-regular descriptor instead of blocking the reporter.
      fd = openSync(path, fsConstants.O_RDONLY | O_NOFOLLOW | O_NONBLOCK);
    } catch (error) {
      const code = (error as NodeJS.ErrnoException).code;
      if (code === 'ENOENT') return null;
      // ELOOP (a planted symlink), EACCES, EIO: a record we cannot read is refused rather
      // than read as absent, so a symlinked or unreadable record can never be replaced
      // silently — the same rule the chunks follow.
      throw new Error(
        `@evidgate/playwright-reporter: refusing to read spool record ${path} (${code ?? 'unknown OS error'})`,
      );
    }
    try {
      const stats = fstatSync(fd);
      if (!stats.isFile()) {
        throw new Error(
          `@evidgate/playwright-reporter: refusing to read non-regular spool record ${path}`,
        );
      }
      if (process.platform !== 'win32') {
        const uid = process.getuid?.();
        if (uid !== undefined && stats.uid !== uid) {
          throw new Error(
            `@evidgate/playwright-reporter: spool record ${path} is owned by uid ${stats.uid}, not ${uid}`,
          );
        }
      }
      return readFileSync(fd, 'utf8');
    } finally {
      closeSync(fd);
    }
  }

  /** The size of the existing `shards.json`, so a rewrite counts only its delta (cap cache). */
  private existingRecordBytes(): number {
    try {
      const stats = lstatSync(join(this.directory, SHARDS_FILE));
      return stats.isFile() ? stats.size : 0;
    } catch {
      return 0;
    }
  }

  /**
   * Releases `writer.lock` if this writer still holds it. Best effort: a lock that cannot be
   * inspected is left in place (the pid dies with the process, so a later run sees a stale
   * lock and takes it over) — a release failure must never mask the caller's error.
   */
  private releaseLock(): void {
    const held = this.lock;
    if (held === undefined) return;
    this.lock = undefined;
    untrackLock(held);
    try {
      releaseWriterLock(held);
    } catch {
      // Left in place deliberately: never delete a lock we cannot verify is ours.
    }
  }

  /**
   * A write open must never reach a planted FIFO/socket/device or a directory. `open` with
   * `O_WRONLY` on a FIFO would wait for a reader (or, with one, write spool lines into a pipe),
   * so the target is inspected first and refused when it exists and is not a regular file. A
   * symlink is refused too (`O_NOFOLLOW` would turn it into `ELOOP`); the caller's
   * `openVerified` still re-checks the descriptor for a race.
   */
  private assertWritableTargetIsRegularFile(path: string): void {
    let stats: Stats;
    try {
      stats = lstatSync(path);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return;
      throw error;
    }
    if (!stats.isFile()) {
      throw new Error(
        `@evidgate/playwright-reporter: refusing to use non-regular spool file ${path}`,
      );
    }
  }

  /**
   * Opens a spool file with the given flags (plus `O_NONBLOCK`, see below) and verifies the
   * descriptor: it must be a regular file owned by the current process, and (when a mode is
   * given) is forced to that mode. A non-regular file (a symlink target, FIFO or device) or a
   * foreign-owned file is rejected. On Windows the ownership/mode checks are skipped.
   * `O_NONBLOCK` is added here so no call site can forget it: it is a no-op on a regular file
   * and stops a planted FIFO from blocking whichever open reaches it first (`readChunk`, the
   * torn-tail truncation, the append or the record staging file).
   */
  private openVerified(path: string, flags: number, mode?: number): number {
    const fd = openSync(path, flags | O_NONBLOCK, mode);
    try {
      const stats = fstatSync(fd);
      if (!stats.isFile()) {
        throw new Error(
          `@evidgate/playwright-reporter: refusing to use non-regular spool file ${path}`,
        );
      }
      if (process.platform !== 'win32') {
        const uid = process.getuid?.();
        if (uid !== undefined && stats.uid !== uid) {
          throw new Error(
            `@evidgate/playwright-reporter: spool file ${path} is owned by uid ${stats.uid}, not ${uid}`,
          );
        }
        if (mode !== undefined) fchmodSync(fd, mode);
      }
      return fd;
    } catch (error) {
      closeSync(fd);
      throw error;
    }
  }

  /**
   * A directory the writer creates or reuses under `spool_dir` must be a real directory owned by
   * the current process; a symlink or a foreign-owned directory is rejected, and an owned one is
   * forced to 0700. `spool_dir` itself is never passed here (a symlinked spool root is allowed and
   * keeps its mode).
   */
  private hardenDirectory(path: string): void {
    const stats = lstatSync(path);
    if (stats.isSymbolicLink()) {
      throw new Error(
        `@evidgate/playwright-reporter: refusing to use symlinked spool directory ${path}`,
      );
    }
    if (!stats.isDirectory()) {
      throw new Error(`@evidgate/playwright-reporter: spool path ${path} is not a directory`);
    }
    if (process.platform === 'win32') return;
    const uid = process.getuid?.();
    if (uid !== undefined && stats.uid !== uid) {
      throw new Error(
        `@evidgate/playwright-reporter: spool directory ${path} is owned by uid ${stats.uid}, not ${uid}`,
      );
    }
    chmodSync(path, SPOOL_DIR_MODE);
  }

  /** Reads a chunk, or `null` when it does not exist yet. */
  private readChunkIfExists(path: string): Buffer | null {
    try {
      return this.readChunk(path);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null;
      throw error;
    }
  }

  /** Reads a chunk through an O_NOFOLLOW, ownership-checked descriptor. */
  private readChunk(path: string): Buffer {
    const fd = this.openVerified(path, fsConstants.O_RDONLY | O_NOFOLLOW);
    try {
      return readFileSync(fd);
    } finally {
      closeSync(fd);
    }
  }
}

function countLines(text: string): number {
  let count = 0;
  for (const line of text.split('\n')) if (line.length > 0) count += 1;
  return count;
}
