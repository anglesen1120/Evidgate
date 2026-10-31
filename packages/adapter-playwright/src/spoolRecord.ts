// The sequence record files: `shards.json` (read and written atomically) and the legacy
// `checkpoint.json` (read only). The record is advisory — the chunk scan stays authoritative — so
// every reader here answers `null` for a torn or unusable file instead of failing the writer.
import { closeSync, constants as fsConstants, fsyncSync, renameSync, unlinkSync } from 'node:fs';
import { join } from 'node:path';

import {
  CHECKPOINT_FILE,
  CHECKPOINT_TMP_FILE,
  SHARDS_FILE,
  SHARDS_TMP_FILE,
  SPOOL_FILE_MODE,
} from './spoolConfig.js';
import { EvidgateShardRecordMismatchError } from './spoolErrors.js';
import {
  O_NOFOLLOW,
  assertWritableTargetIsRegularFile,
  fsyncDirectory,
  openVerified,
  readRecordFile,
  writeAll,
} from './spoolFs.js';
import type { ShardsRecord, SpoolCheckpoint } from './spoolTypes.js';

/**
 * What a readable `shards.json` yielded: the identity is required, the sequence may be
 * unusable.
 */
export interface StoredShards {
  producer_id: string;
  shard_index?: number;
  shard_total?: number;
  sequence_next?: number;
  last_restart_at?: string;
}

/** The fields one `shards.json` write needs; the caller keeps the byte counters. */
export interface ShardsRecordWrite {
  directory: string;
  producerId: string;
  shardIndex: number | undefined;
  shardTotal: number | undefined;
  durableNextSequence: number;
  /** The floor the directory already carried at open; the record is never written below it. */
  recordFloor: number;
  restartAt: string | undefined;
}

/**
 * Reads `shards.json`. Returns `null` when the file is absent or unusable — a torn/corrupt
 * record is ignored, because the chunk scan alone recovers the sequence — and refuses
 * (`spool_shard_mismatch`, no overwrite) when the record names another producer or shard slot
 * than the caller passed: that is a client bug, not a resume.
 */
export function readShardsRecord(
  directory: string,
  producerId: string,
  shardIndex: number | undefined,
): StoredShards | null {
  const path = join(directory, SHARDS_FILE);
  const text = readRecordFile(path);
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
  const storedShardIndex =
    typeof parsed.shard_index === 'number' && Number.isSafeInteger(parsed.shard_index)
      ? parsed.shard_index
      : undefined;
  // A record for another producer or shard slot is a client bug (two runs sharing a spool
  // dir, or a recomputed shard index), not a resume: refuse and never overwrite.
  const indexDisagrees =
    shardIndex !== undefined && storedShardIndex !== undefined && storedShardIndex !== shardIndex;
  if (parsed.producer_id !== producerId || indexDisagrees) {
    throw new EvidgateShardRecordMismatchError({
      recordPath: path,
      stored: {
        producer_id: parsed.producer_id,
        ...(storedShardIndex === undefined ? {} : { shard_index: storedShardIndex }),
      },
      requested: {
        producer_id: producerId,
        ...(shardIndex === undefined ? {} : { shard_index: shardIndex }),
      },
    });
  }
  return toStoredShards(parsed, producerId, storedShardIndex);
}

/**
 * The usable fields of a record that named this producer: each optional value is dropped unless it
 * is the type and range the writer may resume from.
 */
function toStoredShards(
  parsed: Partial<ShardsRecord>,
  producerId: string,
  storedShardIndex: number | undefined,
): StoredShards {
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
    producer_id: producerId,
    ...(storedShardIndex === undefined ? {} : { shard_index: storedShardIndex }),
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
export function readCheckpoint(directory: string, producerId: string): SpoolCheckpoint | null {
  try {
    // Best-effort cleanup of the legacy staging name: we never write it again, and a crash
    // between its write and rename could otherwise leave it behind forever. `unlink` never
    // follows a symlink, and a non-file entry is left alone (it is not ours to remove).
    unlinkSync(join(directory, CHECKPOINT_TMP_FILE));
  } catch {
    // ENOENT (nothing to clean) and every other errno: cleanup must not fail the writer.
  }
  const text = readRecordFile(join(directory, CHECKPOINT_FILE));
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
      producer_id: typeof parsed.producer_id === 'string' ? parsed.producer_id : producerId,
      next_sequence: parsed.next_sequence,
    };
  }
  return null;
}

/**
 * Writes `shards.json` atomically (tmp + fsync + rename + directory fsync). Called at rotation,
 * close and restart detection ONLY: the chunk scan recovers the sequence, so this record never
 * has to be on the per-callback path. The value is the sequence after the last line that
 * reached a chunk, never one the writer only buffered — an ahead value would read as a gap
 * after a crash. Only a floor that was already on disk when this writer opened survives.
 * Returns the body length, so the caller can account the size delta against its cap cache.
 */
export function writeShardsRecordFile(write: ShardsRecordWrite): number {
  const record: ShardsRecord = {
    producer_id: write.producerId,
    ...(write.shardIndex === undefined ? {} : { shard_index: write.shardIndex }),
    ...(write.shardTotal === undefined ? {} : { shard_total: write.shardTotal }),
    // `durableNextSequence`, not the live counter. A rotation happens mid-batch, so the live
    // counter already covers envelopes still buffered; writing that value leaves a
    // record ahead of the chunks after a crash, and the resumed writer skips sequences
    // straight into a hole the CLI reader rejects (`spool_corrupt`, "skips the expected").
    // `recordFloor` keeps a floor that arrived on disk without ever raising it.
    sequence_next: Math.max(write.durableNextSequence, write.recordFloor),
    ...(write.restartAt === undefined ? {} : { last_restart_at: write.restartAt }),
  };
  const body = Buffer.from(`${JSON.stringify(record)}\n`, 'utf8');
  const tmpPath = join(write.directory, SHARDS_TMP_FILE);
  const targetPath = join(write.directory, SHARDS_FILE);
  // Clear a leftover tmp (a crash between write and rename) and re-create it with
  // O_EXCL|O_NOFOLLOW, so a planted `shards.json.tmp -> victim` symlink is never truncated or
  // followed.
  try {
    unlinkSync(tmpPath);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
  }
  assertWritableTargetIsRegularFile(tmpPath);
  const fd = openVerified(
    tmpPath,
    fsConstants.O_WRONLY | fsConstants.O_CREAT | fsConstants.O_EXCL | O_NOFOLLOW,
    SPOOL_FILE_MODE,
  );
  try {
    writeAll(fd, body);
    fsyncSync(fd);
  } finally {
    closeSync(fd);
  }
  renameSync(tmpPath, targetPath);
  fsyncDirectory(write.directory);
  return body.byteLength;
}
