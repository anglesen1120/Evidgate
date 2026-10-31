// The append path: all-or-nothing per call (the cap is checked before the first write),
// lines written in order, the chunk rotated at 1000 lines / 1 MiB, and the chunk plus the
// directory fsynced before the call returns. A failed append rolls its own chunk back to
// where the call started, so the JSONL never keeps a partial line.
import { CliError } from './errors.js';
import { readCursor } from './spoolCursor.js';
import { SpoolCapExceededError } from './spoolErrors.js';
import { fsyncDirectorySync, writeAll } from './spoolFs.js';
import { runDirBytes } from './spoolIds.js';
import { spoolChunkMaxBytes, spoolChunkMaxLines } from './spoolLimits.js';
import { parseAppendLine, type AppendLine } from './spoolLines.js';

import type { FileHandle } from 'node:fs/promises';
import type { ChunkState, ChunkStore } from './spoolChunks.js';

/** What one append needs: the scope it caps against and the chunk sequence it writes into. */
export interface AppendTarget {
  readonly spoolDir: string;
  readonly runId: string;
  readonly producerId: string;
  /** The run-directory cap in bytes (`EVIDGATE_SPOOL_MAX_BYTES`). */
  readonly maxBytes: number;
  readonly directory: string;
  readonly chunks: ChunkStore;
}

/**
 * Appends complete envelope lines, all-or-nothing per call: the cap is checked before
 * the first write, so a `SpoolCapExceededError` leaves the spool byte-identical. Lines
 * are written in order, the chunk rotated at 1000 lines / 1 MiB, and the chunk plus the
 * directory fsynced before returning. A torn tail in the highest chunk is truncated
 * back to the last newline before the first write.
 */
export async function appendLines(target: AppendTarget, lines: readonly string[]): Promise<void> {
  if (lines.length === 0) return;
  const parsed = parseAppendBatch(lines);
  const requiredBytes = parsed.reduce((sum, item) => sum + item.lineBytes, 0);
  const cursor = await readCursor(target.directory, target.producerId);
  const usedBytes = await runDirBytes(target.spoolDir, target.runId);
  const lastAppended = parsed[parsed.length - 1];
  const unsentLastSequence = Math.max(
    lastAppended?.sequence ?? cursor.nextUnackedSequence,
    cursor.nextUnackedSequence,
  );
  if (usedBytes + requiredBytes > target.maxBytes) {
    // Checks only so far: no chunk was repaired, created or written, so the spool is
    // byte-identical when this throws ("nothing written").
    throw new SpoolCapExceededError({
      runId: target.runId,
      producerId: target.producerId,
      capBytes: target.maxBytes,
      usedBytes,
      requiredBytes,
      unsentFirstSequence: cursor.nextUnackedSequence,
      unsentLastSequence,
    });
  }
  const start = await target.chunks.chunkState();
  const written = await writeBatch(target, parsed, start);
  fsyncDirectorySync(target.directory);
  target.chunks.remember(written);
}

/** Parses one call's lines and refuses a batch that is not in ascending sequence order. */
function parseAppendBatch(lines: readonly string[]): readonly AppendLine[] {
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
  return parsed;
}

/** Writes the batch, or rolls the chunk it was writing back to `start`. */
async function writeBatch(
  target: AppendTarget,
  parsed: readonly AppendLine[],
  start: ChunkState,
): Promise<ChunkState> {
  const writer = new ChunkWriter(target, start);
  try {
    for (const item of parsed) {
      await writer.rotateIfFull();
      await writer.push(item);
    }
    await writer.finish();
  } catch (error) {
    // Roll the chunk back to where this call started, so a failed append never leaves a
    // partial line (the JSONL stays parseable). Chunks rotated earlier in the call stay
    // durable and valid; the sequence they hold is still contiguous from the cursor.
    await writer.rollback();
    throw error;
  }
  return writer.result();
}

/**
 * One call's view of a chunk: the segment of lines for the current chunk is concatenated
 * into one buffer and written with a single `write`, so a 1000-line chunk costs one syscall
 * instead of 1000. The segment is at most one chunk (plus the single line that rotates it),
 * which keeps the buffer bounded while leaving the rollback, cap and rotation rules
 * untouched.
 */
class ChunkWriter {
  private readonly startIndex: number;
  private readonly startBytes: number;
  private index: number;
  private bytes: number;
  private lines: number;
  private handle: FileHandle | null = null;
  /** Envelope texts of the current chunk segment, joined and written once on flush. */
  private segment: string[] = [];

  constructor(
    private readonly target: AppendTarget,
    start: ChunkState,
  ) {
    this.index = start.index;
    this.startIndex = start.index;
    this.startBytes = start.bytes;
    this.bytes = start.bytes;
    this.lines = start.lines;
  }

  /** Closes the full chunk and moves to the next one, before the line is written. */
  async rotateIfFull(): Promise<void> {
    if (this.bytes >= spoolChunkMaxBytes || this.lines >= spoolChunkMaxLines) {
      await this.flush();
      if (this.handle !== null) {
        await this.handle.sync();
        await this.handle.close();
        this.handle = null;
      }
      this.index += 1;
      this.bytes = 0;
      this.lines = 0;
    }
  }

  async push(item: AppendLine): Promise<void> {
    if (this.handle === null) this.handle = await this.target.chunks.openChunkForAppend(this.index);
    this.segment.push(item.text);
    this.bytes += item.lineBytes;
    this.lines += 1;
  }

  /** Flushes the last segment and fsyncs the chunk it went into. */
  async finish(): Promise<void> {
    await this.flush();
    if (this.handle !== null) {
      await this.handle.sync();
      await this.handle.close();
      this.handle = null;
    }
  }

  /** Best-effort truncate back to the call's starting offset, then close. */
  async rollback(): Promise<void> {
    if (this.handle === null) return;
    const rollbackTarget = this.index === this.startIndex ? this.startBytes : 0;
    try {
      await this.handle.truncate(rollbackTarget);
    } catch {
      // Best effort: the write error is the one that matters.
    }
    try {
      await this.handle.close();
    } catch {
      // Already closed by the failed write.
    }
  }

  result(): ChunkState {
    return { index: this.index, bytes: this.bytes, lines: this.lines };
  }

  private async flush(): Promise<void> {
    if (this.segment.length === 0 || this.handle === null) return;
    const buffer = Buffer.from(`${this.segment.join('\n')}\n`, 'utf8');
    this.segment = [];
    await writeAll(this.handle, buffer);
  }
}
