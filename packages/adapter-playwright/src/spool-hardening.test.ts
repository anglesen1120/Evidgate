// 04.3.1-T1 (tier T2) hardening review fixes (CR4, PS3, PS4, PS8, PS9): symlink and mode
// hardening on the spool files and directories, a descriptor that is always released,
// short/failed writes that never leave a torn line, and stricter id/checkpoint parsing.
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
import type { writeSync as WriteSyncFn } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it, vi } from 'vitest';

import { buildEnvelope, EXT_SCHEMA, type UnsignedEnvelope } from './envelope.js';
import {
  CHECKPOINT_FILE,
  CHECKPOINT_TMP_FILE,
  EvidgateInvalidSpoolIdError,
  SpoolWriter,
} from './spool.js';

// PS4: a controllable short write. `vi.mock` is hoisted, so the flag lives in `vi.hoisted`.
const fsControl = vi.hoisted(() => ({ failNextWrite: false }));

vi.mock('node:fs', async (importOriginal) => {
  const actual = await importOriginal<Record<string, unknown>>();
  const realWriteSync = actual.writeSync as typeof WriteSyncFn;
  const writeSync = ((
    fd: number,
    buffer: NodeJS.ArrayBufferView,
    offset?: number,
    length?: number,
    position?: number,
  ): number => {
    if (fsControl.failNextWrite) {
      fsControl.failNextWrite = false;
      // Write part of the line for real, then fail, exactly as a short write followed by
      // ENOSPC/EIO would: a torn fragment is left mid-chunk.
      const total = length ?? buffer.byteLength;
      const partial = Math.max(1, Math.floor(total / 2));
      realWriteSync(fd, buffer, offset ?? 0, partial);
      throw new Error('simulated short write');
    }
    return realWriteSync(fd, buffer, offset, length, position);
  }) as unknown as typeof WriteSyncFn;
  return { ...actual, writeSync };
});

const isWindows = process.platform === 'win32';
const tempDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-spool-hardening-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  fsControl.failNextWrite = false;
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

function producerDir(spool: string, runId = 'run-1', producerId = 'pw-0'): string {
  return join(spool, runId, producerId);
}

/** Open descriptors of the current process, or `null` where the platform has no fd dir. */
function openFdCount(): number | null {
  const dir = process.platform === 'darwin' ? '/dev/fd' : '/proc/self/fd';
  try {
    return readdirSync(dir).length;
  } catch {
    return null;
  }
}

describe('spool symlink and mode hardening', () => {
  it('does not follow a planted checkpoint.json.tmp symlink', () => {
    if (isWindows) return;
    const spool = tempSpool();
    const dir = producerDir(spool);
    mkdirSync(dir, { recursive: true });
    const victim = join(spool, 'victim.txt');
    writeFileSync(victim, 'do not touch');
    symlinkSync(victim, join(dir, CHECKPOINT_TMP_FILE));

    const writer = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
    writer.append(envelope('pw-0'));
    writer.close();

    expect(readFileSync(victim, 'utf8')).toBe('do not touch');
    expect(existsSync(join(dir, CHECKPOINT_TMP_FILE))).toBe(false);
    expect(JSON.parse(readFileSync(join(dir, CHECKPOINT_FILE), 'utf8'))).toMatchObject({
      producer_id: 'pw-0',
      next_sequence: 2,
    });
  });

  it('rejects a planted chunk symlink instead of following it', () => {
    if (isWindows) return;
    const spool = tempSpool();
    const dir = producerDir(spool);
    mkdirSync(dir, { recursive: true });
    const victim = join(spool, 'victim.txt');
    writeFileSync(victim, 'do not touch');
    symlinkSync(victim, join(dir, '000001.jsonl'));

    expect(
      () => new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' }),
    ).toThrow();
    expect(readFileSync(victim, 'utf8')).toBe('do not touch');
  });

  it('forces a pre-existing 0644 chunk to 0600', () => {
    if (isWindows) return;
    const spool = tempSpool();
    const dir = producerDir(spool);
    mkdirSync(dir, { recursive: true });
    const chunk = join(dir, '000001.jsonl');
    writeFileSync(chunk, chunkLine('pw-0', 1));
    chmodSync(chunk, 0o644);

    const writer = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
    writer.append(envelope('pw-0'));
    writer.close();

    expect(statSync(chunk).mode & 0o777).toBe(0o600);
  });

  it('rejects a symlinked run_id directory', () => {
    if (isWindows) return;
    const spool = tempSpool();
    const real = join(spool, 'real-run');
    mkdirSync(real, { recursive: true });
    symlinkSync(real, join(spool, 'run-1'));

    expect(
      () => new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' }),
    ).toThrow();
  });
});

describe('spool write-failure hardening', () => {
  it('releases the chunk descriptor when the final flush throws', () => {
    if (isWindows) return;
    const spool = tempSpool();
    const before = openFdCount();
    const writer = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
    writer.append(envelope('pw-0'));
    // A directory at the tmp path makes the checkpoint write fail (unlink and O_EXCL open).
    mkdirSync(join(producerDir(spool), CHECKPOINT_TMP_FILE));

    expect(() => writer.close()).toThrow();
    if (before !== null) expect(openFdCount()).toBe(before);
    // `closed` is set, so a second close is a no-op and append is rejected.
    expect(() => writer.close()).not.toThrow();
    expect(() => writer.append(envelope('pw-0'))).toThrow(/closed/);
  });

  it('keeps unwritten envelopes and leaves a clean chunk when a mid-batch rotate fails', () => {
    const spool = tempSpool();
    const writer = new SpoolWriter({
      spoolDir: spool,
      runId: 'run-1',
      producerId: 'pw-0',
      chunkMaxLines: 1,
    });
    writer.append(envelope('pw-0'));
    // Chunk 2 cannot be created: a directory sits where the file must go.
    mkdirSync(join(producerDir(spool), '000002.jsonl'));
    writer.append(envelope('pw-0'));

    expect(() => writer.flush()).toThrow();
    const chunk1 = readFileSync(join(producerDir(spool), '000001.jsonl'), 'utf8');
    expect(chunk1.endsWith('\n')).toBe(true);
    expect(chunk1.trim().split('\n')).toHaveLength(1);
    // The envelope that never reached a chunk is still queued, not silently dropped.
    expect(writer.pendingCount).toBe(1);

    // CR4/PS4: the failed rotate restored the previous chunk, so once the obstacle is
    // gone a later flush writes the queued envelope instead of failing forever.
    rmSync(join(producerDir(spool), '000002.jsonl'), { recursive: true });
    writer.flush();

    expect(writer.pendingCount).toBe(0);
    const chunk2 = readFileSync(join(producerDir(spool), '000002.jsonl'), 'utf8');
    expect(chunk2.trim().split('\n')).toHaveLength(1);
    // The queued envelope lands with its original sequence, and the first envelope is not
    // duplicated: chunk 1 still holds exactly sequence 1.
    expect(JSON.parse(chunk2.trim())).toMatchObject({ sequence: 2 });
    const chunk1After = readFileSync(join(producerDir(spool), '000001.jsonl'), 'utf8');
    expect(chunk1After.trim().split('\n')).toHaveLength(1);
    expect(JSON.parse(chunk1After.trim())).toMatchObject({ sequence: 1 });
  });

  it('rolls a short write back to the last complete line', () => {
    const spool = tempSpool();
    const writer = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
    writer.append(envelope('pw-0'));
    writer.flush();
    const chunk = join(producerDir(spool), '000001.jsonl');
    const good = readFileSync(chunk, 'utf8');
    expect(good.endsWith('\n')).toBe(true);

    fsControl.failNextWrite = true;
    writer.append(envelope('pw-0'));
    expect(() => writer.flush()).toThrow(/simulated short write/);

    // The half-written fragment was truncated away: the chunk is byte-identical to the
    // last good state and still ends at a line boundary.
    expect(readFileSync(chunk, 'utf8')).toBe(good);
    expect(writer.pendingCount).toBe(1);
  });
});

describe('spool id and checkpoint hardening', () => {
  it('rejects an id containing ":"', () => {
    const spool = tempSpool();
    expect(
      () => new SpoolWriter({ spoolDir: spool, runId: 'run:stream', producerId: 'pw-0' }),
    ).toThrow(EvidgateInvalidSpoolIdError);
    expect(() => new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw:0' })).toThrow(
      EvidgateInvalidSpoolIdError,
    );
    expect(readdirSync(spool)).toEqual([]);
  });

  it('ignores an unsafe checkpoint next_sequence', () => {
    for (const unsafe of [1e300, 2 ** 53]) {
      const spool = tempSpool();
      const dir = producerDir(spool);
      mkdirSync(dir, { recursive: true });
      writeFileSync(join(dir, '000001.jsonl'), chunkLine('pw-0', 1));
      writeFileSync(
        join(dir, CHECKPOINT_FILE),
        `${JSON.stringify({ producer_id: 'pw-0', next_sequence: unsafe })}\n`,
      );

      const writer = new SpoolWriter({ spoolDir: spool, runId: 'run-1', producerId: 'pw-0' });
      // The unsafe value is ignored, so the chunk scan wins: the next sequence is 2.
      expect(writer.nextSequenceNumber).toBe(2);
      writer.close();
    }
  });

  it('still accepts the ids the existing tests use', () => {
    const spool = tempSpool();
    for (const id of ['pw-0', 'run-1', '01a11650-a2bb-7599-8fa4-071cbd857e12', 'x'.repeat(128)]) {
      const writer = new SpoolWriter({ spoolDir: spool, runId: id, producerId: 'pw-0' });
      expect(writer.directory).toBe(join(spool, id, 'pw-0'));
      writer.close();
    }
  });
});
