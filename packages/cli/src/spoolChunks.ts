// The chunk files of one producer directory: which chunk the next line goes into, the
// cached rotation state, and the durability sweep an ack needs. Rotation always moves to
// `highest + 1`, never back to the lowest ack boundary, so every lower chunk is immutable.
import { constants as fsConstants } from 'node:fs';
import { readdir } from 'node:fs/promises';
import { join } from 'node:path';

import { fsyncDirectorySync, openVerifiedFile, pathExists } from './spoolFs.js';
import { O_NOFOLLOW, spoolChunkDigits, spoolFileMode } from './spoolLimits.js';
import {
  countChunkLines,
  lastTerminatedSequence,
  scanLastSequence,
  truncateTornTail,
} from './spoolLines.js';

import type { FileHandle } from 'node:fs/promises';
import type { ChunkFile } from './spoolTypes.js';

const chunkFilePattern = /^(\d{6})\.jsonl$/;

/** Where the appender is in the producer's chunk sequence. */
export interface ChunkState {
  readonly index: number;
  readonly bytes: number;
  readonly lines: number;
}

/** The chunk sequence of one producer directory, with the state the appender caches. */
export class ChunkStore {
  private chunkIndex: number | undefined;
  private chunkBytes: number | undefined;
  private chunkLines: number | undefined;
  /**
   * Chunks this process has already fsynced and that are now immutable (the writer only
   * ever appends to the highest chunk), so `ack` never has to sync them again.
   */
  private readonly syncedChunks = new Set<string>();

  constructor(private readonly directory: string) {}

  async listChunks(): Promise<readonly ChunkFile[]> {
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
   * tail it may end with), or `000001.jsonl` when the producer has none yet.
   */
  async chunkState(): Promise<ChunkState> {
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
    this.remember({ index, bytes, lines });
    return { index, bytes, lines };
  }

  /** Caches what a finished append left behind, so the next call does not re-read the chunk. */
  remember(state: ChunkState): void {
    this.chunkIndex = state.index;
    this.chunkBytes = state.bytes;
    this.chunkLines = state.lines;
  }

  /** Forgets the cache after the directory was removed. */
  reset(): void {
    this.chunkIndex = undefined;
    this.chunkBytes = undefined;
    this.chunkLines = undefined;
    this.syncedChunks.clear();
  }

  async openChunkForAppend(index: number): Promise<FileHandle> {
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
   * Read fresh on every call: the reporter keeps appending without the lock, so a
   * cached high-water mark would make `ack` reject events that are already on disk.
   */
  async lastOnDiskSequence(): Promise<number> {
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
   * highest chunk (rotation always moves to `highest + 1`), so every lower chunk is
   * immutable and was already fsynced — by `append` when this process rotated it, or by
   * this method once, after which its name is remembered in {@link syncedChunks}. Only the
   * highest chunk can have grown since the last ack (a writer appending without the lock),
   * so it is synced on every call. That keeps a send at O(chunks) syncs in total
   * instead of the O(batches × chunks) the previous every-chunk sweep cost.
   *
   * Restart safety rests on absence, never on presence: {@link syncedChunks} is empty in a
   * fresh process and a name enters it only after this process opened and fsynced that
   * chunk, so "not in the set" always means "not yet made durable by this process" and the
   * first ack of a new process syncs every chunk on disk. Nothing here assumes a chunk was
   * durable because `append` wrote it: the reporter appends without the CLI lock, so
   * a chunk below the highest is trusted only after this process synced it itself.
   */
  async fsyncChunks(): Promise<void> {
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

  private chunkPath(index: number): string {
    return join(this.directory, `${String(index).padStart(spoolChunkDigits, '0')}.jsonl`);
  }
}
