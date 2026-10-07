// 04.3.1-T1.1: sequence contiguity and restart (AC4, T84). A restarted producer keeps
// its id and resumes after the checkpointed sequence; a sequence is never reused.
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import { buildEnvelope, EXT_SCHEMA, type UnsignedEnvelope } from './envelope.js';
import { CHECKPOINT_FILE, SpoolWriter } from './spool.js';

const tempDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-seq-'));
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

function chunkLine(producerId: string, sequence: number): string {
  return `${JSON.stringify({ ...envelope(producerId), sequence })}\n`;
}

describe('per-producer sequence', () => {
  it('starts at 1 and is contiguous', () => {
    const spool = tempSpool();
    const writer = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
    const written = writer.appendMany([envelope('pw-0'), envelope('pw-0'), envelope('pw-0')]);
    writer.close();

    expect(written.map((item) => item.sequence)).toEqual([1, 2, 3]);
    const checkpoint: unknown = JSON.parse(
      readFileSync(join(spool, 'run-1', 'pw-0', CHECKPOINT_FILE), 'utf8'),
    );
    expect(checkpoint).toEqual({ producer_id: 'pw-0', next_sequence: 4 });
  });

  it('resumes at the checkpoint after a clean close (no reuse)', () => {
    const spool = tempSpool();
    const first = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
    first.appendMany([envelope('pw-0'), envelope('pw-0')]);
    first.close();

    const second = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
    expect(second.nextSequenceNumber).toBe(3);
    const resumed = second.append(envelope('pw-0'));
    second.close();

    expect(resumed.sequence).toBe(3);
  });

  it('reconciles from the chunk file when the checkpoint is missing (crash before rename)', () => {
    const spool = tempSpool();
    const dir = join(spool, 'run-1', 'pw-0');
    mkdirSync(dir, { recursive: true });
    writeFileSync(
      join(dir, '000001.jsonl'),
      chunkLine('pw-0', 1) + chunkLine('pw-0', 2) + chunkLine('pw-0', 3),
    );

    const writer = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
    expect(writer.nextSequenceNumber).toBe(4);
    writer.close();
  });

  it('takes the max of the checkpoint and the chunk contents', () => {
    const spool = tempSpool();
    const dir = join(spool, 'run-1', 'pw-0');
    mkdirSync(dir, { recursive: true });
    writeFileSync(
      join(dir, '000001.jsonl'),
      chunkLine('pw-0', 1) + chunkLine('pw-0', 2) + chunkLine('pw-0', 3) + chunkLine('pw-0', 4),
    );
    writeFileSync(
      join(dir, CHECKPOINT_FILE),
      `${JSON.stringify({ producer_id: 'pw-0', next_sequence: 2 })}\n`,
    );

    const writer = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
    expect(writer.nextSequenceNumber).toBe(5);
    writer.close();
  });

  it('keeps an independent sequence space per producer', () => {
    const spool = tempSpool();
    const first = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
    const second = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-1' });
    first.appendMany([envelope('pw-0'), envelope('pw-0')]);
    const secondWritten = second.appendMany([envelope('pw-1')]);
    first.close();
    second.close();

    expect(secondWritten.map((item) => item.sequence)).toEqual([1]);
  });
});
