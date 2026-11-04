// The spool edges the unit spec does not pin down: the exact cap boundary and the per-run
// cap, CR-only and raw-control torn input, cursor corruption, a kill between the two writes,
// and stale and live producer locks. Every root is temporary and nothing sleeps.
import { mkdir, readFile, stat, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

import { beforeEach, describe, expect, it } from 'vitest';

import {
  ackNothing,
  chunkFile,
  envelopeText,
  exactBytes,
  linesFrom,
  openSpool,
  producer,
  runId,
  useTempRoots,
} from './qa.spoolSend.testing.js';
import { SpoolCapExceededError, SpoolCorruptError, runDirBytes } from './spool.js';
import { collect } from './spool.testing.js';
import { SpoolLockedError, withSpoolLock } from './spoolLock.js';

const { tempDir } = useTempRoots();

describe('spool: cap boundary (04.2.3 AC3, contracts/cli.md §11.4)', () => {
  let spoolDir: string;

  beforeEach(async () => {
    spoolDir = await tempDir();
  });

  it('accepts a batch that lands exactly on the cap and refuses one byte less', async () => {
    const lines = linesFrom(1, 3);
    const exact = exactBytes(lines);

    const atCap = await openSpool(spoolDir, exact);
    await expect(atCap.append(lines)).resolves.toBeUndefined();
    expect(await runDirBytes(spoolDir, runId)).toBe(exact);

    const tight = await tempDir();
    const under = await openSpool(tight, exact - 1);
    await expect(under.append(lines)).rejects.toBeInstanceOf(SpoolCapExceededError);
    // Refused whole: nothing of the batch reached disk.
    expect(await runDirBytes(tight, runId)).toBe(0);
    expect(await collect(under.readUnacked())).toEqual([]);
  });

  it('refuses one more line once the exact cap is reached, leaving the first lines intact', async () => {
    const lines = linesFrom(1, 3);
    const spool = await openSpool(spoolDir, exactBytes(lines));
    await spool.append(lines);

    await expect(spool.append(linesFrom(4, 1))).rejects.toBeInstanceOf(SpoolCapExceededError);
    const seqs = (await collect(spool.readUnacked())).map((event) => event.sequence);
    expect(seqs).toEqual([1, 2, 3]);
  });

  it('shares the cap across producers of the same run (the cap is per run, not per producer)', async () => {
    const lines = linesFrom(1, 2);
    const cap = exactBytes(lines);
    const first = await openSpool(spoolDir, cap, 'pw-shard-0');
    await first.append(lines);

    const second = await openSpool(spoolDir, cap, 'pw-shard-1');
    await expect(
      second.append([envelopeText(1).replace(producer, 'pw-shard-1')]),
    ).rejects.toBeInstanceOf(SpoolCapExceededError);
  });
});

describe('spool: torn and corrupt input (contracts/cli.md §11.3)', () => {
  let spoolDir: string;

  beforeEach(async () => {
    spoolDir = await tempDir();
  });

  it('treats a bare CR between two envelopes as one unparseable line, never two events', async () => {
    const spool = await openSpool(spoolDir, 1_000_000);
    await spool.append([envelopeText(1)]);
    const path = await chunkFile(spoolDir);
    // Two envelopes joined by CR only: CR is not a line terminator (only LF is).
    await writeFile(path, `${envelopeText(1)}\r${envelopeText(2)}\n`);

    const reopened = await openSpool(spoolDir, 1_000_000);
    await expect(collect(reopened.readUnacked())).rejects.toBeInstanceOf(SpoolCorruptError);
  });

  it('rejects a raw CR inside a JSON string (control characters are invalid JSON)', async () => {
    const spool = await openSpool(spoolDir, 1_000_000);
    await spool.append([envelopeText(1)]);
    const path = await chunkFile(spoolDir);
    // Replace the type value with a literal CR byte inside the quotes (not the escape \r).
    const raw = envelopeText(1).replace('"run.log"', '"run\rlog"');
    await writeFile(path, `${raw}\n`);

    const reopened = await openSpool(spoolDir, 1_000_000);
    await expect(collect(reopened.readUnacked())).rejects.toBeInstanceOf(SpoolCorruptError);
  });

  it('refuses a sequence gap on disk: the reader reports spool_corrupt, never a silent skip', async () => {
    const spool = await openSpool(spoolDir, 1_000_000);
    await spool.append(linesFrom(1, 3));
    // Hand-write a gap (4 is missing) directly into the chunk; the reader must not skip it.
    const path = await chunkFile(spoolDir);
    const text = await readFile(path, 'utf8');
    await writeFile(path, `${text}${envelopeText(5)}\n`);

    const reopened = await openSpool(spoolDir, 1_000_000);
    await expect(collect(reopened.readUnacked())).rejects.toBeInstanceOf(SpoolCorruptError);
  });

  it.each([
    ['not JSON', '{not json'],
    ['an array', '[]'],
    ['next below 1', { next_unacked_sequence: 0, last_acked_sequence: -1, acked_count: 0 }],
    ['last != next - 1', { next_unacked_sequence: 3, last_acked_sequence: 1, acked_count: 2 }],
    ['fractional count', { next_unacked_sequence: 2, last_acked_sequence: 1, acked_count: 1.5 }],
    [
      'foreign producer',
      { producer_id: 'other', next_unacked_sequence: 2, last_acked_sequence: 1, acked_count: 1 },
    ],
  ])('refuses a cursor that is %s with spool_corrupt', async (_label, payload) => {
    const spool = await openSpool(spoolDir, 1_000_000);
    await spool.append(linesFrom(1, 3));
    const cursorPath = join(spoolDir, runId, producer, 'cursor.json');
    const body =
      typeof payload === 'string'
        ? payload
        : JSON.stringify({ producer_id: producer, updated_at: '2026-01-01T00:00:00Z', ...payload });
    await writeFile(cursorPath, body);

    const reopened = await openSpool(spoolDir, 1_000_000);
    await expect(reopened.cursor()).rejects.toBeInstanceOf(SpoolCorruptError);
  });

  it('a kill between the chunk write and the cursor write re-yields the unacked tail, no more', async () => {
    const spool = await openSpool(spoolDir, 1_000_000);
    await spool.append(linesFrom(1, 5));
    await spool.ack(2, 2);
    const cursorPath = join(spoolDir, runId, producer, 'cursor.json');
    const stale = await readFile(cursorPath, 'utf8');
    await spool.ack(4, 2);
    // Simulate the process dying after the ack's cursor rename was lost: put the old cursor back.
    await writeFile(cursorPath, stale);

    const restarted = await openSpool(spoolDir, 1_000_000);
    const seqs = (await collect(restarted.readUnacked())).map((event) => event.sequence);
    expect(seqs).toEqual([3, 4, 5]);
    expect(await restarted.cursor()).toMatchObject({
      nextUnackedSequence: 3,
      lastAckedSequence: 2,
      ackedCount: 2,
    });
  });
});

describe('spool: producer lock (contracts/cli.md §11.2)', () => {
  let spoolDir: string;

  beforeEach(async () => {
    spoolDir = await tempDir();
  });

  it('refuses a second writer while a live holder owns the producer directory', async () => {
    const dir = join(spoolDir, runId, producer);
    await mkdir(dir, { recursive: true });
    await withSpoolLock(dir, async () => {
      await expect(withSpoolLock(dir, ackNothing)).rejects.toBeInstanceOf(SpoolLockedError);
    });
  });

  it('records the holder pid and started_at, and creates the lock file 0600', async () => {
    const dir = join(spoolDir, runId, producer);
    await mkdir(dir, { recursive: true });
    await withSpoolLock(dir, async () => {
      const raw = await readFile(join(dir, 'spool.lock'), 'utf8');
      const parsed = JSON.parse(raw) as Record<string, unknown>;
      expect(Object.keys(parsed).sort()).toEqual(['pid', 'started_at']);
      expect(parsed['pid']).toBe(process.pid);
      expect((await stat(join(dir, 'spool.lock'))).mode & 0o777).toBe(0o600);
    });
  });

  it('takes over a stale lock whose holder is dead', async () => {
    const dir = join(spoolDir, runId, producer);
    await mkdir(dir, { recursive: true });
    await withSpoolLock(dir, ackNothing);
    // A lock left by a crashed writer: pid 4242 is named, and the probe reports it dead.
    await writeFile(
      join(dir, 'spool.lock'),
      JSON.stringify({ pid: 4242, started_at: '2026-01-01T00:00:00Z' }),
    );
    let ran = false;
    await withSpoolLock(
      dir,
      async () => {
        await Promise.resolve();
        ran = true;
      },
      { isProcessAlive: () => false },
    );
    expect(ran).toBe(true);
  });

  it('releases the lock when the body throws, so the next writer is not blocked', async () => {
    const dir = join(spoolDir, runId, producer);
    await mkdir(dir, { recursive: true });
    await expect(
      withSpoolLock(dir, async () => {
        await Promise.resolve();
        throw new Error('body failed');
      }),
    ).rejects.toThrow('body failed');
    await expect(withSpoolLock(dir, () => Promise.resolve('free'))).resolves.toBe('free');
  });
});
