// 04.2.3-T1.1: the CLI's read/append side of the on-disk spool.
//
// Layout (D1 of the 04.2.3 contract; it adopts the merged reporter's writer so a run
// spooled by `@evidgate/playwright-reporter` is readable here):
//
//   <spool_dir>/<run_id>/<producer_id>/NNNNNN.jsonl   6-digit chunks, one envelope per line
//   <spool_dir>/<run_id>/<producer_id>/shards.json      writer-owned (never written here):
//                                                      shard identity + sequence_next floor;
//                                                      legacy checkpoint.json read as fallback
//   <spool_dir>/<run_id>/<producer_id>/writer.lock      reporter-held writer lock (D-S56-Q8)
//   <spool_dir>/<run_id>/<producer_id>/uploads/         CLI-owned upload journal (04.2.4)
//   <spool_dir>/<run_id>/<producer_id>/cursor.json      CLI-owned ack cursor (0600)
//   <spool_dir>/<run_id>/<producer_id>/spool.lock       CLI single-writer lock (spoolLock.ts)
//
// Durability: appended lines are written, fsynced and the directory fsynced before this
// module reports success; `ack` makes every chunk it has not already made durable (plus
// the highest chunk, the only one a writer may still be appending to) and the directory
// durable BEFORE advancing the cursor, which is itself written tmp + fsync + rename +
// directory fsync, so a crash can only ever repeat an ack, never lose one.
//
// Reads are streaming (bounded blocks, one line at a time): a chunk is never loaded whole
// and neither is a run. One line is bounded by {@link maxSpoolLineBytes}; a longer line is
// `spool_corrupt`, never an unbounded buffer. A final line without `\n` is a torn write
// from a crashed writer and is ignored by the reader (D3); any other unparseable line, a
// non-contiguous sequence, or a torn line in a non-final chunk is `spool_corrupt` naming
// chunk + line (never skipped). The appender repairs a torn tail it finds in the highest
// chunk before its first write, so appending can never concatenate JSON onto a fragment.
//
// Cap (D4): `EVIDGATE_SPOOL_MAX_BYTES` bounds the sum of the run directory's file sizes.
// The check runs BEFORE a single byte is written, so `SpoolCapExceededError` always means
// "nothing was appended" and names the run, the producer, the cap, the used bytes and the
// unsent sequence range.
import { closeSync, constants as fsConstants, fsyncSync, openSync } from 'node:fs';
import { chmod, lstat, mkdir, open, readdir, rename, rm, unlink } from 'node:fs/promises';
import { basename, join, resolve, sep } from 'node:path';

import { CliError } from './errors.js';
import { assertNoLiveWriter, withSpoolLock } from './spoolLock.js';

import type { ProducerLockOptions } from './spoolLock.js';
import type { Dirent } from 'node:fs';
import type { FileHandle } from 'node:fs/promises';

/** `EVIDGATE_SPOOL_MAX_BYTES` default: 256 MiB per run directory (D4). */
export const defaultSpoolMaxBytes = 268_435_456;
/** Spool directories are created 0700. */
export const spoolDirMode = 0o700;
/** Every spool file this module writes is 0600 (task file §8). */
export const spoolFileMode = 0o600;
/** A chunk rotates at 1 MiB (the merged writer's limit, D1). */
export const spoolChunkMaxBytes = 1_048_576;
/** A chunk rotates at 1000 lines (the merged writer's limit, D1). */
export const spoolChunkMaxLines = 1000;
/** Chunk names are zero-padded to six digits: `000001.jsonl`. */
export const spoolChunkDigits = 6;
/** The CLI-owned ack cursor (D1). */
export const spoolCursorFileName = 'cursor.json';
/** A leftover tmp from a crash between write and rename. */
export const spoolCursorTmpFileName = 'cursor.json.tmp';
/** Envelope `producer_id`/`run_id` path-segment limit (writer parity). */
export const spoolIdMaxLength = 128;

const chunkFilePattern = /^(\d{6})\.jsonl$/;
/** The only characters allowed in a `run_id`/`producer_id` path segment (writer parity). */
const spoolIdPattern = /^[A-Za-z0-9._-]+$/;
const controlCharPattern = /[\u0000-\u001f\u007f]/;
/** Block size for streaming scans; bounds memory to one block plus one line. */
const readBlockBytes = 64 * 1024;
/**
 * Longest tail line {@link lastTerminatedSequence} is willing to read before giving up
 * (it returns `null`, and the caller falls back to the streaming scan, whose hard bound is
 * {@link maxSpoolLineBytes}). Deliberately smaller than that bound: the tail fast path must
 * stay cheap, and a longer last line is still read correctly, just by the full scan.
 */
const maxTailLineBytes = 1_048_576;
/**
 * Largest legal line in a chunk, in bytes: the intake's own per-line guard
 * (`maxInputLineBytes` in `commands/eventsSend.ts`, 32 MiB, kept in sync there because a
 * legal envelope must survive append → read back). A chunk is rotated at
 * {@link spoolChunkMaxBytes}, but one line is always written whole, so this — not the
 * rotation size — is the hard bound a scanner must enforce. A longer line cannot have
 * come from this CLI or from a writer following the same contract, so it is
 * `spool_corrupt` instead of a reason to grow the heap without limit.
 */
export const maxSpoolLineBytes = 32 * 1_048_576;
/**
 * `O_NOFOLLOW` is undefined on Windows; the POSIX symlink hardening degrades to a plain
 * open there (writer parity).
 */
export const O_NOFOLLOW: number = process.platform === 'win32' ? 0 : fsConstants.O_NOFOLLOW;
/**
 * `O_NONBLOCK` is a POSIX-only flag (undefined on Windows). Opening a FIFO or a device
 * with it makes the open return instead of parking the whole CLI in the thread pool; the
 * descriptor is then rejected by the regular-file check in {@link openVerifiedFile}.
 */
export const O_NONBLOCK: number = process.platform === 'win32' ? 0 : fsConstants.O_NONBLOCK;

/** One event read back from a chunk. */
export interface SpoolEvent {
  /** The envelope's `sequence` (contiguous from `cursor.nextUnackedSequence`). */
  readonly sequence: number;
  /** The envelope's `event_id`, the server-side dedupe identity. */
  readonly eventId: string;
  /** The exact JSON text of the line, without the trailing `\n`. */
  readonly line: string;
  /**
   * UTF-8 bytes of `line` including its `\n`, measured when the line was read. Nothing on
   * the send path reads it: the batcher sizes a batch from `line` itself (`batcher.ts`
   * body accounting) and the sender builds the body from `line`. It is kept because the
   * spool specs assert the measured length is exactly the on-disk byte count.
   */
  readonly lineBytes: number;
  /** The chunk file it came from, e.g. `000003.jsonl` (for error messages). */
  readonly chunk: string;
  /** 1-based physical line number inside that chunk. */
  readonly lineNumber: number;
}

/** The CLI-owned ack cursor (D1). `next_unacked_sequence === last_acked_sequence + 1`. */
export interface SpoolCursor {
  readonly producerId: string;
  readonly nextUnackedSequence: number;
  readonly lastAckedSequence: number;
  readonly ackedCount: number;
}

/** Options of {@link ProducerSpool.open}. */
export interface OpenProducerSpoolOptions {
  /** The spool root (`EVIDGATE_SPOOL_DIR`), already absolute-resolved by the command. */
  readonly spoolDir: string;
  readonly runId: string;
  readonly producerId: string;
  /** The run-directory byte cap (`EVIDGATE_SPOOL_MAX_BYTES`). */
  readonly maxBytes: number;
  /**
   * Create `<spool_dir>/<run_id>/<producer_id>` (0700) when absent. `false` (default)
   * opens an existing producer spool only, and fails with `spool_missing`.
   */
  readonly createIfMissing?: boolean;
}

/**
 * `spool_cap_exceeded` (AC3, exit 3): the run's spool cannot take another byte without
 * crossing `EVIDGATE_SPOOL_MAX_BYTES`. Nothing was written by the call that throws it.
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

/** `spool_corrupt` (D3, exit 3): a spool line that is neither a torn tail nor valid. */
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

/** `spool_missing` (D7, exit 3): there is no spool for this run/producer. */
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

/** Validates a spool path segment (writer parity, S3 analogue). */
export function assertValidSpoolId(field: 'run_id' | 'producer_id', value: string): void {
  if (typeof value !== 'string' || value.length === 0) {
    throw new InvalidSpoolIdError(field, String(value), 'must not be empty');
  }
  if (value === '.' || value === '..') {
    throw new InvalidSpoolIdError(field, value, 'must not be "." or ".."');
  }
  if (value.includes('/') || value.includes('\\')) {
    throw new InvalidSpoolIdError(field, value, 'must not contain a path separator');
  }
  if (controlCharPattern.test(value)) {
    throw new InvalidSpoolIdError(field, value, 'must not contain a control character');
  }
  if (!spoolIdPattern.test(value)) {
    throw new InvalidSpoolIdError(
      field,
      value,
      'must contain only letters, digits, ".", "_" and "-"',
    );
  }
  if (value.length > spoolIdMaxLength) {
    throw new InvalidSpoolIdError(field, value, `must be at most ${spoolIdMaxLength} characters`);
  }
}

/** A spool path that is not a real, owned directory/file (symlink, foreign owner, file). */
function unsafePath(message: string): CliError {
  return new CliError(message, {
    code: 'spool_unsafe_path',
    hint: 'the spool directory must be owned by the current user and contain no symlinks',
  });
}

/**
 * Defence in depth for S3: even after id validation, the resolved directory must stay
 * inside the resolved spool root.
 */
function assertWithinSpoolDir(
  spoolDir: string,
  directory: string,
  field: 'run_id' | 'producer_id',
  value: string,
): void {
  const root = resolve(spoolDir);
  const target = resolve(directory);
  if (target !== root && !target.startsWith(root + sep)) {
    throw new InvalidSpoolIdError(field, value, `resolves outside the spool directory ${root}`);
  }
}

/** The sorted producer ids of a run (valid single segments only; symlinks never listed). */
export async function listProducers(spoolDir: string, runId: string): Promise<string[]> {
  assertValidSpoolId('run_id', runId);
  const runDirectory = join(spoolDir, runId);
  assertWithinSpoolDir(spoolDir, runDirectory, 'run_id', runId);
  let entries: Dirent[];
  try {
    entries = await readdir(runDirectory, { withFileTypes: true });
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return [];
    throw error;
  }
  const producers: string[] = [];
  for (const entry of entries) {
    if (!entry.isDirectory() || entry.isSymbolicLink()) continue;
    if (entry.name === '.' || entry.name === '..') continue;
    if (!spoolIdPattern.test(entry.name) || entry.name.length > spoolIdMaxLength) continue;
    producers.push(entry.name);
  }
  return producers.sort(compareStrings);
}

/** Total bytes of every regular file under `<spool_dir>/<run_id>` (the D4 cap base). */
export function runDirBytes(spoolDir: string, runId: string): Promise<number> {
  assertValidSpoolId('run_id', runId);
  const runDirectory = join(spoolDir, runId);
  assertWithinSpoolDir(spoolDir, runDirectory, 'run_id', runId);
  return directoryBytes(runDirectory);
}

/**
 * Append-only spool access for one `(run_id, producer_id)` pair: the CLI's intake
 * (`append`), the sender's read (`readUnacked`), the ack cursor, and the lock/purge that
 * bracket a send.
 */
export class ProducerSpool {
  readonly spoolDir: string;
  readonly runId: string;
  readonly producerId: string;
  /** `<spool_dir>/<run_id>`. */
  readonly runDirectory: string;
  /** `<spool_dir>/<run_id>/<producer_id>`. */
  readonly directory: string;
  /** The run-directory cap in bytes (`EVIDGATE_SPOOL_MAX_BYTES`). */
  readonly maxBytes: number;

  private chunkIndex: number | undefined;
  private chunkBytes: number | undefined;
  private chunkLines: number | undefined;
  /**
   * Chunks this process has already fsynced and that are now immutable (the writer only
   * ever appends to the highest chunk), so `ack` never has to sync them again.
   */
  private readonly syncedChunks = new Set<string>();

  private constructor(o: OpenProducerSpoolOptions) {
    assertValidSpoolId('run_id', o.runId);
    assertValidSpoolId('producer_id', o.producerId);
    if (!Number.isSafeInteger(o.maxBytes) || o.maxBytes <= 0) {
      throw new CliError(
        `EVIDGATE_SPOOL_MAX_BYTES must be a positive integer number of bytes, got ${String(o.maxBytes)}`,
        { code: 'invalid_spool_max_bytes' },
      );
    }
    this.spoolDir = o.spoolDir;
    this.runId = o.runId;
    this.producerId = o.producerId;
    this.maxBytes = o.maxBytes;
    this.runDirectory = join(o.spoolDir, o.runId);
    this.directory = join(this.runDirectory, o.producerId);
    assertWithinSpoolDir(o.spoolDir, this.runDirectory, 'run_id', o.runId);
    assertWithinSpoolDir(o.spoolDir, this.directory, 'producer_id', o.producerId);
  }

  /**
   * Opens (and with `createIfMissing`, creates 0700) a producer spool. Refuses symlinked
   * or foreign-owned run/producer directories and any path that escapes `spoolDir`.
   */
  static async open(o: OpenProducerSpoolOptions): Promise<ProducerSpool> {
    const spool = new ProducerSpool(o);
    await spool.initialize(o.createIfMissing === true);
    return spool;
  }

  private async initialize(createIfMissing: boolean): Promise<void> {
    if (createIfMissing) {
      // One level at a time: the run directory is hardened BEFORE the producer directory
      // is created, so a symlinked run directory is refused without a write ever landing
      // inside its target.
      await mkdirOwned(this.runDirectory);
      await hardenDirectory(this.runDirectory);
      await mkdirOwned(this.directory);
      await hardenDirectory(this.directory);
      return;
    }
    // A missing run or producer directory is `spool_missing` (D7), never a raw errno.
    await this.hardenExisting(this.runDirectory);
    await this.hardenExisting(this.directory);
  }

  private async hardenExisting(directory: string): Promise<void> {
    try {
      await hardenDirectory(directory);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') {
        throw new SpoolMissingError({
          runId: this.runId,
          producerId: this.producerId,
          directory: this.directory,
        });
      }
      throw error;
    }
  }

  /**
   * Runs `fn` holding the exclusive producer lock; a live holder is `spool_locked`.
   *
   * `options.requireNoLiveWriter` adds the D-S56-Q8 guard: the reporter's `writer.lock` is
   * inspected after `spool.lock` is taken, and a live reporter refuses the command with
   * `spool_locked` (exit 3) before `fn` runs. Only the chunk readers/purgers (events send,
   * runs finalize) pass it; the upload journal keeps the plain lock, so a running test can
   * never block an artifact upload.
   */
  withLock<T>(fn: () => Promise<T>, options: ProducerLockOptions = {}): Promise<T> {
    return withSpoolLock(this.directory, async () => {
      if (options.requireNoLiveWriter === true) await assertNoLiveWriter(this.directory, options);
      return await fn();
    });
  }

  /**
   * Appends complete envelope lines, all-or-nothing per call: the cap is checked before
   * the first write, so a `SpoolCapExceededError` leaves the spool byte-identical. Lines
   * are written in order, the chunk rotated at 1000 lines / 1 MiB, and the chunk plus the
   * directory fsynced before returning. A torn tail in the highest chunk is truncated
   * back to the last newline before the first write.
   *
   * The lines of one chunk segment are concatenated into one buffer and written with a
   * single `write`, so a 1000-line chunk costs one syscall instead of 1000: the segment is
   * at most one chunk (plus the single line that rotates it), which keeps the buffer
   * bounded while leaving the rollback, cap and rotation rules untouched.
   */
  async append(lines: readonly string[]): Promise<void> {
    if (lines.length === 0) return;
    const parsed = lines.map((line, index) => parseAppendLine(line, index));
    for (let i = 1; i < parsed.length; i += 1) {
      const previous = parsed[i - 1];
      const current = parsed[i];
      if (previous === undefined || current === undefined) continue;
      if (current.sequence <= previous.sequence) {
        throw new CliError(
          `spool line ${i + 1} has sequence ${current.sequence}, which does not follow ` +
            `${previous.sequence}`,
          {
            code: 'invalid_spool_line',
            hint: 'append one producer’s envelopes in ascending sequence order',
          },
        );
      }
    }
    const requiredBytes = parsed.reduce((sum, item) => sum + item.lineBytes, 0);
    const cursor = await this.cursor();
    const usedBytes = await runDirBytes(this.spoolDir, this.runId);
    const lastAppended = parsed[parsed.length - 1];
    const unsentLastSequence = Math.max(
      lastAppended?.sequence ?? cursor.nextUnackedSequence,
      cursor.nextUnackedSequence,
    );
    if (usedBytes + requiredBytes > this.maxBytes) {
      // Checks only so far: no chunk was repaired, created or written, so the spool is
      // byte-identical when this throws (AC3 "nothing written").
      throw new SpoolCapExceededError({
        runId: this.runId,
        producerId: this.producerId,
        capBytes: this.maxBytes,
        usedBytes,
        requiredBytes,
        unsentFirstSequence: cursor.nextUnackedSequence,
        unsentLastSequence,
      });
    }
    const state = await this.chunkState();
    let index = state.index;
    const startIndex = state.index;
    const startBytes = state.bytes;
    let bytes = state.bytes;
    let linesInChunk = state.lines;
    let handle: FileHandle | null = null;
    /** Envelope texts of the current chunk segment, joined and written once on flush. */
    let segment: string[] = [];
    const flushSegment = async (): Promise<void> => {
      if (segment.length === 0 || handle === null) return;
      const buffer = Buffer.from(`${segment.join('\n')}\n`, 'utf8');
      segment = [];
      await writeAll(handle, buffer);
    };
    try {
      for (const item of parsed) {
        if (bytes >= spoolChunkMaxBytes || linesInChunk >= spoolChunkMaxLines) {
          await flushSegment();
          if (handle !== null) {
            await handle.sync();
            await handle.close();
            handle = null;
          }
          index += 1;
          bytes = 0;
          linesInChunk = 0;
        }
        if (handle === null) handle = await this.openChunkForAppend(index);
        segment.push(item.text);
        bytes += item.lineBytes;
        linesInChunk += 1;
      }
      await flushSegment();
      if (handle !== null) {
        await handle.sync();
        await handle.close();
        handle = null;
      }
    } catch (error) {
      // Roll the chunk back to where this call started, so a failed append never leaves a
      // partial line (the JSONL stays parseable). Chunks rotated earlier in the call stay
      // durable and valid; the sequence they hold is still contiguous from the cursor.
      if (handle !== null) {
        const rollbackTarget = index === startIndex ? startBytes : 0;
        try {
          await handle.truncate(rollbackTarget);
        } catch {
          // Best effort: the write error is the one that matters.
        }
        try {
          await handle.close();
        } catch {
          // Already closed by the failed write.
        }
      }
      throw error;
    }
    fsyncDirectorySync(this.directory);
    this.chunkIndex = index;
    this.chunkBytes = bytes;
    this.chunkLines = linesInChunk;
  }

  /**
   * Streams the events from `cursor.nextUnackedSequence` onward, in sequence order. A
   * final line without `\n` is a torn write and is skipped (D3); every other bad line, a
   * gap and a regression are `SpoolCorruptError` naming chunk + line.
   */
  async *readUnacked(): AsyncGenerator<SpoolEvent> {
    const cursor = await this.cursor();
    const chunks = await this.listChunks();
    let emitted = 0;
    for (let i = 0; i < chunks.length; i += 1) {
      const chunk = chunks[i];
      if (chunk === undefined) continue;
      const isLast = i === chunks.length - 1;
      if (!isLast) {
        // Skip a chunk whose last durable sequence is already acked, without streaming it.
        const tail = await lastTerminatedSequence(chunk.path);
        if (tail !== null && tail < cursor.nextUnackedSequence) continue;
      }
      for await (const raw of readChunkLines(chunk.path)) {
        if (!raw.terminated) {
          if (isLast) continue;
          throw new SpoolCorruptError({
            chunk: chunk.name,
            lineNumber: raw.lineNumber,
            reason: 'line is not newline-terminated but a later chunk exists',
          });
        }
        const parsed = parseSpoolLine(raw.text, chunk.name, raw.lineNumber);
        if (emitted === 0 && parsed.sequence < cursor.nextUnackedSequence) continue;
        const expected = cursor.nextUnackedSequence + emitted;
        if (parsed.sequence !== expected) {
          throw new SpoolCorruptError({
            chunk: chunk.name,
            lineNumber: raw.lineNumber,
            reason:
              parsed.sequence < expected
                ? `sequence ${parsed.sequence} regresses from the expected ${expected}`
                : `sequence ${parsed.sequence} skips the expected ${expected}`,
          });
        }
        emitted += 1;
        yield {
          sequence: parsed.sequence,
          eventId: parsed.eventId,
          line: raw.text,
          lineBytes: Buffer.byteLength(raw.text, 'utf8') + 1,
          chunk: chunk.name,
          lineNumber: raw.lineNumber,
        };
      }
    }
  }

  /** The ack cursor; a missing `cursor.json` means "nothing acked yet". */
  async cursor(): Promise<SpoolCursor> {
    let text: string;
    try {
      text = await readVerifiedText(join(this.directory, spoolCursorFileName));
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') {
        return {
          producerId: this.producerId,
          nextUnackedSequence: 1,
          lastAckedSequence: 0,
          ackedCount: 0,
        };
      }
      throw error;
    }
    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch {
      throw new SpoolCorruptError({
        chunk: spoolCursorFileName,
        lineNumber: 1,
        reason: 'not valid JSON',
      });
    }
    if (typeof parsed !== 'object' || parsed === null) {
      throw new SpoolCorruptError({
        chunk: spoolCursorFileName,
        lineNumber: 1,
        reason: 'not a JSON object',
      });
    }
    const record: object = parsed;
    const fileProducer: unknown = 'producer_id' in record ? record.producer_id : undefined;
    const nextUnacked: unknown =
      'next_unacked_sequence' in record ? record.next_unacked_sequence : undefined;
    const lastAcked: unknown =
      'last_acked_sequence' in record ? record.last_acked_sequence : undefined;
    const ackedCount: unknown = 'acked_count' in record ? record.acked_count : undefined;
    if (fileProducer !== this.producerId) {
      throw new SpoolCorruptError({
        chunk: spoolCursorFileName,
        lineNumber: 1,
        reason: `belongs to producer ${JSON.stringify(fileProducer)}`,
      });
    }
    if (typeof nextUnacked !== 'number' || !Number.isSafeInteger(nextUnacked) || nextUnacked < 1) {
      throw new SpoolCorruptError({
        chunk: spoolCursorFileName,
        lineNumber: 1,
        reason: 'next_unacked_sequence must be an integer >= 1',
      });
    }
    if (typeof lastAcked !== 'number' || !Number.isSafeInteger(lastAcked) || lastAcked < 0) {
      throw new SpoolCorruptError({
        chunk: spoolCursorFileName,
        lineNumber: 1,
        reason: 'last_acked_sequence must be an integer >= 0',
      });
    }
    if (typeof ackedCount !== 'number' || !Number.isSafeInteger(ackedCount) || ackedCount < 0) {
      throw new SpoolCorruptError({
        chunk: spoolCursorFileName,
        lineNumber: 1,
        reason: 'acked_count must be an integer >= 0',
      });
    }
    if (lastAcked !== nextUnacked - 1) {
      throw new SpoolCorruptError({
        chunk: spoolCursorFileName,
        lineNumber: 1,
        reason: 'last_acked_sequence must be next_unacked_sequence - 1',
      });
    }
    return {
      producerId: this.producerId,
      nextUnackedSequence: nextUnacked,
      lastAckedSequence: lastAcked,
      ackedCount,
    };
  }

  /**
   * Advances the ack cursor to `upToSequence`, adding `addedCount` acked events. Only a
   * contiguous prefix can be acked: going backwards, or past the last sequence on disk,
   * is refused. Every chunk the cursor claims — and the directory — is durable before the
   * cursor is replaced (see {@link fsyncChunks} for why that is O(1), not O(chunks)).
   */
  async ack(upToSequence: number, addedCount: number): Promise<void> {
    if (!Number.isSafeInteger(upToSequence) || upToSequence < 0) {
      throw invalidAck(`sequence ${String(upToSequence)} is not a non-negative integer`);
    }
    if (!Number.isSafeInteger(addedCount) || addedCount < 0) {
      throw invalidAck(`count ${String(addedCount)} is not a non-negative integer`);
    }
    const cursor = await this.cursor();
    if (upToSequence < cursor.lastAckedSequence) {
      throw invalidAck(
        `sequence ${upToSequence} is behind the acked sequence ${cursor.lastAckedSequence}`,
      );
    }
    const lastOnDisk = await this.lastOnDiskSequence();
    if (upToSequence > lastOnDisk) {
      throw invalidAck(
        `sequence ${upToSequence} is ahead of the last spooled sequence ${lastOnDisk}`,
      );
    }
    if (upToSequence === cursor.lastAckedSequence && addedCount === 0) return;
    await this.fsyncChunks();
    await this.writeCursor({
      producerId: cursor.producerId,
      nextUnackedSequence: upToSequence + 1,
      lastAckedSequence: upToSequence,
      ackedCount: cursor.ackedCount + addedCount,
    });
  }

  /** `first..last` of the events still to send, or `null` when everything is acked. */
  async unsentRange(): Promise<{ first: number; last: number } | null> {
    const cursor = await this.cursor();
    const last = await this.lastOnDiskSequence();
    if (last < cursor.nextUnackedSequence) return null;
    return { first: cursor.nextUnackedSequence, last };
  }

  /**
   * Removes the producer directory and everything in it (only after a successful
   * finalize). The run and producer directories are re-checked immediately before the
   * recursive `rm`: `rm` would otherwise follow a directory swapped for a symlink since
   * the spool was opened and delete the link's target instead.
   */
  async purge(): Promise<void> {
    for (const directory of [this.runDirectory, this.directory]) {
      try {
        await assertRealOwnedDirectory(directory);
      } catch (error) {
        // An already-removed directory is not an error for `rm --force` parity.
        if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
      }
    }
    await rm(this.directory, { recursive: true, force: true });
    this.chunkIndex = undefined;
    this.chunkBytes = undefined;
    this.chunkLines = undefined;
    this.syncedChunks.clear();
  }

  private chunkPath(index: number): string {
    return join(this.directory, `${String(index).padStart(spoolChunkDigits, '0')}.jsonl`);
  }

  private async listChunks(): Promise<readonly ChunkFile[]> {
    let names: string[];
    try {
      names = await readdir(this.directory);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return [];
      throw error;
    }
    const chunks: ChunkFile[] = [];
    for (const name of names) {
      const match = chunkFilePattern.exec(name);
      if (match === null) continue;
      chunks.push({ index: Number(match[1] ?? '0'), name, path: join(this.directory, name) });
    }
    chunks.sort((a, b) => a.index - b.index);
    return chunks;
  }

  /**
   * The chunk the next line goes into: the highest existing chunk (after repairing a torn
   * tail it may end with), or `000001.jsonl` when the producer has none yet. A consumed
   * chunk is never reused: rotation always moves to `highest + 1`, never to the lowest ack
   * boundary (D2).
   */
  private async chunkState(): Promise<{ index: number; bytes: number; lines: number }> {
    if (
      this.chunkIndex !== undefined &&
      this.chunkBytes !== undefined &&
      this.chunkLines !== undefined
    ) {
      return { index: this.chunkIndex, bytes: this.chunkBytes, lines: this.chunkLines };
    }
    const chunks = await this.listChunks();
    const highest = chunks[chunks.length - 1];
    let index: number;
    let bytes: number;
    let lines: number;
    if (highest === undefined) {
      index = 1;
      bytes = 0;
      lines = 0;
    } else {
      bytes = await truncateTornTail(highest.path);
      index = highest.index;
      lines = await countChunkLines(highest.path);
    }
    this.chunkIndex = index;
    this.chunkBytes = bytes;
    this.chunkLines = lines;
    return { index, bytes, lines };
  }

  private async openChunkForAppend(index: number): Promise<FileHandle> {
    const path = this.chunkPath(index);
    const exists = await pathExists(path);
    const flags = exists
      ? fsConstants.O_WRONLY | fsConstants.O_APPEND | O_NOFOLLOW
      : fsConstants.O_WRONLY |
        fsConstants.O_CREAT |
        fsConstants.O_EXCL |
        fsConstants.O_APPEND |
        O_NOFOLLOW;
    return openVerifiedFile(path, flags, spoolFileMode);
  }

  /**
   * The highest sequence durable on disk (0 when the producer has no valid line yet).
   * Read fresh on every call: D2 lets the reporter keep appending without the lock, so a
   * cached high-water mark would make `ack` reject events that are already on disk.
   */
  private async lastOnDiskSequence(): Promise<number> {
    const chunks = await this.listChunks();
    let value = 0;
    for (let i = chunks.length - 1; i >= 0; i -= 1) {
      const chunk = chunks[i];
      if (chunk === undefined) continue;
      const tail = await lastTerminatedSequence(chunk.path);
      if (tail !== null) {
        value = tail;
        break;
      }
      const scanned = await scanLastSequence(chunk.path);
      if (scanned !== null) {
        value = scanned;
        break;
      }
    }
    return value;
  }

  /**
   * Makes everything the cursor is about to claim durable, then the directory. Runs BEFORE
   * the cursor may advance.
   *
   * Cost is O(1) syscalls per call, not O(chunks): the writer only ever appends to the
   * highest chunk (rotation always moves to `highest + 1`, D2), so every lower chunk is
   * immutable and was already fsynced — by `append` when this process rotated it, or by
   * this method once, after which its name is remembered in {@link syncedChunks}. Only the
   * highest chunk can have grown since the last ack (a writer appending without the lock,
   * D2), so it is synced on every call. That keeps a send at O(chunks) syncs in total
   * instead of the O(batches × chunks) the previous every-chunk sweep cost.
   *
   * Restart safety rests on absence, never on presence: {@link syncedChunks} is empty in a
   * fresh process and a name enters it only after this process opened and fsynced that
   * chunk, so "not in the set" always means "not yet made durable by this process" and the
   * first ack of a new process syncs every chunk on disk. Nothing here assumes a chunk was
   * durable because `append` wrote it: the reporter appends without the CLI lock (D2), so
   * a chunk below the highest is trusted only after this process synced it itself.
   */
  private async fsyncChunks(): Promise<void> {
    const chunks = await this.listChunks();
    for (let i = 0; i < chunks.length; i += 1) {
      const chunk = chunks[i];
      if (chunk === undefined) continue;
      const isHighest = i === chunks.length - 1;
      if (!isHighest && this.syncedChunks.has(chunk.name)) continue;
      const handle = await openVerifiedFile(chunk.path, fsConstants.O_RDONLY | O_NOFOLLOW);
      try {
        await handle.sync();
      } finally {
        await handle.close();
      }
      if (!isHighest) this.syncedChunks.add(chunk.name);
    }
    fsyncDirectorySync(this.directory);
  }

  /** Atomic cursor replace: tmp (0600) + fsync + rename + directory fsync (D1). */
  private async writeCursor(cursor: SpoolCursor): Promise<void> {
    const state = {
      producer_id: cursor.producerId,
      next_unacked_sequence: cursor.nextUnackedSequence,
      last_acked_sequence: cursor.lastAckedSequence,
      acked_count: cursor.ackedCount,
      updated_at: new Date().toISOString(),
    };
    const tmpPath = join(this.directory, spoolCursorTmpFileName);
    const targetPath = join(this.directory, spoolCursorFileName);
    // A leftover tmp is a crash between write and rename; a planted symlink at that path
    // is unlinked, never truncated through.
    await unlink(tmpPath).catch((error: unknown) => {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
    });
    const handle = await openVerifiedFile(
      tmpPath,
      fsConstants.O_WRONLY | fsConstants.O_CREAT | fsConstants.O_EXCL | O_NOFOLLOW,
      spoolFileMode,
    );
    try {
      await writeAll(handle, Buffer.from(`${JSON.stringify(state)}\n`, 'utf8'));
      await handle.sync();
    } finally {
      await handle.close();
    }
    await rename(tmpPath, targetPath);
    fsyncDirectorySync(this.directory);
  }
}

interface ChunkFile {
  readonly index: number;
  readonly name: string;
  readonly path: string;
}

function invalidAck(reason: string): CliError {
  return new CliError(`cannot advance the spool cursor: ${reason}`, {
    code: 'invalid_ack',
    hint: 'ack only a contiguous prefix that is already durable in the spool',
  });
}

function compareStrings(a: string, b: string): number {
  if (a === b) return 0;
  return a < b ? -1 : 1;
}

/** Sums regular files under `root`, never following a symlink. Missing tree = 0 bytes. */
async function directoryBytes(root: string): Promise<number> {
  let total = 0;
  const pending: string[] = [root];
  while (pending.length > 0) {
    const directory = pending.pop();
    if (directory === undefined) continue;
    let entries: Dirent[];
    try {
      entries = await readdir(directory, { withFileTypes: true });
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') continue;
      throw error;
    }
    for (const entry of entries) {
      if (entry.isSymbolicLink()) continue;
      const child = join(directory, entry.name);
      if (entry.isDirectory()) {
        pending.push(child);
        continue;
      }
      try {
        const stats = await lstat(child);
        if (stats.isFile()) total += stats.size;
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code === 'ENOENT') continue;
        throw error;
      }
    }
  }
  return total;
}

/** `mkdir -p` that tolerates "something is already there" (`hardenDirectory` names it). */
export async function mkdirOwned(directory: string): Promise<void> {
  await mkdir(directory, { recursive: true, mode: spoolDirMode }).catch((error: unknown) => {
    const code = (error as NodeJS.ErrnoException).code;
    // EEXIST/ENOENT: a file, a directory or a dangling symlink is already at the path.
    if (code !== 'EEXIST' && code !== 'ENOENT') throw error;
  });
}

/**
 * Requires `path` to be a real directory (never a symlink) owned by this process. Shared
 * by {@link hardenDirectory} and `ProducerSpool.purge`, which re-checks the run and
 * producer directories immediately before its recursive `rm` (a path checked at open time
 * can be swapped for a symlink before the removal: TOCTOU).
 */
export async function assertRealOwnedDirectory(path: string): Promise<void> {
  const stats = await lstat(path);
  if (stats.isSymbolicLink()) {
    throw unsafePath(`refusing to use symlinked spool directory ${path}`);
  }
  if (!stats.isDirectory()) {
    throw unsafePath(`spool path ${path} is not a directory`);
  }
  if (process.platform === 'win32') return;
  const uid = process.getuid?.();
  if (uid !== undefined && stats.uid !== uid) {
    throw unsafePath(`spool directory ${path} is owned by uid ${stats.uid}, not ${uid}`);
  }
}

/**
 * A directory the CLI creates or reuses under `spool_dir` must be a real directory owned
 * by this process (a planted symlink could redirect every write) and is forced to 0700.
 */
export async function hardenDirectory(path: string): Promise<void> {
  await assertRealOwnedDirectory(path);
  if (process.platform === 'win32') return;
  await chmod(path, spoolDirMode);
}

/**
 * Opens a spool file, verifying it is a regular file of ours and forcing its mode.
 *
 * `O_NOFOLLOW` and `O_NONBLOCK` are always OR-ed in: the first refuses a planted symlink
 * at the final path component, and the second makes an `open` of a FIFO or a device return
 * instead of parking the CLI in the thread pool forever, so the regular-file check below
 * can reject it with `spool_unsafe_path`.
 */
export async function openVerifiedFile(
  path: string,
  flags: number,
  mode?: number,
): Promise<FileHandle> {
  let handle: FileHandle;
  try {
    handle = await open(path, flags | O_NOFOLLOW | O_NONBLOCK, mode);
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    if (code === 'ELOOP') {
      throw unsafePath(`refusing to follow symlinked spool path ${path}`);
    }
    // A FIFO opened for writing with O_NONBLOCK and no reader: a path swapped for a FIFO
    // between the existence check and this open, never a regular spool file.
    if (code === 'ENXIO') {
      throw unsafePath(`refusing to open non-regular spool file ${path}`);
    }
    throw error;
  }
  try {
    const stats = await handle.stat();
    if (!stats.isFile()) {
      throw unsafePath(`refusing to use non-regular spool file ${path}`);
    }
    if (process.platform !== 'win32') {
      const uid = process.getuid?.();
      if (uid !== undefined && stats.uid !== uid) {
        throw unsafePath(`spool file ${path} is owned by uid ${stats.uid}, not ${uid}`);
      }
      if (mode !== undefined) await handle.chmod(mode);
    }
    return handle;
  } catch (error) {
    await handle.close();
    throw error;
  }
}

/** Reads a spool file through an O_NOFOLLOW, ownership-checked descriptor. */
export async function readVerifiedText(path: string): Promise<string> {
  const handle = await openVerifiedFile(path, fsConstants.O_RDONLY | O_NOFOLLOW);
  try {
    return await handle.readFile('utf8');
  } finally {
    await handle.close();
  }
}

async function pathExists(path: string): Promise<boolean> {
  try {
    const stats = await lstat(path);
    return stats.isFile() || stats.isSymbolicLink();
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return false;
    throw error;
  }
}

/** `write()` may write fewer bytes than asked (ENOSPC, EINTR, quota); loop until done. */
export async function writeAll(handle: FileHandle, buffer: Buffer): Promise<void> {
  let written = 0;
  while (written < buffer.byteLength) {
    const { bytesWritten } = await handle.write(buffer, written, buffer.byteLength - written);
    if (bytesWritten <= 0) throw new Error('spool write made no progress');
    written += bytesWritten;
  }
}

/** Directory fsync is a durability nicety; a filesystem that refuses it must not fail the run. */
export function fsyncDirectorySync(directory: string): void {
  if (process.platform === 'win32') return;
  try {
    const fd = openSync(directory, 'r');
    try {
      fsyncSync(fd);
    } finally {
      closeSync(fd);
    }
  } catch {
    // Best effort (some network mounts refuse O_RDONLY fsync on a directory).
  }
}

/**
 * Truncates a trailing fragment that has no `\n` (a crash inside `write(2)`) back to the
 * last newline and fsyncs, so the JSONL stays line-delimited for the reader. Returns the
 * chunk's new size in bytes.
 */
async function truncateTornTail(path: string): Promise<number> {
  const handle = await openVerifiedFile(path, fsConstants.O_RDWR | O_NOFOLLOW);
  try {
    const { size } = await handle.stat();
    if (size === 0) return 0;
    const last = Buffer.allocUnsafe(1);
    const tail = await handle.read(last, 0, 1, size - 1);
    if (tail.bytesRead === 1 && last[0] === 0x0a) return size;
    const newline = await findLastNewlineOffset(handle, size);
    const newSize = newline === null ? 0 : newline + 1;
    await handle.truncate(newSize);
    await handle.sync();
    return newSize;
  } finally {
    await handle.close();
  }
}

/** Byte offset of the last `\n` before `size`, or `null` when the file has none. */
async function findLastNewlineOffset(handle: FileHandle, size: number): Promise<number | null> {
  const block = Buffer.allocUnsafe(readBlockBytes);
  let end = size;
  while (end > 0) {
    const start = Math.max(0, end - readBlockBytes);
    const length = end - start;
    const { bytesRead } = await handle.read(block, 0, length, start);
    for (let i = bytesRead - 1; i >= 0; i -= 1) {
      if (block[i] === 0x0a) return start + i;
    }
    end = start;
  }
  return null;
}

/**
 * Counts complete lines of a chunk, streaming (a torn tail was truncated before this).
 * One line is bounded by {@link maxSpoolLineBytes}, so a chunk carrying a longer one is
 * `spool_corrupt` here too, before the appender can decide to write behind it.
 */
async function countChunkLines(path: string): Promise<number> {
  const chunk = basename(path);
  const handle = await openVerifiedFile(path, fsConstants.O_RDONLY | O_NOFOLLOW);
  try {
    const block = Buffer.allocUnsafe(readBlockBytes);
    let position = 0;
    let count = 0;
    /** Bytes of the current, not yet newline-terminated line seen in earlier blocks. */
    let partial = 0;
    for (;;) {
      const { bytesRead } = await handle.read(block, 0, readBlockBytes, position);
      if (bytesRead === 0) return count;
      position += bytesRead;
      let start = 0;
      for (let i = 0; i < bytesRead; i += 1) {
        if (block[i] !== 0x0a) continue;
        if (partial + (i - start) > maxSpoolLineBytes) {
          throw lineTooLong(chunk, count + 1);
        }
        count += 1;
        partial = 0;
        start = i + 1;
      }
      partial += bytesRead - start;
      if (partial > maxSpoolLineBytes) throw lineTooLong(chunk, count + 1);
    }
  } finally {
    await handle.close();
  }
}

/** `spool_corrupt` for a line that cannot be a legal envelope: it is past every size cap. */
function lineTooLong(chunk: string, lineNumber: number): SpoolCorruptError {
  return new SpoolCorruptError({
    chunk,
    lineNumber,
    reason:
      `line is longer than the ${maxSpoolLineBytes}-byte limit for one envelope ` +
      '(no legal writer produces one, so this is not a torn write)',
  });
}

interface RawLine {
  readonly text: string;
  /** `false` for the final fragment of a file that has no trailing `\n` (a torn write). */
  readonly terminated: boolean;
  readonly lineNumber: number;
}

/**
 * Streams a chunk one line at a time from bounded blocks, so neither the chunk nor the run
 * is ever loaded whole. Bytes of a line straddling a block boundary are kept as a list of
 * pieces (never re-concatenated per block, which made the scan quadratic) and joined once,
 * when the line is complete; a line past {@link maxSpoolLineBytes} is `spool_corrupt`
 * naming chunk + line as soon as the limit is crossed, so a chunk with no newline can no
 * longer grow the heap without bound.
 */
async function* readChunkLines(path: string): AsyncGenerator<RawLine> {
  const chunk = basename(path);
  const handle = await openVerifiedFile(path, fsConstants.O_RDONLY | O_NOFOLLOW);
  try {
    const block = Buffer.allocUnsafe(readBlockBytes);
    let position = 0;
    let lineNumber = 0;
    /** Pieces of the line being assembled across blocks (empty for a line inside one block). */
    let pieces: Buffer[] = [];
    let piecesBytes = 0;
    for (;;) {
      const { bytesRead } = await handle.read(block, 0, readBlockBytes, position);
      if (bytesRead === 0) break;
      position += bytesRead;
      const view = block.subarray(0, bytesRead);
      let start = 0;
      for (;;) {
        const newline = view.indexOf(0x0a, start);
        if (newline === -1) break;
        // Only a line straddling a block boundary is assembled: the common case is a
        // decode straight out of the block, with no intermediate copy.
        let text: string;
        if (piecesBytes === 0) {
          text = view.toString('utf8', start, newline);
        } else {
          const segment = view.subarray(start, newline);
          if (piecesBytes + segment.length > maxSpoolLineBytes) {
            throw lineTooLong(chunk, lineNumber + 1);
          }
          pieces.push(segment);
          text = Buffer.concat(pieces).toString('utf8');
          pieces = [];
          piecesBytes = 0;
        }
        lineNumber += 1;
        yield { text, terminated: true, lineNumber };
        start = newline + 1;
      }
      if (start < view.length) {
        const rest = view.subarray(start);
        if (piecesBytes + rest.length > maxSpoolLineBytes) {
          throw lineTooLong(chunk, lineNumber + 1);
        }
        // Copied: the next block read overwrites the buffer this subarray points into.
        pieces.push(Buffer.from(rest));
        piecesBytes += rest.length;
      }
    }
    if (piecesBytes > 0) {
      const only = pieces[0];
      const text =
        pieces.length === 1 && only !== undefined
          ? only.toString('utf8')
          : Buffer.concat(pieces).toString('utf8');
      lineNumber += 1;
      yield { text, terminated: false, lineNumber };
    }
  } finally {
    await handle.close();
  }
}

/**
 * The sequence of a chunk's last newline-terminated line, read from the file tail only, or
 * `null` when that line is missing/unparseable — or longer than {@link maxTailLineBytes},
 * which keeps this fast path cheap; the caller then falls back to the full streaming scan,
 * whose bound is {@link maxSpoolLineBytes}.
 */
async function lastTerminatedSequence(path: string): Promise<number | null> {
  const handle = await openVerifiedFile(path, fsConstants.O_RDONLY | O_NOFOLLOW);
  try {
    const { size } = await handle.stat();
    if (size === 0) return null;
    const newline = await findLastNewlineOffset(handle, size);
    if (newline === null) return null;
    const lineEnd = newline;
    const previous = lineEnd === 0 ? null : await findLastNewlineOffset(handle, lineEnd);
    const start = previous === null ? 0 : previous + 1;
    const length = lineEnd - start;
    if (length <= 0 || length > maxTailLineBytes) return null;
    const buffer = Buffer.allocUnsafe(length);
    const { bytesRead } = await handle.read(buffer, 0, length, start);
    if (bytesRead !== length) return null;
    const parsed = tryParseLine(buffer.toString('utf8'));
    return parsed === null ? null : parsed.sequence;
  } finally {
    await handle.close();
  }
}

/** The last parseable sequence in a chunk, streaming; `null` when it has none. */
async function scanLastSequence(path: string): Promise<number | null> {
  let last: number | null = null;
  for await (const raw of readChunkLines(path)) {
    if (!raw.terminated && raw.text.length === 0) continue;
    const parsed = tryParseLine(raw.text);
    if (parsed !== null) last = parsed.sequence;
  }
  return last;
}

interface ParsedSpoolLine {
  readonly sequence: number;
  readonly eventId: string;
}

function tryParseLine(text: string): ParsedSpoolLine | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return null;
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return null;
  const record: object = parsed;
  const sequence: unknown = 'sequence' in record ? record.sequence : undefined;
  const eventId: unknown = 'event_id' in record ? record.event_id : undefined;
  if (typeof sequence !== 'number' || !Number.isSafeInteger(sequence) || sequence < 0) return null;
  if (typeof eventId !== 'string' || eventId.length === 0) return null;
  return { sequence, eventId };
}

/** Parses a line the reader must trust, or throws `spool_corrupt` (D3). */
function parseSpoolLine(text: string, chunk: string, lineNumber: number): ParsedSpoolLine {
  const parsed = tryParseLine(text);
  if (parsed === null) {
    throw new SpoolCorruptError({
      chunk,
      lineNumber,
      reason: 'not a JSON envelope with an integer "sequence" and a string "event_id"',
    });
  }
  return parsed;
}

interface AppendLine {
  readonly text: string;
  readonly lineBytes: number;
  readonly sequence: number;
}

/** Validates one line handed to `append`: one JSON envelope, no line break, a sequence. */
function parseAppendLine(line: string, index: number): AppendLine {
  const position = index + 1;
  if (typeof line !== 'string' || line.length === 0) {
    throw invalidLine(position, 'is empty');
  }
  if (line.includes('\n') || line.includes('\r')) {
    throw invalidLine(position, 'contains a line break');
  }
  const parsed = tryParseLine(line);
  if (parsed === null) {
    throw invalidLine(
      position,
      'is not a JSON envelope with an integer "sequence" and a string "event_id"',
    );
  }
  return {
    text: line,
    lineBytes: Buffer.byteLength(line, 'utf8') + 1,
    sequence: parsed.sequence,
  };
}

function invalidLine(position: number, reason: string): CliError {
  return new CliError(`spool line ${position} ${reason}`, {
    code: 'invalid_spool_line',
    hint: 'the spool accepts one JSON event envelope per line, without a trailing newline',
  });
}
