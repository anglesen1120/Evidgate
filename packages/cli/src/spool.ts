// The CLI's read/append side of the on-disk spool. Appends and acks are fsynced before
// success is reported, so a crash can repeat an ack but never lose one; reads stream
// bounded lines, and the byte cap is checked before anything is written. The chunk
// plumbing, the append path and the cursor live in `spoolChunks`, `spoolAppend` and
// `spoolCursor`; this module is the one a caller opens and drives.
import { rm } from 'node:fs/promises';
import { join } from 'node:path';

import { CliError } from './errors.js';
import { appendLines } from './spoolAppend.js';
import { ChunkStore } from './spoolChunks.js';
import { readCursor, writeCursor } from './spoolCursor.js';
import { SpoolCorruptError, SpoolMissingError, invalidAck } from './spoolErrors.js';
import { assertRealOwnedDirectory, hardenDirectory, mkdirOwned } from './spoolFs.js';
import { assertValidSpoolId, assertWithinSpoolDir } from './spoolIds.js';
import { lastTerminatedSequence, parseSpoolLine, readChunkLines } from './spoolLines.js';
import { assertNoLiveWriter, withSpoolLock } from './spoolLock.js';

import type { ProducerLockOptions } from './spoolLock.js';
import type { OpenProducerSpoolOptions, SpoolCursor, SpoolEvent } from './spoolTypes.js';

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

  private readonly chunks: ChunkStore;

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
    this.chunks = new ChunkStore(this.directory);
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
    // A missing run or producer directory is `spool_missing`, never a raw errno.
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
   * `options.requireNoLiveWriter` adds the live-writer guard: the reporter's `writer.lock` is
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

  /** Appends complete envelope lines, all-or-nothing per call. */
  async append(lines: readonly string[]): Promise<void> {
    await appendLines(
      {
        spoolDir: this.spoolDir,
        runId: this.runId,
        producerId: this.producerId,
        maxBytes: this.maxBytes,
        directory: this.directory,
        chunks: this.chunks,
      },
      lines,
    );
  }

  /**
   * Streams the events from `cursor.nextUnackedSequence` onward, in sequence order. A
   * final line without `\n` is a torn write and is skipped; every other bad line, a
   * gap and a regression are `SpoolCorruptError` naming chunk + line.
   */
  async *readUnacked(): AsyncGenerator<SpoolEvent> {
    const cursor = await this.cursor();
    const chunks = await this.chunks.listChunks();
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
    return await readCursor(this.directory, this.producerId);
  }

  /**
   * Advances the ack cursor to `upToSequence`, adding `addedCount` acked events. Only a
   * contiguous prefix can be acked: going backwards, or past the last sequence on disk,
   * is refused. Every chunk the cursor claims — and the directory — is durable before the
   * cursor is replaced (see {@link ChunkStore.fsyncChunks} for why that is O(1), not
   * O(chunks)).
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
    const lastOnDisk = await this.chunks.lastOnDiskSequence();
    if (upToSequence > lastOnDisk) {
      throw invalidAck(
        `sequence ${upToSequence} is ahead of the last spooled sequence ${lastOnDisk}`,
      );
    }
    if (upToSequence === cursor.lastAckedSequence && addedCount === 0) return;
    await this.chunks.fsyncChunks();
    await writeCursor(this.directory, {
      producerId: cursor.producerId,
      nextUnackedSequence: upToSequence + 1,
      lastAckedSequence: upToSequence,
      ackedCount: cursor.ackedCount + addedCount,
    });
  }

  /** `first..last` of the events still to send, or `null` when everything is acked. */
  async unsentRange(): Promise<{ first: number; last: number } | null> {
    const cursor = await this.cursor();
    const last = await this.chunks.lastOnDiskSequence();
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
    this.chunks.reset();
  }
}

export {
  O_NOFOLLOW,
  O_NONBLOCK,
  defaultSpoolMaxBytes,
  maxSpoolLineBytes,
  spoolChunkDigits,
  spoolChunkMaxBytes,
  spoolChunkMaxLines,
  spoolCursorFileName,
  spoolCursorTmpFileName,
  spoolDirMode,
  spoolFileMode,
  spoolIdMaxLength,
} from './spoolLimits.js';
export {
  InvalidSpoolIdError,
  SpoolCapExceededError,
  SpoolCorruptError,
  SpoolMissingError,
} from './spoolErrors.js';
export { assertValidSpoolId, listProducers, runDirBytes } from './spoolIds.js';
export {
  assertRealOwnedDirectory,
  fsyncDirectorySync,
  hardenDirectory,
  mkdirOwned,
  openVerifiedFile,
  readVerifiedText,
  writeAll,
} from './spoolFs.js';
export type { OpenProducerSpoolOptions, SpoolCursor, SpoolEvent } from './spoolTypes.js';
