// 04.3.1-T1.1: spool layout, modes and rotation (task file §4, 04 §8 S3 row).
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import { buildEnvelope, EXT_SCHEMA, type UnsignedEnvelope } from './envelope.js';
import { CHECKPOINT_FILE, CHECKPOINT_TMP_FILE, SpoolWriter } from './spool.js';

const isWindows = process.platform === 'win32';
const tempDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-spool-'));
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

function chunkFiles(dir: string): string[] {
  return readdirSync(dir)
    .filter((entry) => entry.endsWith('.jsonl'))
    .sort();
}

describe('spool layout', () => {
  it('writes <spool_dir>/<run_id>/<producer_id>/NNNNNN.jsonl', () => {
    const spool = tempSpool();
    const writer = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
    writer.append(envelope('pw-0'));
    writer.close();

    const dir = join(spool, 'run-1', 'pw-0');
    expect(chunkFiles(dir)).toEqual(['000001.jsonl']);
    expect(existsSync(join(dir, CHECKPOINT_FILE))).toBe(true);
    const lines = readFileSync(join(dir, '000001.jsonl'), 'utf8').trim().split('\n');
    expect(lines).toHaveLength(1);
    // The spool line is our own JSON; parse it into the envelope shape for the read.
    const first = JSON.parse(lines[0] ?? '{}') as { sequence?: number };
    expect(first.sequence).toBe(1);
  });

  it('creates the directory 0700 and the files 0600', () => {
    if (isWindows) return; // Windows has no POSIX mode bits.
    const spool = tempSpool();
    const writer = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
    writer.append(envelope('pw-0'));
    writer.close();

    const dir = join(spool, 'run-1', 'pw-0');
    expect(statSync(dir).mode & 0o777).toBe(0o700);
    expect(statSync(join(dir, '000001.jsonl')).mode & 0o777).toBe(0o600);
    expect(statSync(join(dir, CHECKPOINT_FILE)).mode & 0o777).toBe(0o600);
  });

  it('rotates chunks at chunkMaxLines', () => {
    const spool = tempSpool();
    const writer = new SpoolWriter({
      spoolDir: spool,
      runId: 'run-1',
      producerId: 'pw-0',
      chunkMaxLines: 2,
    });
    writer.appendMany([
      envelope('pw-0'),
      envelope('pw-0'),
      envelope('pw-0'),
      envelope('pw-0'),
      envelope('pw-0'),
    ]);
    writer.close();

    expect(chunkFiles(join(spool, 'run-1', 'pw-0'))).toEqual([
      '000001.jsonl',
      '000002.jsonl',
      '000003.jsonl',
    ]);
  });

  it('leaves no checkpoint tmp file behind', () => {
    const spool = tempSpool();
    const writer = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
    writer.appendMany([envelope('pw-0'), envelope('pw-0')]);
    writer.close();

    expect(existsSync(join(spool, 'run-1', 'pw-0', CHECKPOINT_TMP_FILE))).toBe(false);
  });

  it('buffers below the flush threshold until flush', () => {
    const spool = tempSpool();
    const writer = new SpoolWriter({
      spoolDir: spool,
      runId: 'run-1',
      producerId: 'pw-0',
      flushThreshold: 10,
    });
    writer.append(envelope('pw-0'));
    expect(existsSync(join(spool, 'run-1', 'pw-0', CHECKPOINT_FILE))).toBe(false);

    writer.flush();
    expect(existsSync(join(spool, 'run-1', 'pw-0', CHECKPOINT_FILE))).toBe(true);
    writer.close();
  });
});
