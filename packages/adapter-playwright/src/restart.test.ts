// 04.3.3-T1 (AC2/T84): restart behaviour of the reporter's sequence store. A producer that
// dies without closing (Playwright kills the worker, the browser crashes) must resume on the
// same `producer_id`, never mint a suffix, never reuse `(run, producer, sequence)` and never
// lose a chunk. The resume value is `max(shards.json, last chunk sequence + 1)`; a record
// ahead of the chunks is a floor (reuse is the only sequence refusal the server has — a hole
// is an accepted reconciliation fact, contracts/events/ordering.md §1/§3), while an unusable
// value (PS8(b), e.g. `1e300`) is dropped and the chunk scan decides.
import { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import { buildEnvelope, EXT_SCHEMA, type UnsignedEnvelope } from './envelope.js';
import { SHARDS_FILE, SpoolWriter, type SpoolWriterOptions } from './spool.js';

/** The pid of the "crashed" writer: never this process, so the default liveness check says dead. */
const CRASHED_PID = 4242;
const PRODUCER_ID = 'pw-0';

const baseDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-restart-'));
  baseDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of baseDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function envelope(): UnsignedEnvelope {
  return buildEnvelope({
    producer_id: PRODUCER_ID,
    execution_key: '0'.repeat(64),
    attempt_no: 1,
    type: 'test.started',
    ext: { schema: EXT_SCHEMA },
  });
}

function options(spool: string, overrides: Partial<SpoolWriterOptions> = {}): SpoolWriterOptions {
  return { spoolDir: spool, runId: 'run-1', producerId: PRODUCER_ID, ...overrides };
}

/** A writer whose lock belongs to `CRASHED_PID`, i.e. one the next writer finds dead. */
function crashedWriter(spool: string, overrides: Partial<SpoolWriterOptions> = {}): SpoolWriter {
  return new SpoolWriter(
    options(spool, { lock: { pid: CRASHED_PID, isProcessAlive: () => true }, ...overrides }),
  );
}

/** Every sequence on disk for the producer, in chunk order. */
function sequences(spool: string): number[] {
  const dir = join(spool, 'run-1', PRODUCER_ID);
  const result: number[] = [];
  for (const name of readdirSync(dir)
    .filter((entry) => /^\d{6}\.jsonl$/.test(entry))
    .sort()) {
    const text = readFileSync(join(dir, name), 'utf8');
    if (text === '') continue;
    // A chunk always ends on a line boundary: the writer never leaves a fragment.
    expect(text.endsWith('\n')).toBe(true);
    for (const line of text.trim().split('\n')) {
      result.push((JSON.parse(line) as { sequence: number }).sequence);
    }
  }
  return result;
}

describe('reporter restart (T84)', () => {
  it('resumes contiguously after a crash between rotations, reusing nothing', () => {
    const spool = tempSpool();
    // Rotation every 4 lines, one line per flush: the record written at the last rotation
    // says 9 while the chunks already hold 1..10, so the chunk scan has to win.
    const crashed = crashedWriter(spool, { flushThreshold: 1, chunkMaxLines: 4 });
    for (let index = 0; index < 10; index += 1) crashed.append(envelope());
    expect(sequences(spool)).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
    // (No close: the worker was killed. The lock still names CRASHED_PID.)

    const resumed = new SpoolWriter(options(spool));
    expect(resumed.nextSequenceNumber).toBe(11);
    expect(typeof resumed.restartRecordedAt).toBe('string');
    resumed.appendMany([envelope(), envelope()]);
    resumed.close();

    // Contiguous across the restart, no sequence emitted twice (AC2).
    expect(sequences(spool)).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
    const record = JSON.parse(
      readFileSync(join(spool, 'run-1', PRODUCER_ID, SHARDS_FILE), 'utf8'),
    ) as { sequence_next: number; producer_id: string };
    expect(record.producer_id).toBe(PRODUCER_ID);
    expect(record.sequence_next).toBe(13);
  });

  it('keeps the same producer directory across restarts (no suffix is minted)', () => {
    const spool = tempSpool();
    const first = crashedWriter(spool, { shard: { index: 1, total: 2 } });
    first.append(envelope());
    first.close();
    const second = new SpoolWriter(options(spool, { shard: { index: 1, total: 2 } }));
    second.append(envelope());
    second.close();
    const third = crashedWriter(spool, { shard: { index: 1, total: 2 } });
    third.append(envelope());
    third.close();

    expect(readdirSync(join(spool, 'run-1'))).toEqual([PRODUCER_ID]);
    expect(sequences(spool)).toEqual([1, 2, 3]);
  });

  it('resumes from the record when the chunks are gone, instead of reusing sequences', () => {
    const spool = tempSpool();
    const first = new SpoolWriter(options(spool));
    first.appendMany([envelope(), envelope(), envelope()]);
    first.close();
    const dir = join(spool, 'run-1', PRODUCER_ID);
    // A chunk file lost (or rotated away by an operator): the record is the only surviving
    // evidence, and resuming at 1 would re-emit an already accepted sequence.
    rmSync(join(dir, '000001.jsonl'));

    const resumed = new SpoolWriter(options(spool));
    expect(resumed.nextSequenceNumber).toBe(4);
    expect(resumed.restartRecordedAt).toBeDefined();
    resumed.append(envelope());
    resumed.close();
    expect(sequences(spool)).toEqual([4]);
  });

  it('ignores a tampered record with an unusable sequence_next', () => {
    for (const unsafe of [1e300, 2 ** 53, -1, 0, 1.5]) {
      const spool = tempSpool();
      const dir = join(spool, 'run-1', PRODUCER_ID);
      mkdirSync(dir, { recursive: true });
      writeFileSync(
        join(dir, '000001.jsonl'),
        [1, 2, 3].map((sequence) => `${JSON.stringify({ ...envelope(), sequence })}\n`).join(''),
      );
      writeFileSync(
        join(dir, SHARDS_FILE),
        `${JSON.stringify({ producer_id: PRODUCER_ID, sequence_next: unsafe })}\n`,
      );

      // PS8(b): the unusable value is dropped while reading, so the chunk scan decides.
      const resumed = new SpoolWriter(options(spool));
      expect(resumed.nextSequenceNumber).toBe(4);
      resumed.close();
      const record = JSON.parse(readFileSync(join(dir, SHARDS_FILE), 'utf8')) as {
        sequence_next: number;
      };
      expect(record.sequence_next).toBe(4);
    }
  });

  it('ignores an unreadable sequence in a chunk line instead of jumping to it (F4)', () => {
    const spool = tempSpool();
    const dir = join(spool, 'run-1', PRODUCER_ID);
    mkdirSync(dir, { recursive: true });
    // The last line the scan may trust carries sequence 3. The three complete lines after it
    // carry values no writer may resume after — a float past the safe range, a negative and
    // a fraction — and are unreadable exactly like a torn line, so the scan walks backwards.
    const valid = [1, 2, 3].map((sequence) => `${JSON.stringify({ ...envelope(), sequence })}\n`);
    writeFileSync(
      join(dir, '000001.jsonl'),
      `${valid.join('')}{"sequence":1e300}\n{"sequence":-7}\n{"sequence":2.5}\n`,
    );

    const resumed = new SpoolWriter(options(spool));
    // Not 1e300, not 0: the last safe integer `>= 1` decides (the same rule the record and
    // checkpoint readers apply), so the producer continues contiguously across the damage.
    expect(resumed.nextSequenceNumber).toBe(4);
    const minted = resumed.append(envelope());
    expect(minted.sequence).toBe(4);
    resumed.close();
    // The record carries the real next sequence, never the unusable one.
    expect(readFileSync(join(dir, SHARDS_FILE), 'utf8')).toContain('"sequence_next":5');
  });

  it('a crash before the first rotation leaves no record and still resumes from the chunks', () => {
    const spool = tempSpool();
    const crashed = crashedWriter(spool, { flushThreshold: 1, chunkMaxLines: 100 });
    for (let index = 0; index < 3; index += 1) crashed.append(envelope());
    // No rotation, no close: no record exists yet.
    expect(readdirSync(join(spool, 'run-1', PRODUCER_ID))).not.toContain(SHARDS_FILE);

    const resumed = new SpoolWriter(options(spool));
    expect(resumed.nextSequenceNumber).toBe(4);
    resumed.append(envelope());
    resumed.close();
    expect(sequences(spool)).toEqual([1, 2, 3, 4]);
  });

  it('never deletes a chunk at rotation (retention, D-S54-Q2)', () => {
    const spool = tempSpool();
    const writer = new SpoolWriter(options(spool, { flushThreshold: 1, chunkMaxLines: 2 }));
    for (let index = 0; index < 7; index += 1) writer.append(envelope());
    writer.close();

    const chunks = readdirSync(join(spool, 'run-1', PRODUCER_ID))
      .filter((name) => /^\d{6}\.jsonl$/.test(name))
      .sort();
    expect(chunks).toEqual(['000001.jsonl', '000002.jsonl', '000003.jsonl', '000004.jsonl']);
    expect(sequences(spool)).toEqual([1, 2, 3, 4, 5, 6, 7]);
  });
});
