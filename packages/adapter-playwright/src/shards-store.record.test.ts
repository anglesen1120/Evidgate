// The reporter-owned `shards.json` sequence record: its shape and identity, when it is
// written (rotation, close and restart only), the mismatch refusal and the legacy
// `checkpoint.json` fallback.
import {
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import {
  CHECKPOINT_FILE,
  CHECKPOINT_TMP_FILE,
  EvidgateInvalidShardError,
  EvidgateShardRecordMismatchError,
  SHARDS_FILE,
  SHARDS_TMP_FILE,
  SpoolWriter,
} from './spool.js';
import {
  chunks,
  envelope,
  options,
  producerDir,
  tempSpool,
  useTempSpoolCleanup,
} from './shards-store.testing.js';

useTempSpoolCleanup();

const isWindows = process.platform === 'win32';

/** The record as the writer writes it (the tests below assert its exact content). */
interface StoredRecord {
  producer_id: string;
  shard_index?: number;
  shard_total?: number;
  sequence_next: number;
  last_restart_at?: string;
}

function readRecord(spool: string, producerId = 'pw-0'): StoredRecord {
  const text = readFileSync(join(producerDir(spool, producerId), SHARDS_FILE), 'utf8');
  // The file was just written by the writer under test; the assertions pin the shape.
  return JSON.parse(text) as StoredRecord;
}

/**
 * A pid no process on this host has, so the writer after a "crash" finds the abandoned
 * lock stale and takes it over (the reporter kills its worker; nothing releases the lock).
 */
const CRASHED_PID = 4242;

/**
 * Every sequence on disk for the producer, in chunk order — the order the CLI reader walks
 * them in (`readUnacked`), so a contiguous list is exactly what its rule accepts.
 */
function sequences(spool: string, producerId = 'pw-0'): number[] {
  const result: number[] = [];
  const dir = producerDir(spool, producerId);
  for (const entry of readdirSync(dir, { withFileTypes: true })
    .filter((item) => item.isFile() && /^\d{6}\.jsonl$/.test(item.name))
    .sort((left, right) => (left.name < right.name ? -1 : 1))) {
    const text = readFileSync(join(dir, entry.name), 'utf8');
    if (text === '') continue;
    expect(text.endsWith('\n')).toBe(true);
    for (const line of text.trim().split('\n')) {
      const parsed: unknown = JSON.parse(line);
      if (typeof parsed !== 'object' || parsed === null || !('sequence' in parsed)) {
        throw new Error(`spool chunk line is not an envelope: ${line}`);
      }
      const sequence = parsed.sequence;
      if (typeof sequence !== 'number') {
        throw new Error(`spool chunk line has no numeric sequence: ${line}`);
      }
      result.push(sequence);
    }
  }
  return result;
}

describe('shards.json record', () => {
  it('records producer, shard slot and sequence_next at close', () => {
    const spool = tempSpool();
    const writer = new SpoolWriter(options(spool, { shard: { index: 2, total: 3 } }));
    writer.appendMany([envelope('pw-0'), envelope('pw-0')]);
    writer.close();

    expect(readRecord(spool)).toEqual({
      producer_id: 'pw-0',
      shard_index: 2,
      shard_total: 3,
      sequence_next: 3,
    });
  });

  it('omits the shard keys when the caller declares no shard slot', () => {
    const spool = tempSpool();
    const writer = new SpoolWriter(options(spool));
    writer.append(envelope('pw-0'));
    writer.close();

    expect(readRecord(spool)).toEqual({ producer_id: 'pw-0', sequence_next: 2 });
  });

  it('is written at rotation, close and restart only — never on a per-callback flush', () => {
    const spool = tempSpool();
    const writer = new SpoolWriter(
      options(spool, { flushThreshold: 10, chunkMaxLines: 6, chunkMaxBytes: 1024 * 1024 }),
    );
    const record = join(producerDir(spool), SHARDS_FILE);

    // A fresh producer directory is not a restart: nothing is written at open.
    expect(existsSync(record)).toBe(false);
    expect(writer.shardsWriteCount).toBe(0);

    // Six per-callback flushes fill the chunk exactly, with no rotation yet: the record stays
    // absent and its counter does not move (the counter is a seam; the file's absence is the
    // observable behaviour).
    for (let index = 0; index < 6; index += 1) {
      writer.append(envelope('pw-0'));
      writer.flush();
    }
    expect(writer.pendingCount).toBe(0);
    expect(existsSync(record)).toBe(false);
    expect(writer.shardsWriteCount).toBe(0);
    expect(chunks(spool)).toEqual([6]);

    // Rotation (chunkMaxLines = 6) is a write point: the 7th line finds the chunk full and
    // rotates before writing.
    writer.append(envelope('pw-0'));
    writer.flush();
    expect(writer.shardsWriteCount).toBe(1);
    expect(chunks(spool)).toEqual([6, 1]);
    // The record covers the chunk that just closed (sequences 1..6), not the 7th line
    // this very flush goes on to write — a rotation happens mid-batch, and a record ahead of
    // the chunks would leave a hole after a crash.
    const afterRotation = readRecord(spool);
    expect(afterRotation.sequence_next).toBe(7);

    // Further flushes do not rewrite it: the file's bytes stay byte-identical while the
    // chunks move on (the counter stays put too, so this is not just an equal value).
    const afterRotationBytes = readFileSync(record, 'utf8');
    writer.append(envelope('pw-0'));
    writer.flush();
    expect(writer.shardsWriteCount).toBe(1);
    expect(readFileSync(record, 'utf8')).toBe(afterRotationBytes);

    // Close is a write point, and it records the final value.
    writer.close();
    expect(writer.shardsWriteCount).toBe(2);
    expect(readRecord(spool).sequence_next).toBe(writer.nextSequenceNumber);
    expect(writer.nextSequenceNumber).toBe(9);
  });

  it('records last_restart_at and keeps the producer directory on a restart', () => {
    const spool = tempSpool();
    const first = new SpoolWriter(options(spool, { shard: { index: 1, total: 2 } }));
    first.append(envelope('pw-0'));
    first.close();

    const restartedAt = '2026-10-24T02:03:04.005Z';
    const second = new SpoolWriter(
      options(spool, { shard: { index: 1, total: 2 }, now: () => new Date(restartedAt) }),
    );
    expect(second.restartRecordedAt).toBe(restartedAt);
    expect(second.nextSequenceNumber).toBe(2);
    second.append(envelope('pw-0'));
    second.close();

    // The same deterministic producer id, no restart suffix directory.
    expect(readdirSync(join(spool, 'run-1'))).toEqual(['pw-0']);
    expect(readRecord(spool)).toMatchObject({
      producer_id: 'pw-0',
      shard_index: 1,
      shard_total: 2,
      sequence_next: 3,
      last_restart_at: restartedAt,
    });
  });

  it('refuses a record for another producer without overwriting it', () => {
    const spool = tempSpool();
    const dir = producerDir(spool);
    mkdirSync(dir, { recursive: true });
    const recordPath = join(dir, SHARDS_FILE);
    const other = `${JSON.stringify({ producer_id: 'pw-9', shard_index: 1, sequence_next: 4 })}\n`;
    writeFileSync(recordPath, other);

    let error: unknown;
    try {
      new SpoolWriter(options(spool));
    } catch (thrown) {
      error = thrown;
    }
    expect(error).toBeInstanceOf(EvidgateShardRecordMismatchError);
    expect((error as EvidgateShardRecordMismatchError).code).toBe('spool_shard_mismatch');
    expect((error as EvidgateShardRecordMismatchError).stored.producer_id).toBe('pw-9');
    expect((error as EvidgateShardRecordMismatchError).requested.producer_id).toBe('pw-0');
    // No overwrite, and the refused writer left no lock behind.
    expect(readFileSync(recordPath, 'utf8')).toBe(other);
    expect(readdirSync(dir)).toEqual([SHARDS_FILE]);
  });

  it('refuses a record for another shard slot of the same producer', () => {
    const spool = tempSpool();
    const dir = producerDir(spool);
    mkdirSync(dir, { recursive: true });
    writeFileSync(
      join(dir, SHARDS_FILE),
      `${JSON.stringify({ producer_id: 'pw-0', shard_index: 1, sequence_next: 2 })}\n`,
    );

    expect(() => new SpoolWriter(options(spool, { shard: { index: 2, total: 3 } }))).toThrow(
      EvidgateShardRecordMismatchError,
    );
  });

  it('rejects an invalid shard option', () => {
    const spool = tempSpool();
    for (const shard of [
      { index: 0 },
      { index: 257 },
      { index: 1.5 },
      { index: 3, total: 2 },
      { index: 1, total: 0 },
      { index: 1, total: 257 },
    ]) {
      expect(() => new SpoolWriter(options(spool, { shard }))).toThrow(EvidgateInvalidShardError);
    }
    // Nothing was created for a rejected option.
    expect(readdirSync(spool)).toEqual([]);
  });

  it('reads a legacy checkpoint.json as a fallback and never writes it again', () => {
    const spool = tempSpool();
    const dir = producerDir(spool);
    mkdirSync(dir, { recursive: true });
    // A legacy producer directory: one chunk with sequences 1..3 plus its checkpoint.
    writeFileSync(
      join(dir, '000001.jsonl'),
      [1, 2, 3]
        .map((sequence) => `${JSON.stringify({ ...envelope('pw-0'), sequence })}\n`)
        .join(''),
    );
    const legacy = `${JSON.stringify({ producer_id: 'pw-0', next_sequence: 4 })}\n`;
    writeFileSync(join(dir, CHECKPOINT_FILE), legacy);
    // A leftover staging name from the old writer's crash between write and rename.
    writeFileSync(join(dir, CHECKPOINT_TMP_FILE), '{torn');

    const writer = new SpoolWriter(options(spool));
    expect(writer.nextSequenceNumber).toBe(4);
    writer.append(envelope('pw-0'));
    writer.close();

    // The legacy record is untouched, its staging name is cleaned up, and the new store
    // carries the state instead: the file is never written again.
    expect(readFileSync(join(dir, CHECKPOINT_FILE), 'utf8')).toBe(legacy);
    expect(existsSync(join(dir, CHECKPOINT_TMP_FILE))).toBe(false);
    expect(readRecord(spool)).toMatchObject({ producer_id: 'pw-0', sequence_next: 5 });
    expect(chunks(spool)).toEqual([4]);
  });

  it('resumes a legacy checkpoint.json ahead of the chunks instead of reusing sequences', () => {
    const spool = tempSpool();
    const dir = producerDir(spool);
    mkdirSync(dir, { recursive: true });
    // The server only refuses a REUSED `(run, producer, sequence)`; a hole is an accepted
    // reconciliation fact (contracts/events/ordering.md). So a checkpoint ahead of the
    // chunks (a rotated-away or lost chunk) is a floor, never a ceiling: resuming at 1 would
    // re-emit an already accepted sequence.
    writeFileSync(
      join(dir, CHECKPOINT_FILE),
      `${JSON.stringify({ producer_id: 'pw-0', next_sequence: 7 })}\n`,
    );

    const writer = new SpoolWriter(options(spool));
    expect(writer.nextSequenceNumber).toBe(7);
    expect(writer.restartRecordedAt).toBeDefined();
    writer.close();
    expect(readRecord(spool).sequence_next).toBe(7);
  });

  it('ignores a leftover shards.json.tmp (crash between write and rename)', () => {
    const spool = tempSpool();
    const dir = producerDir(spool);
    mkdirSync(dir, { recursive: true });
    writeFileSync(
      join(dir, '000001.jsonl'),
      `${JSON.stringify({ ...envelope('pw-0'), sequence: 1 })}\n`,
    );
    writeFileSync(join(dir, SHARDS_TMP_FILE), '{"producer_id":"pw-0","shard_ind');

    const writer = new SpoolWriter(options(spool));
    // The torn staging file is not a record: the chunk scan decides, and the restart write
    // replaces the leftover instead of trusting or following it.
    expect(writer.nextSequenceNumber).toBe(2);
    expect(readRecord(spool)).toMatchObject({ producer_id: 'pw-0', sequence_next: 2 });
    writer.close();
    expect(existsSync(join(dir, SHARDS_TMP_FILE))).toBe(false);
  });

  it('refuses a symlinked shards.json instead of following it', () => {
    if (isWindows) return;
    const spool = tempSpool();
    const dir = producerDir(spool);
    mkdirSync(dir, { recursive: true });
    const victim = join(spool, 'victim.json');
    writeFileSync(victim, '{"producer_id":"pw-0","sequence_next":99}\n');
    symlinkSync(victim, join(dir, SHARDS_FILE));

    expect(() => new SpoolWriter(options(spool))).toThrow(/refusing to read spool record/);
    expect(readFileSync(victim, 'utf8')).toBe('{"producer_id":"pw-0","sequence_next":99}\n');
  });

  // A rotation happens MID-BATCH, so the writer's `nextSequence` already covers
  // envelopes the flush has not written yet. Recording that value leaves a record AHEAD of
  // the chunks after a crash; the resumed writer then skips sequences, and the CLI reader
  // (`readUnacked`, "expected = cursor + emitted") refuses the hole as `spool_corrupt`.
  it('never records a sequence ahead of the chunks, so a crash mid-batch opens no hole', () => {
    const spool = tempSpool();
    // One chunk fewer than the batch needs. The first rotation writes the record, the second
    // one fails, so the batch's last envelopes never reach a chunk — that is the crash
    // window a rotate-inside-flush opens. (Planted after the writer opened its first chunk:
    // an obstacle present at open would be caught by the chunk scan instead.)
    const crashed = new SpoolWriter(
      options(spool, {
        lock: { pid: CRASHED_PID, isProcessAlive: () => true, probeDelayMs: 0 },
        flushThreshold: 5,
        chunkMaxLines: 2,
      }),
    );
    mkdirSync(join(producerDir(spool), '000003.jsonl'), { recursive: true });
    expect(() => crashed.appendMany([1, 2, 3, 4, 5].map(() => envelope('pw-0')))).toThrow(
      /non-regular spool file/,
    );

    // Sequences 1..4 reached chunks; the record written at the rotation that closed chunk 1
    // says 3 (the sequence after its last line), never 6 (what the batch had minted).
    expect(sequences(spool)).toEqual([1, 2, 3, 4]);
    expect(readRecord(spool).sequence_next).toBe(3);
    // No `close()`: the worker was killed here, and the lock still names CRASHED_PID.
    rmSync(join(producerDir(spool), '000003.jsonl'), { recursive: true });

    const resumed = new SpoolWriter(options(spool, { lock: { probeDelayMs: 0 } }));
    expect(resumed.restartRecordedAt).toBeDefined();
    expect(resumed.nextSequenceNumber).toBe(5);
    // The next minted sequence is contiguous with the last durable line, so walking the
    // chunks the way the CLI does yields 1..5 with no skip.
    expect(resumed.append(envelope('pw-0')).sequence).toBe(5);
    resumed.close();
    expect(sequences(spool)).toEqual([1, 2, 3, 4, 5]);
    expect(readRecord(spool).sequence_next).toBe(6);
  });

  // `shards.json` is advisory — the chunk scan is authoritative on resume — so a
  // record write that fails on a full/broken disk must not fail the flush that just made
  // the batch durable. The failure is remembered and retried at the next write point.
  it('treats a failed record write at rotation as advisory, remembers it and retries', () => {
    const spool = tempSpool();
    const dir = producerDir(spool);
    // One line per chunk, so the second flush rotates. A directory planted at the staging
    // name makes that record write fail (EISDIR/EPERM) — the inducible stand-in for ENOSPC
    // or EIO, and the same obstacle the close-time test uses.
    const writer = new SpoolWriter(options(spool, { chunkMaxLines: 1 }));
    mkdirSync(join(dir, SHARDS_TMP_FILE), { recursive: true });

    writer.append(envelope('pw-0'));
    writer.flush();
    writer.append(envelope('pw-0'));
    expect(() => writer.flush()).not.toThrow();

    // The flush's line is durable, the record write is not counted, and the failure is kept.
    expect(sequences(spool)).toEqual([1, 2]);
    expect(existsSync(join(dir, SHARDS_FILE))).toBe(false);
    expect(writer.shardsWriteCount).toBe(0);
    const failure = writer.lastShardsWriteFailure;
    expect(failure).toBeInstanceOf(Error);
    expect(failure?.message).toMatch(/shards\.json\.tmp/);
    expect(failure?.message).toMatch(/E(ISDIR|PERM|ACCES)/);

    // Once the disk is healthy again the next rotation retries the record and clears it: no
    // replay is needed, because the value is derived from the live counters.
    rmSync(join(dir, SHARDS_TMP_FILE), { recursive: true });
    writer.append(envelope('pw-0'));
    writer.flush();
    expect(writer.lastShardsWriteFailure).toBeUndefined();
    expect(writer.shardsWriteCount).toBe(1);
    expect(readRecord(spool).sequence_next).toBe(3);
    expect(existsSync(join(dir, SHARDS_TMP_FILE))).toBe(false);
    writer.close();
  });
});
