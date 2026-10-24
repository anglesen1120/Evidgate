// QA 04.3.1-T1 (tier T2): spool boundary and crash-consistency probes. These are the
// cases the existing `spool.test.ts` / `sequence.test.ts` do not cover: a torn last line
// with no usable checkpoint, a corrupt checkpoint, path traversal via the ids, a
// pre-existing spool root with the wrong mode, and a symlinked spool root.
import {
  chmodSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  rmSync,
  statSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import { buildEnvelope, EXT_SCHEMA, type UnsignedEnvelope } from './envelope.js';
import { EvidgateInvalidSpoolIdError, SHARDS_FILE, SpoolWriter } from './spool.js';

const isWindows = process.platform === 'win32';
const tempDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-spool-boundary-'));
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

/** A line cut off mid-write, as a SIGKILL during `writeSync` leaves it. */
const TORN_LINE = '{"schema_version":1,"event_id":"01a11650-a2bb-7599-8fa4-071cbd857e12","pro';

function producerDir(spool: string, runId = 'run-1', producerId = 'pw-0'): string {
  return join(spool, runId, producerId);
}

describe('spool crash consistency', () => {
  it('does not reuse sequences when the first flush crashed mid-line (no record)', () => {
    const spool = tempSpool();
    const dir = producerDir(spool);
    mkdirSync(dir, { recursive: true });
    // The first flush wrote sequences 1..3 completely and was killed mid-line 4, before the
    // writer ever reached a rotation (so no shards.json exists at all). The three complete
    // lines are durable on disk.
    writeFileSync(
      join(dir, '000001.jsonl'),
      chunkLine('pw-0', 1) + chunkLine('pw-0', 2) + chunkLine('pw-0', 3) + TORN_LINE,
    );

    const writer = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
    // The highest sequence already on disk is 3, so the next append must be 4.
    expect(writer.nextSequenceNumber).toBe(4);
    // The torn fragment is truncated back to the last newline, so the next append cannot
    // concatenate onto it and corrupt the JSONL for the reader.
    const content = readFileSync(join(dir, '000001.jsonl'), 'utf8');
    expect(content.endsWith('\n')).toBe(true);
    expect(content).not.toContain(TORN_LINE);
    writer.close();
  });

  it('does not reuse sequences when the record is corrupt and the last line is torn', () => {
    const spool = tempSpool();
    const dir = producerDir(spool);
    mkdirSync(dir, { recursive: true });
    writeFileSync(
      join(dir, '000001.jsonl'),
      chunkLine('pw-0', 1) + chunkLine('pw-0', 2) + chunkLine('pw-0', 3) + TORN_LINE,
    );
    writeFileSync(join(dir, SHARDS_FILE), '{not json');

    const writer = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
    expect(writer.nextSequenceNumber).toBe(4);
    writer.close();
  });

  it('recovers from a corrupt shards.json when the chunk is intact', () => {
    const spool = tempSpool();
    const dir = producerDir(spool);
    mkdirSync(dir, { recursive: true });
    writeFileSync(
      join(dir, '000001.jsonl'),
      chunkLine('pw-0', 1) + chunkLine('pw-0', 2) + chunkLine('pw-0', 3),
    );
    writeFileSync(join(dir, SHARDS_FILE), '{not json');

    const writer = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
    expect(writer.nextSequenceNumber).toBe(4);
    writer.close();
  });

  it('ignores a record whose sequence_next is not a positive integer', () => {
    const spool = tempSpool();
    const dir = producerDir(spool);
    mkdirSync(dir, { recursive: true });
    writeFileSync(join(dir, '000001.jsonl'), chunkLine('pw-0', 1) + chunkLine('pw-0', 2));
    writeFileSync(
      join(dir, SHARDS_FILE),
      `${JSON.stringify({ producer_id: 'pw-0', shard_index: 1, sequence_next: 1.5 })}\n`,
    );

    const writer = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
    expect(writer.nextSequenceNumber).toBe(3);
    writer.close();
  });
});

describe('spool path containment', () => {
  it('rejects a run_id that would escape spool_dir, before writing anything', () => {
    const spool = tempSpool();
    const escapeName = `escape-${Date.now()}-${Math.random().toString(36).slice(2)}`;
    const escape = join(spool, '..', escapeName);
    expect(
      () => new SpoolWriter({ spoolDir: spool, runId: `../${escapeName}`, producerId: 'pw-0' }),
    ).toThrow(EvidgateInvalidSpoolIdError);
    expect(existsSync(escape)).toBe(false);
    expect(readdirSync(spool)).toEqual([]);
  });

  it('rejects a producer_id that would escape spool_dir, before writing anything', () => {
    const spool = tempSpool();
    const escapeName = `escape-${Date.now()}-${Math.random().toString(36).slice(2)}`;
    const escape = join(spool, '..', escapeName);
    expect(
      () => new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: `../../${escapeName}` }),
    ).toThrow(EvidgateInvalidSpoolIdError);
    expect(existsSync(escape)).toBe(false);
    expect(readdirSync(spool)).toEqual([]);
  });

  it('rejects empty, dot, separator, control and over-long ids', () => {
    const spool = tempSpool();
    const invalid = ['', '.', '..', 'a/b', 'a\\b', 'a\u0000b', 'a\nb', 'x'.repeat(129)];
    for (const id of invalid) {
      expect(() => new SpoolWriter({ spoolDir: spool, runId: id, producerId: 'pw-0' })).toThrow(
        EvidgateInvalidSpoolIdError,
      );
      expect(() => new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: id })).toThrow(
        EvidgateInvalidSpoolIdError,
      );
    }
    expect(readdirSync(spool)).toEqual([]);
  });

  it('accepts an id at the 128-character limit', () => {
    const spool = tempSpool();
    const runId = 'x'.repeat(128);
    const writer = new SpoolWriter({ spoolDir: spool, runId, producerId: 'pw-0' });
    expect(writer.directory).toBe(join(spool, runId, 'pw-0'));
    writer.close();
  });
});

describe('spool directory modes', () => {
  it('creates its own directories 0700 even when the spool root pre-exists 0755', () => {
    if (isWindows) return; // Windows has no POSIX mode bits.
    const spool = tempSpool();
    chmodSync(spool, 0o755);

    const writer = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
    writer.append(envelope('pw-0'));
    writer.close();

    expect(statSync(join(spool, 'run-1')).mode & 0o777).toBe(0o700);
    expect(statSync(producerDir(spool)).mode & 0o777).toBe(0o700);
    expect(statSync(join(producerDir(spool), '000001.jsonl')).mode & 0o777).toBe(0o600);
  });

  it('writes through a symlinked spool_dir', () => {
    if (isWindows) return; // symlink creation needs privileges on Windows.
    const root = tempSpool();
    const real = join(root, 'real');
    mkdirSync(real, { recursive: true });
    const link = join(root, 'link');
    symlinkSync(real, link);

    const writer = new SpoolWriter({ spoolDir: link, runId: 'run-1', producerId: 'pw-0' });
    writer.append(envelope('pw-0'));
    writer.close();

    expect(existsSync(join(real, 'run-1', 'pw-0', '000001.jsonl'))).toBe(true);
    const line = readFileSync(join(real, 'run-1', 'pw-0', '000001.jsonl'), 'utf8').trim();
    expect(JSON.parse(line)).toMatchObject({ sequence: 1 });
  });
});
