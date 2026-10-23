// 04.3.3-T1 (AC1/AC2, T54/T84): `shards.json` as the single reporter-owned sequence record.
// These specs pin the record shape and identity, its write CADENCE (rotation, close and
// restart detection only — never a per-callback flush), the mismatch refusal, the legacy
// `checkpoint.json` fallback, and the `EVIDGATE_SPOOL_MAX_BYTES` write-time cap
// (contracts/cli.md §11.4) including its exact boundary.
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import { buildEnvelope, EXT_SCHEMA, type UnsignedEnvelope } from './envelope.js';
import {
  CHECKPOINT_FILE,
  CHECKPOINT_TMP_FILE,
  DEFAULT_SPOOL_MAX_BYTES,
  EvidgateInvalidShardError,
  EvidgateInvalidSpoolConfigError,
  EvidgateShardRecordMismatchError,
  EvidgateSpoolCapExceededError,
  MAX_SPOOL_MAX_BYTES,
  resolveSpoolMaxBytes,
  SHARDS_FILE,
  SHARDS_TMP_FILE,
  SpoolWriter,
  type SpoolWriterOptions,
} from './spool.js';

const isWindows = process.platform === 'win32';
const tempDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-shards-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function envelope(producerId: string): UnsignedEnvelope {
  return buildEnvelope({
    producer_id: producerId,
    execution_key: '0'.repeat(64),
    attempt_no: 1,
    type: 'test.started',
    ext: { schema: EXT_SCHEMA },
  });
}

/** The exact UTF-8 bytes one spool line occupies (the cap is measured in these). */
function lineBytes(event: UnsignedEnvelope, sequence: number): number {
  return Buffer.byteLength(`${JSON.stringify({ ...event, sequence })}\n`, 'utf8');
}

function producerDir(spool: string, producerId = 'pw-0'): string {
  return join(spool, 'run-1', producerId);
}

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

/** Complete lines per chunk of a producer directory, in file-name order. */
function chunks(spool: string, producerId = 'pw-0'): number[] {
  return readdirSync(producerDir(spool, producerId))
    .filter((name) => /^\d{6}\.jsonl$/.test(name))
    .sort()
    .map((name) => {
      const text = readFileSync(join(producerDir(spool, producerId), name), 'utf8');
      if (text === '') return 0;
      expect(text.endsWith('\n')).toBe(true);
      return text.trim().split('\n').length;
    });
}

/** A pid the liveness seam treats as alive, so two writers in this process can both exist. */
const FIXED_PID = 1000;

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

function options(spool: string, overrides: Partial<SpoolWriterOptions> = {}): SpoolWriterOptions {
  return {
    spoolDir: spool,
    runId: 'run-1',
    producerId: 'pw-0',
    lock: { pid: FIXED_PID, isProcessAlive: () => true, probeDelayMs: 0 },
    ...overrides,
  };
}

/**
 * The run directory's base bytes before the first append: exactly the lock record this
 * writer creates (0600, `{pid, started_at}`), so a cap can be set to the byte.
 */
function lockBytes(pid: number): number {
  const scratch = tempSpool();
  const writer = new SpoolWriter(
    options(scratch, { lock: { pid, isProcessAlive: () => true, probeDelayMs: 0 } }),
  );
  const bytes = writer.runBytesInUse;
  writer.close();
  return bytes;
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
    // F1: the record covers the chunk that just closed (sequences 1..6), not the 7th line
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

    // T84: the same deterministic producer id, no restart suffix directory.
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
    // A 04.3.1 producer directory: one chunk with sequences 1..3 plus its checkpoint.
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
    // reconciliation fact (contracts/events/ordering.md §1/§3). So a checkpoint ahead of the
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

  // F1 (P2): a rotation happens MID-BATCH, so the writer's `nextSequence` already covers
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

  // F2 (P3): `shards.json` is advisory — the chunk scan is authoritative on resume — so a
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

describe('EVIDGATE_SPOOL_MAX_BYTES write-time cap', () => {
  it('resolves the env value with the CLI rules', () => {
    expect(resolveSpoolMaxBytes({})).toBe(DEFAULT_SPOOL_MAX_BYTES);
    expect(DEFAULT_SPOOL_MAX_BYTES).toBe(268_435_456);
    expect(resolveSpoolMaxBytes({ EVIDGATE_SPOOL_MAX_BYTES: '' })).toBe(DEFAULT_SPOOL_MAX_BYTES);
    expect(resolveSpoolMaxBytes({ EVIDGATE_SPOOL_MAX_BYTES: '1' })).toBe(1);
    expect(resolveSpoolMaxBytes({ EVIDGATE_SPOOL_MAX_BYTES: ' 2048 ' })).toBe(2048);
    expect(resolveSpoolMaxBytes({ EVIDGATE_SPOOL_MAX_BYTES: '1048576' })).toBe(1_048_576);
    expect(resolveSpoolMaxBytes({ EVIDGATE_SPOOL_MAX_BYTES: String(MAX_SPOOL_MAX_BYTES) })).toBe(
      MAX_SPOOL_MAX_BYTES,
    );
    expect(MAX_SPOOL_MAX_BYTES).toBe(2 ** 40);
  });

  it('refuses a malformed env value without echoing it', () => {
    for (const value of ['lots', '0', '-5', '1.5', '1MiB', String(MAX_SPOOL_MAX_BYTES + 1)]) {
      let error: unknown;
      try {
        resolveSpoolMaxBytes({ EVIDGATE_SPOOL_MAX_BYTES: value });
      } catch (thrown) {
        error = thrown;
      }
      expect(error).toBeInstanceOf(EvidgateInvalidSpoolConfigError);
      const invalid = error as EvidgateInvalidSpoolConfigError;
      expect(invalid.code).toBe('invalid_config');
      expect(invalid.message).toContain('EVIDGATE_SPOOL_MAX_BYTES');
      expect(invalid.message).not.toContain(value);
    }
  });

  it('reads the env once in the constructor and lets an explicit cap win', () => {
    const spool = tempSpool();
    const fromEnv = new SpoolWriter(
      options(spool, { env: { EVIDGATE_SPOOL_MAX_BYTES: ' 4096 ' } }),
    );
    expect(fromEnv.maxRunBytes).toBe(4096);
    fromEnv.close();

    const explicit = new SpoolWriter(
      options(spool, { env: { EVIDGATE_SPOOL_MAX_BYTES: '4096' }, maxRunBytes: 8192 }),
    );
    expect(explicit.maxRunBytes).toBe(8192);
    explicit.close();
  });

  it('refuses a writer whose env cap is malformed, before touching the directory', () => {
    const spool = tempSpool();
    const malformed = { env: { EVIDGATE_SPOOL_MAX_BYTES: 'lots' } };
    expect(() => new SpoolWriter(options(spool, malformed))).toThrow(
      EvidgateInvalidSpoolConfigError,
    );
    expect(readdirSync(spool)).toEqual([]);
  });

  it('passes a batch that lands exactly on the cap and refuses the next byte', () => {
    const spool = tempSpool();
    const base = lockBytes(FIXED_PID);
    const event = envelope('pw-0');
    const cap = base + lineBytes(event, 1);
    const writer = new SpoolWriter(options(spool, { maxRunBytes: cap }));

    writer.append(event);
    expect(() => writer.flush()).not.toThrow();
    expect(writer.runBytesInUse).toBe(cap);

    writer.append(event);
    let error: unknown;
    try {
      writer.flush();
    } catch (thrown) {
      error = thrown;
    }
    expect(error).toBeInstanceOf(EvidgateSpoolCapExceededError);
    const exceeded = error as EvidgateSpoolCapExceededError;
    expect(exceeded.code).toBe('spool_cap_exceeded');
    expect(exceeded.runId).toBe('run-1');
    expect(exceeded.producerId).toBe('pw-0');
    expect(exceeded.capBytes).toBe(cap);
    expect(exceeded.usedBytes).toBe(cap);
    expect(exceeded.requiredBytes).toBe(lineBytes(event, 2));
    expect(exceeded.message).toContain('EVIDGATE_SPOOL_MAX_BYTES');

    // Never a partial line, never a silent drop: one complete line, the refused one queued.
    const chunk = readFileSync(join(producerDir(spool), '000001.jsonl'), 'utf8');
    expect(chunk.endsWith('\n')).toBe(true);
    expect(chunk.trim().split('\n')).toHaveLength(1);
    expect(writer.pendingCount).toBe(1);
    expect(writer.runBytesInUse).toBe(cap);
  });

  it('writes no line at all when a multi-line batch cannot fit', () => {
    const spool = tempSpool();
    const base = lockBytes(FIXED_PID);
    const event = envelope('pw-0');
    const cap = base + lineBytes(event, 1);
    const writer = new SpoolWriter(options(spool, { maxRunBytes: cap }));

    writer.appendMany([event, event, event]);
    expect(() => writer.flush()).toThrow(EvidgateSpoolCapExceededError);
    // All-or-nothing: the check runs before the first byte, so the (eagerly created) chunk
    // holds no complete and no partial line, and the whole batch stays queued.
    expect(chunks(spool)).toEqual([0]);
    expect(writer.pendingCount).toBe(3);
  });

  it('counts every producer of the run, not just its own directory', () => {
    const spool = tempSpool();
    const base = lockBytes(FIXED_PID);
    const event = envelope('pw-0');
    const cap = base + 2 * lineBytes(event, 1);

    const first = new SpoolWriter(options(spool, { maxRunBytes: cap }));
    first.appendMany([event, event]);
    first.flush();
    expect(first.runBytesInUse).toBe(cap);

    // The second producer's own base already includes the first producer's chunk bytes, so
    // its first byte is refused: the cap is per run directory (contracts/cli.md §11.4).
    const second = new SpoolWriter(options(spool, { producerId: 'pw-1', maxRunBytes: cap }));
    expect(second.runBytesInUse).toBeGreaterThan(cap);
    second.append(envelope('pw-1'));
    expect(() => second.flush()).toThrow(EvidgateSpoolCapExceededError);
    expect(chunks(spool, 'pw-1')).toEqual([0]);

    first.close();
    // The refused envelope stays queued, so closing the second producer retries it and fails
    // the same way: the caller always learns about the cap (never a silent drop).
    expect(() => second.close()).toThrow(EvidgateSpoolCapExceededError);
  });
});
