// The append-only writer itself: buffering, rotation, the cap cache, `close` and the lock's
// lifetime. The on-disk layout lives in `spoolConfig`, the hardened primitives in `spoolFs`, the
// chunk scan in `spoolScan` and the record files in `spoolRecord`, so this file decides only when
// each of them is called — which is what the durability guarantees depend on.
import { closeSync, fsyncSync, ftruncateSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

import type { EventEnvelope, UnsignedEnvelope } from './envelope.js';
import { acquireWriterLock, releaseWriterLock, type HeldWriterLock } from './writerLock.js';
import {
  DEFAULT_CHUNK_MAX_BYTES,
  DEFAULT_CHUNK_MAX_LINES,
  DEFAULT_FLUSH_THRESHOLD,
  SPOOL_DIR_MODE,
} from './spoolConfig.js';
import { EvidgateShardRecordMismatchError, EvidgateSpoolCapExceededError } from './spoolErrors.js';
import {
  assertWithinSpoolDir,
  chunkPath,
  directoryBytes,
  existingRecordBytes,
  hardenDirectory,
  readChunkIfExists,
  writeAll,
} from './spoolFs.js';
import { trackLock, untrackLock } from './spoolLock.js';
import {
  readCheckpoint,
  readShardsRecord,
  writeShardsRecordFile,
  type StoredShards,
} from './spoolRecord.js';
import {
  highestChunkIndex,
  lastSequenceInChunks,
  openChunkFile,
  truncateTornTail,
} from './spoolScan.js';
import type {
  SpoolCheckpoint,
  SpoolNotice,
  SpoolNoticeCode,
  SpoolWriterOptions,
} from './spoolTypes.js';
import {
  assertSpoolMaxBytes,
  assertValidShardOption,
  assertValidSpoolId,
  reconcileSequenceNext,
  resolveSpoolMaxBytes,
} from './spoolValidation.js';

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
  private durableNextSequence = 1;
  /**
   * The floor this producer directory already carried when the writer opened:
   * `max(durableNextSequence, stored sequence_next | next_sequence at open)`. The record is
   * never written below it, so a floor that arrived on disk is not thrown away by the first
   * rewrite, while nothing this writer only buffered can push the record ahead of the chunks.
   */
  private recordFloor = 1;
  private nextSequence = 1;
  /** The last `shards.json` write failure: the record is advisory, so the failure is kept. */
  private shardsFailure: Error | undefined;
  /** Queued {@link SpoolNotice}s not yet taken; each code is queued at most once per writer. */
  private notices: SpoolNotice[] = [];
  private readonly noticedCodes = new Set<SpoolNoticeCode>();
  private chunkIndex = 0;
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
    hardenDirectory(join(options.spoolDir, options.runId));
    hardenDirectory(this.directory);
    // The record is read before the lock so a mismatch is a clean refusal with nothing to undo;
    // the chunk scan in `recoverFromDisk` stays authoritative for the sequence.
    const record = readShardsRecord(this.directory, this.producerId, this.shardIndex);
    const legacy = record === null ? readCheckpoint(this.directory, this.producerId) : null;
    // One live writer per producer directory. A second live process fails with
    // `spool_writer_locked` naming the holder; a dead one is taken over.
    this.lock = acquireWriterLock(this.directory, options.lock ?? {});
    trackLock(this.lock);
    try {
      this.recoverFromDisk(record, legacy);
    } catch (error) {
      // A failed construction must not leave a lock naming a live pid with no writer.
      this.releaseLock();
      throw error;
    }
  }

  /**
   * The durable state of the producer directory: the byte caches, the chunk scan, the reconciled
   * sequence and the restart record. Runs under the lock, so a failure here can release it.
   */
  private recoverFromDisk(record: StoredShards | null, legacy: SpoolCheckpoint | null): void {
    this.runBytes = directoryBytes(join(this.spoolDir, this.runId));
    this.recordBytes = existingRecordBytes(this.directory);
    this.chunkIndex = highestChunkIndex(this.directory);
    // Read the highest chunk once and share the buffer with the torn-tail repair, the
    // sequence scan and the open, instead of reading it three times.
    const highestChunk =
      this.chunkIndex < 1 ? null : readChunkIfExists(chunkPath(this.directory, this.chunkIndex));
    const repairedChunk = truncateTornTail(
      highestChunk,
      chunkPath(this.directory, this.chunkIndex),
    );
    const lastSequence = lastSequenceInChunks(this.directory, this.chunkIndex, repairedChunk);
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
      writeAll(this.fd, buffer);
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

  private fsyncChunk(): void {
    if (this.fd < 0) throw new Error('spool chunk is not open');
    fsyncSync(this.fd);
  }

  /**
   * Writes `shards.json` through the atomic record writer and keeps the cap cache honest about our
   * own metadata: the rename replaced the previous record, so only the size delta is new bytes.
   */
  private writeShardsRecord(): void {
    const bodyBytes = writeShardsRecordFile({
      directory: this.directory,
      producerId: this.producerId,
      shardIndex: this.shardIndex,
      shardTotal: this.shardTotal,
      durableNextSequence: this.durableNextSequence,
      recordFloor: this.recordFloor,
      restartAt: this.restartAt,
    });
    this.recordWrites += 1;
    this.shardsFailure = undefined;
    this.runBytes += Math.max(0, bodyBytes - this.recordBytes);
    this.recordBytes = bodyBytes;
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
    this.recordBytes = existingRecordBytes(this.directory);
  }

  private openChunk(content: Buffer | null): void {
    const opened = openChunkFile(this.directory, this.chunkIndex, content);
    this.chunkIndex = opened.chunkIndex;
    this.chunkBytes = opened.chunkBytes;
    this.chunkLines = opened.chunkLines;
    this.fd = opened.fd;
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
}
