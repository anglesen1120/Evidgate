// 04.3.1-T1.1: the spool writer (decision 4 / task file §4). Layout is the task file's
// literal contract:
//
//   <spool_dir>/<run_id>/<producer_id>/NNNNNN.jsonl   (6-digit zero-padded chunks)
//   <spool_dir>/<run_id>/<producer_id>/checkpoint.json { producer_id, next_sequence }
//
// 04.2.3's `<org_hash>/…/journal.seq` + `cursor.json` text is a recorded gap, not
// followed (STATUS.md 04.3.1 row).
//
// Durability: envelopes are appended per callback (O(1), no whole-suite buffering) into
// a small pending batch; a flush writes the batch, fsyncs the chunk, then rewrites the
// checkpoint atomically (tmp + fsync + rename + directory fsync). A crash loses at most
// `flushThreshold - 1` envelopes. On restart the writer reconciles `next_sequence`
// against the highest sequence already on disk — the last COMPLETE parseable line of the
// highest chunk, scanning backwards, then lower chunks — so a sequence is never reused
// (T84). A torn tail (a crash mid-`writeSync` leaves a partial line with no trailing
// newline) is truncated back to the last newline and fsynced before the first append, so
// the JSONL stays line-delimited for the reader.
//
// Rotation: a chunk rotates when it reaches `chunkMaxBytes` (1 MiB) or `chunkMaxLines`
// (1000), whichever comes first.
import {
  chmodSync,
  closeSync,
  constants as fsConstants,
  existsSync,
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
  unlinkSync,
  writeSync,
} from 'node:fs';
import { join, resolve, sep } from 'node:path';

import type { EventEnvelope, UnsignedEnvelope } from './envelope.js';

export const CHUNK_FILE_DIGITS = 6;
export const DEFAULT_FLUSH_THRESHOLD = 64;
export const DEFAULT_CHUNK_MAX_BYTES = 1024 * 1024;
export const DEFAULT_CHUNK_MAX_LINES = 1000;
export const SPOOL_DIR_MODE = 0o700;
export const SPOOL_FILE_MODE = 0o600;
export const CHECKPOINT_FILE = 'checkpoint.json';
export const CHECKPOINT_TMP_FILE = 'checkpoint.json.tmp';

const CHUNK_FILE_PATTERN = /^(\d{6})\.jsonl$/;

/**
 * `O_NOFOLLOW` is undefined on Windows; degrade to a plain open there, where the POSIX
 * symlink/ownership hardening does not apply (S3).
 */
const O_NOFOLLOW: number = process.platform === 'win32' ? 0 : fsConstants.O_NOFOLLOW;

/** The only characters allowed in a `run_id`/`producer_id` path segment (S3, F8). */
const SPOOL_ID_PATTERN = /^[A-Za-z0-9._-]+$/;

/** Envelope `producer_id` `maxLength` (contracts/schemas/event-envelope.v1.json). */
export const SPOOL_ID_MAX_LENGTH = 128;

/**
 * Thrown at construction when `run_id` or `producer_id` is not a safe single path
 * segment. Both are joined into the spool path verbatim, so an id containing `..`, a
 * path separator, a NUL or a control character could escape `spool_dir` (S3).
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
 * Defence in depth for S3: even after id validation, the resolved writer directory must
 * stay inside the resolved spool root.
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

export interface SpoolWriterOptions {
  spoolDir: string;
  runId: string;
  producerId: string;
  /** Envelopes buffered before an automatic flush. Default {@link DEFAULT_FLUSH_THRESHOLD}. */
  flushThreshold?: number;
  chunkMaxBytes?: number;
  chunkMaxLines?: number;
}

/** The checkpoint file's shape (task file §4). */
export interface SpoolCheckpoint {
  producer_id: string;
  next_sequence: number;
}

/**
 * Append-only spool writer for one `(run_id, producer_id)` pair. It owns the
 * per-producer sequence counter: `append` assigns the next contiguous sequence.
 */
export class SpoolWriter {
  readonly directory: string;
  private readonly producerId: string;
  private readonly flushThreshold: number;
  private readonly chunkMaxBytes: number;
  private readonly chunkMaxLines: number;
  private nextSequence: number;
  private chunkIndex: number;
  private chunkBytes = 0;
  private chunkLines = 0;
  private fd = -1;
  private pending: EventEnvelope[] = [];
  private closed = false;

  constructor(options: SpoolWriterOptions) {
    // D2: `run_id`/`producer_id` are joined into the spool path verbatim, so reject any
    // value that could escape `spool_dir` before the first filesystem write.
    assertValidSpoolId('run_id', options.runId);
    assertValidSpoolId('producer_id', options.producerId);
    this.directory = join(options.spoolDir, options.runId, options.producerId);
    assertWithinSpoolDir(options.spoolDir, this.directory);
    this.producerId = options.producerId;
    this.flushThreshold = options.flushThreshold ?? DEFAULT_FLUSH_THRESHOLD;
    this.chunkMaxBytes = options.chunkMaxBytes ?? DEFAULT_CHUNK_MAX_BYTES;
    this.chunkMaxLines = options.chunkMaxLines ?? DEFAULT_CHUNK_MAX_LINES;
    mkdirSync(this.directory, { recursive: true, mode: SPOOL_DIR_MODE });
    // S3: the run and producer directories are ours to create or reuse; reject a planted
    // symlink or a foreign-owned directory and force 0700 on an owned one. `spool_dir`
    // itself is left as given (a symlinked spool root is allowed, and a pre-existing
    // spool root keeps its mode).
    this.hardenDirectory(join(options.spoolDir, options.runId));
    this.hardenDirectory(this.directory);
    const checkpoint = this.readCheckpoint();
    this.chunkIndex = this.highestChunkIndex();
    // F9: read the highest chunk once and share the buffer with the torn-tail repair,
    // the sequence scan and the open, instead of reading it three times.
    const highestChunk =
      this.chunkIndex < 1 ? null : this.readChunkIfExists(this.chunkPath(this.chunkIndex));
    const repairedChunk = this.truncateTornTail(highestChunk);
    const lastSequence = this.lastSequenceInChunks(repairedChunk);
    this.nextSequence = Math.max(checkpoint?.next_sequence ?? 1, lastSequence + 1);
    this.openChunk(repairedChunk);
  }

  /** The sequence the next appended envelope will receive. */
  get nextSequenceNumber(): number {
    return this.nextSequence;
  }

  /** Number of envelopes buffered but not yet flushed (test seam for the O(1) buffer bound). */
  get pendingCount(): number {
    return this.pending.length;
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

  /** Writes the pending batch, fsyncs the chunk, and rewrites the checkpoint atomically. */
  flush(): void {
    if (this.pending.length === 0) return;
    const batch = this.pending;
    this.pending = [];
    let written = 0;
    try {
      for (const envelope of batch) {
        if (this.chunkLines >= this.chunkMaxLines || this.chunkBytes >= this.chunkMaxBytes) {
          this.rotate();
        }
        this.writeLine(`${JSON.stringify(envelope)}\n`);
        written += 1;
      }
      this.fsyncChunk();
      this.writeCheckpoint();
    } catch (error) {
      // CR4/PS4: an envelope that never reached the chunk stays queued so a later flush
      // can retry it instead of silently dropping evidence; the error still propagates.
      // Envelopes already written (and fsynced) are not re-queued, so a retry cannot
      // duplicate a durable line.
      if (written < batch.length) {
        this.pending = batch.slice(written).concat(this.pending);
      }
      throw error;
    }
  }

  /** Flushes and closes the current chunk. Idempotent. */
  close(): void {
    if (this.closed) return;
    // CR4: release the descriptor and mark the writer closed even when the final flush
    // throws — the error still propagates, but the fd does not outlive the failure.
    try {
      this.flush();
    } finally {
      if (this.fd >= 0) {
        closeSync(this.fd);
        this.fd = -1;
      }
      this.closed = true;
    }
  }

  private writeLine(line: string): void {
    if (this.fd < 0) throw new Error('spool chunk is not open');
    const buffer = Buffer.from(line, 'utf8');
    const startOffset = this.chunkBytes;
    try {
      this.writeAll(this.fd, buffer);
    } catch (error) {
      // PS4: roll the chunk back to the last complete line so the JSONL stays parseable
      // and the reader never sees a fragment; the original error still propagates.
      try {
        ftruncateSync(this.fd, startOffset);
      } catch {
        // Best effort: the write error is the one that matters.
      }
      throw error;
    }
    this.chunkBytes += buffer.byteLength;
    this.chunkLines += 1;
  }

  /**
   * PS4: `writeSync` may write fewer bytes than asked (ENOSPC, EINTR, quota); loop until
   * the whole buffer is written so a short write cannot leave a torn line.
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

  private writeCheckpoint(): void {
    const checkpoint: SpoolCheckpoint = {
      producer_id: this.producerId,
      next_sequence: this.nextSequence,
    };
    const tmpPath = join(this.directory, CHECKPOINT_TMP_FILE);
    const targetPath = join(this.directory, CHECKPOINT_FILE);
    // PS3: clear a leftover tmp (a crash between write and rename) and re-create it with
    // O_EXCL|O_NOFOLLOW, so a planted `checkpoint.json.tmp -> victim` symlink is never
    // truncated or followed.
    try {
      unlinkSync(tmpPath);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
    }
    const fd = this.openVerified(
      tmpPath,
      fsConstants.O_WRONLY | fsConstants.O_CREAT | fsConstants.O_EXCL | O_NOFOLLOW,
      SPOOL_FILE_MODE,
    );
    try {
      this.writeAll(fd, Buffer.from(`${JSON.stringify(checkpoint)}\n`, 'utf8'));
      fsyncSync(fd);
    } finally {
      closeSync(fd);
    }
    renameSync(tmpPath, targetPath);
    this.fsyncDirectory();
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
      // CR4/PS4: a failed rotation must not leave the writer unable to flush. Restore the
      // previous chunk's index and counters and reopen it, so the envelopes re-queued by
      // `flush` can be written on a later call once the obstacle is gone (the README
      // promise). The rotation error still propagates.
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
    // PS3: O_NOFOLLOW so a planted chunk symlink is refused; openVerified then rejects a
    // non-regular or foreign-owned file and forces 0600 on the descriptor.
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
   * A crash mid-`writeSync` can leave a partial line with no trailing newline at the end
   * of the highest chunk. Appending after it would concatenate the new JSON onto the
   * fragment and corrupt the JSONL for the reader, so truncate the fragment back to the
   * last newline and fsync before the first append. Returns the repaired content so the
   * caller can share it with the sequence scan and the chunk open (F9).
   */
  private truncateTornTail(content: Buffer | null): Buffer | null {
    if (content === null) return null;
    if (content.byteLength === 0 || content[content.byteLength - 1] === 0x0a) return content;
    const lastNewline = content.lastIndexOf(0x0a);
    const path = this.chunkPath(this.chunkIndex);
    // PS3: O_NOFOLLOW so a planted chunk symlink is refused.
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
   * The highest sequence already durable on disk. Scans chunks from the highest index
   * down, and within each chunk from the last line backwards, so a torn or corrupt final
   * line falls back to the previous COMPLETE parseable line instead of dropping the whole
   * chunk (D1: otherwise the counter restarts at 1 and reuses durable sequences). The
   * highest chunk's already-read (and repaired) buffer is reused instead of re-read (F9).
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
          if (typeof parsed.sequence === 'number') return parsed.sequence;
        } catch {
          // A torn or corrupt line (crash mid-write) is skipped; keep scanning backwards.
        }
      }
    }
    return 0;
  }

  private readCheckpoint(): SpoolCheckpoint | null {
    const path = join(this.directory, CHECKPOINT_FILE);
    if (!existsSync(path)) return null;
    try {
      const parsed = JSON.parse(readFileSync(path, 'utf8')) as Partial<SpoolCheckpoint>;
      if (
        typeof parsed.next_sequence === 'number' &&
        // PS8(b): a tampered/corrupt checkpoint must not jump the sequence to an
        // imprecise value (1e300, 2**53), so require a safe integer.
        Number.isSafeInteger(parsed.next_sequence) &&
        parsed.next_sequence >= 1
      ) {
        return {
          producer_id:
            typeof parsed.producer_id === 'string' ? parsed.producer_id : this.producerId,
          next_sequence: parsed.next_sequence,
        };
      }
    } catch {
      // A truncated checkpoint (crash between write and rename) is ignored; the chunk
      // scan recovers the sequence.
    }
    return null;
  }

  /**
   * PS3: opens a spool file with the given flags and verifies the descriptor: it must be
   * a regular file owned by the current process, and (when a mode is given) is forced to
   * that mode. A non-regular file (a symlink target, FIFO or device) or a foreign-owned
   * file is rejected. On Windows the ownership/mode checks are skipped.
   */
  private openVerified(path: string, flags: number, mode?: number): number {
    const fd = openSync(path, flags, mode);
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
   * PS3: a directory the writer creates or reuses under `spool_dir` must be a real
   * directory owned by the current process; a symlink or a foreign-owned directory is
   * rejected, and an owned one is forced to 0700. `spool_dir` itself is never passed here
   * (a symlinked spool root is allowed and keeps its mode).
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

  /** Reads a chunk through an O_NOFOLLOW, ownership-checked descriptor (PS3). */
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
