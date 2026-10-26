// 04.3.3-T2 (plan 04.3.3 §10 T1 Notes "reporter spool leftovers" (c)/(e); STATUS.md row
// "04.2.4-T1 / 04.3.3-T1 accepted review leftovers (S56, P3)"). These specs pin the writer's
// advisory notice queue, which is the only reporter for two conditions the writer survives:
// (c) a `shards.json` write that failed at chunk ROTATION — the flush that made the batch
// durable still succeeds (F2), so nothing surfaced it before `close()` threw — and (e) a
// record floor read at open that is AHEAD of the chunks: the resume honours it unchanged
// (D-S56-Q9, the record is a floor), so a reporter can only be told that it read one. Both
// are queued once per writer, carry no path and no content, and `takeNotices()` drains them
// without ever throwing.
import { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import { buildEnvelope, EXT_SCHEMA, type UnsignedEnvelope } from './envelope.js';
import { SHARDS_FILE, SHARDS_TMP_FILE, SpoolWriter, type SpoolWriterOptions } from './spool.js';

const tempDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-notices-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function envelope(producerId = 'pw-0'): UnsignedEnvelope {
  return buildEnvelope({
    producer_id: producerId,
    execution_key: '0'.repeat(64),
    attempt_no: 1,
    type: 'test.started',
    ext: { schema: EXT_SCHEMA },
  });
}

function producerDir(spool: string, producerId = 'pw-0'): string {
  return join(spool, 'run-1', producerId);
}

interface StoredRecord {
  producer_id: string;
  sequence_next: number;
}

function readRecord(spool: string, producerId = 'pw-0'): StoredRecord {
  const text = readFileSync(join(producerDir(spool, producerId), SHARDS_FILE), 'utf8');
  return JSON.parse(text) as StoredRecord;
}

/** Every durable sequence of the producer, in chunk order — what the resumed writer sees. */
function sequences(spool: string, producerId = 'pw-0'): number[] {
  const dir = producerDir(spool, producerId);
  const names = readdirSync(dir).filter((entry) => /^\d{6}\.jsonl$/.test(entry));
  const result: number[] = [];
  for (const name of names.sort()) {
    const text = readFileSync(join(dir, name), 'utf8');
    if (text === '') continue;
    for (const line of text.trim().split('\n')) {
      const parsed = JSON.parse(line) as { sequence: number };
      result.push(parsed.sequence);
    }
  }
  return result;
}

/** A pid the liveness seam treats as alive, so a reopened writer takes the released lock. */
const FIXED_PID = 1000;

function options(spool: string, overrides: Partial<SpoolWriterOptions> = {}): SpoolWriterOptions {
  return {
    spoolDir: spool,
    runId: 'run-1',
    producerId: 'pw-0',
    lock: { pid: FIXED_PID, isProcessAlive: () => true, probeDelayMs: 0 },
    ...overrides,
  };
}

const ERRNO = /E(ISDIR|PERM|ACCES)/;

describe('spool notices (04.3.3-T2)', () => {
  it('queues one notice for a failed rotation record write and still throws at close', () => {
    const spool = tempSpool();
    // One line per chunk: the second flush rotates, which is the only write point that can
    // fail without failing the flush that just made the batch durable (F2).
    const writer = new SpoolWriter(options(spool, { chunkMaxLines: 1 }));
    // The obstacle `shards-store.test.ts` uses for the same failure: a directory planted at
    // the staging name makes every record write fail (EISDIR/EPERM).
    mkdirSync(join(producerDir(spool), SHARDS_TMP_FILE), { recursive: true });

    writer.append(envelope());
    writer.flush();
    // No rotation yet, so nothing failed and nothing is queued.
    expect(writer.takeNotices()).toEqual([]);

    writer.append(envelope());
    expect(() => writer.flush()).not.toThrow();
    expect(sequences(spool)).toEqual([1, 2]);
    expect(writer.lastShardsWriteFailure).toBeInstanceOf(Error);

    const notices = writer.takeNotices();
    expect(notices).toHaveLength(1);
    const [notice] = notices;
    expect(notice?.code).toBe('shards_write_failed');
    // The code/errno only: the real error names `shards.json.tmp` and its absolute path.
    expect(notice?.message).toMatch(ERRNO);
    expect(notice?.message).toContain('shards.json');
    expect(notice?.message).not.toContain(SHARDS_TMP_FILE);
    expect(notice?.message).not.toContain(producerDir(spool));
    expect(notice?.message).not.toContain(spool);

    // takeNotices cleared the queue, and a second failed rotation queues nothing new: one
    // notice per writer, however many write points fail afterwards.
    expect(writer.takeNotices()).toEqual([]);
    writer.append(envelope());
    expect(() => writer.flush()).not.toThrow();
    expect(sequences(spool)).toEqual([1, 2, 3]);
    expect(writer.lastShardsWriteFailure).toBeInstanceOf(Error);
    expect(writer.takeNotices()).toEqual([]);

    // The notice never replaces close(): that is still where a refused record is an error.
    expect(() => writer.close()).toThrow(ERRNO);
    expect(writer.takeNotices()).toEqual([]);
  });

  it('queues nothing for a clean open, a clean restart and a record one past the chunks', () => {
    const spool = tempSpool();
    const writer = new SpoolWriter(options(spool));
    expect(writer.takeNotices()).toEqual([]);
    writer.append(envelope());
    writer.flush();
    writer.close();
    // The writer's own record is `highest chunk sequence + 1` = 2: exactly the boundary the
    // floor check must NOT report.
    expect(readRecord(spool).sequence_next).toBe(2);

    const resumed = new SpoolWriter(options(spool));
    expect(resumed.restartRecordedAt).toBeDefined();
    expect(resumed.nextSequenceNumber).toBe(2);
    expect(resumed.takeNotices()).toEqual([]);
    resumed.close();
  });

  it('queues one floor-ahead notice for a hand-edited floor and resumes at the floor', () => {
    const spool = tempSpool();
    const writer = new SpoolWriter(options(spool));
    writer.append(envelope());
    writer.append(envelope());
    writer.flush();
    writer.close();
    expect(sequences(spool)).toEqual([1, 2]);

    // A floor far ahead of the chunks: nothing this writer mints looks like this (F1), so it
    // can only come from a hand-edited or legacy file.
    const recordPath = join(producerDir(spool), SHARDS_FILE);
    const record = readRecord(spool);
    writeFileSync(recordPath, `${JSON.stringify({ ...record, sequence_next: 40 })}\n`);

    const resumed = new SpoolWriter(options(spool));
    const notices = resumed.takeNotices();
    expect(notices).toHaveLength(1);
    const [notice] = notices;
    expect(notice?.code).toBe('shards_floor_ahead');
    // Both numbers, so a caller can act without opening the file.
    expect(notice?.message).toContain('floor 40');
    expect(notice?.message).toContain('highest chunk sequence 2');
    expect(notice?.message).not.toContain(spool);

    // D-S56-Q9 is untouched: the record is a FLOOR, so the writer resumes there instead of
    // closing the hole — the notice only says it read one.
    expect(resumed.nextSequenceNumber).toBe(40);
    expect(resumed.append(envelope()).sequence).toBe(40);
    // Once per writer.
    expect(resumed.takeNotices()).toEqual([]);
    resumed.close();
    expect(readRecord(spool).sequence_next).toBe(41);
    expect(sequences(spool)).toEqual([1, 2, 40]);
  });

  it('keeps returning an empty list after a take without clearing the recorded failure', () => {
    const spool = tempSpool();
    const writer = new SpoolWriter(options(spool, { chunkMaxLines: 1 }));
    mkdirSync(join(producerDir(spool), SHARDS_TMP_FILE), { recursive: true });
    writer.append(envelope());
    writer.flush();
    writer.append(envelope());
    writer.flush();

    expect(writer.takeNotices()).toHaveLength(1);
    // The queue and the failure state are different things: draining the notice must not
    // make the failure invisible.
    expect(writer.lastShardsWriteFailure).toBeInstanceOf(Error);
    expect(writer.takeNotices()).toEqual([]);
    expect(writer.lastShardsWriteFailure).toBeInstanceOf(Error);

    // A closed writer has nothing left to queue; draining still never throws.
    expect(() => writer.close()).toThrow(ERRNO);
    expect(writer.takeNotices()).toEqual([]);
  });
});
