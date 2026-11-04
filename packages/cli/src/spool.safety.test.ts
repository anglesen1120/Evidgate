// The spool's on-disk safety: the 0600/0700 modes every file and directory gets, ids that
// would escape their directory, a symlinked producer directory, purge (including a
// directory swapped for a symlink under it), and the proof that no credential material
// reaches a spool file or its lock/cursor files.
import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import {
  mkdir,
  readFile,
  readdir,
  rename,
  stat,
  symlink,
  unlink,
  writeFile,
} from 'node:fs/promises';
import { join } from 'node:path';
import { beforeEach, describe, expect, it } from 'vitest';

import {
  InvalidSpoolIdError,
  SpoolMissingError,
  assertValidSpoolId,
  listProducers,
  runDirBytes,
  spoolCursorFileName,
  spoolDirMode,
  spoolFileMode,
  spoolIdMaxLength,
} from './spool.js';
import { spoolLockFileName, writerLockFileName } from './spoolLock.js';
import {
  allFiles,
  collect,
  expectError,
  failureOf,
  makeEvents,
  modeOf,
  producerId,
  runId,
  useSpoolFixture,
} from './spool.testing.js';

describe('ProducerSpool', () => {
  const fixture = useSpoolFixture();
  const { open } = fixture;

  let root: string;
  let spoolDir: string;

  beforeEach(() => {
    root = fixture.root;
    spoolDir = fixture.spoolDir;
  });

  it('file_modes: dirs 0700 and every file 0600', async () => {
    if (process.platform === 'win32') {
      // Windows has no POSIX mode bits; the AC skips this check there.
      return;
    }
    const spool = await open();
    await spool.append(makeEvents([1]).map((event) => event.line));
    await spool.ack(1, 1);
    await spool.withLock(async () => {
      expect(await modeOf(join(spool.directory, spoolLockFileName))).toBe(spoolFileMode);
    });
    expect(await modeOf(spool.directory)).toBe(spoolDirMode);
    expect(await modeOf(spool.runDirectory)).toBe(spoolDirMode);
    expect(await modeOf(join(spool.directory, '000001.jsonl'))).toBe(spoolFileMode);
    expect(await modeOf(join(spool.directory, spoolCursorFileName))).toBe(spoolFileMode);
  });

  it('rejects path-traversal run and producer ids', async () => {
    const invalid = ['', '.', '..', 'a/b', 'a\\b', 'bad\u0000id', 'x'.repeat(spoolIdMaxLength + 1)];
    for (const value of invalid) {
      expect(() => assertValidSpoolId('run_id', value)).toThrow(InvalidSpoolIdError);
      expect(() => assertValidSpoolId('producer_id', value)).toThrow(InvalidSpoolIdError);
    }
    // The writer's own charset and length boundary stays accepted.
    expect(() => assertValidSpoolId('producer_id', 'a.b_c-1')).not.toThrow();
    expect(() => assertValidSpoolId('producer_id', 'x'.repeat(spoolIdMaxLength))).not.toThrow();

    for (const overrides of [{ runId: '..' }, { producerId: '../escape' }] as const) {
      const failure = expectError(await failureOf(open(overrides)), InvalidSpoolIdError);
      expect(failure.code).toBe('invalid_spool_id');
    }
    // Nothing escaped: the refused opens created no directory at all.
    expect(await readdir(spoolDir)).toEqual([]);
  });

  it('refuses a symlinked producer directory', async () => {
    const victim = join(root, 'victim');
    await mkdir(victim);
    const runDirectory = join(spoolDir, runId);
    await mkdir(runDirectory, { recursive: true });
    await symlink(victim, join(runDirectory, producerId));

    const failure = await failureOf(open());
    expect(failure).toMatchObject({ code: 'spool_unsafe_path' });
    // The link itself is untouched and still points at the victim.
    expect((await readdir(victim)).length).toBe(0);
  });

  it('writes no credential material into any spool file', async () => {
    // A JWT-shaped value and a token, assembled at runtime: no literal secret in the spec.
    const secret = ['evg', 'tok', randomUUID().replace(/-/g, '')].join('_');
    const jwt = [
      ['eyJ', 'hbGciOi', 'JIUzI1NiJ9'].join(''),
      ['eyJ', 'zdWIiOiJ0ZXN0In0'].join(''),
      'c2lnbmF0dXJlLXZhbHVl',
    ].join('.');

    const spool = await open();
    await spool.append(makeEvents([1, 2]).map((event) => event.line));
    await spool.ack(2, 2);
    await spool.withLock(async () => {
      const lockText = await readFile(join(spool.directory, spoolLockFileName), 'utf8');
      // Exactly `{ pid, started_at }`, so no credential field can hide in the lock.
      expect([...lockText.matchAll(/"([a-z_]+)":/gu)].map((match) => match[1])).toEqual([
        'pid',
        'started_at',
      ]);
    });

    const cursor: unknown = JSON.parse(
      await readFile(join(spool.directory, spoolCursorFileName), 'utf8'),
    );
    if (typeof cursor !== 'object' || cursor === null) {
      throw new Error('cursor.json is not an object');
    }
    expect(Object.keys(cursor).sort()).toEqual([
      'acked_count',
      'last_acked_sequence',
      'next_unacked_sequence',
      'producer_id',
      'updated_at',
    ]);

    for (const file of await allFiles(spoolDir)) {
      const text = await readFile(file, 'utf8');
      expect(text).not.toContain(secret);
      expect(text).not.toContain(jwt);
      expect(text).not.toMatch(/Bearer|https:\/\/[^\s:@/]+:[^\s@/]+@/u);
    }
  });

  it('purge removes the producer directory and leaves the run directory', async () => {
    const spool = await open();
    await spool.append(makeEvents([1]).map((event) => event.line));
    await spool.purge();
    expect(await failureOf(stat(spool.directory))).toMatchObject({ code: 'ENOENT' });
    expect((await stat(spool.runDirectory)).isDirectory()).toBe(true);

    const missing = await failureOf(open({ createIfMissing: false }));
    expect(missing).toBeInstanceOf(SpoolMissingError);
    expect(missing).toMatchObject({ code: 'spool_missing', producerId });
    expect(await failureOf(open({ createIfMissing: false, producerId: 'other' }))).toBeInstanceOf(
      SpoolMissingError,
    );
  });

  it('drains and purges a producer dir that also holds shards.json, uploads/ and writer.lock', async () => {
    const spool = await open();
    await spool.append(makeEvents([1, 2]).map((event) => event.line));
    // The reporter's sparse record, its writer lock and the CLI's upload journal live beside
    // the chunks: none of them is a chunk, so a drain reads only the chunks and a purge
    // removes the whole producer directory.
    const shardsText = `${JSON.stringify({
      producer_id: producerId,
      shard_index: 1,
      shard_total: 1,
      sequence_next: 3,
    })}\n`;
    await writeFile(join(spool.directory, 'shards.json'), shardsText);
    await mkdir(join(spool.directory, 'uploads'), { mode: 0o700 });
    await writeFile(
      join(spool.directory, 'uploads', `${randomUUID()}.json`),
      '{"artifact_id":"x"}\n',
    );
    await writeFile(
      join(spool.directory, writerLockFileName),
      `${JSON.stringify({ pid: 4242, started_at: '2026-10-23T00:00:00.000Z' })}\n`,
      { mode: spoolFileMode },
    );

    const drained: number[] = [];
    for await (const event of spool.readUnacked()) drained.push(event.sequence);
    expect(drained).toEqual([1, 2]);
    await spool.ack(2, 2);
    expect(await readFile(join(spool.directory, 'shards.json'), 'utf8')).toBe(shardsText);

    await spool.purge();
    expect(await failureOf(stat(spool.directory))).toMatchObject({ code: 'ENOENT' });
  });

  it('lists producer ids and sums the run directory bytes without following symlinks', async () => {
    const first = await open({ producerId: 'b-producer' });
    const second = await open({ producerId: 'a-producer' });
    await first.append(makeEvents([1]).map((event) => event.line));
    await second.append(makeEvents([1]).map((event) => event.line));
    // Neither a foreign-named directory nor a symlink is a producer the CLI will send.
    await mkdir(join(first.runDirectory, 'bad name'));
    await symlink(first.directory, join(first.runDirectory, 'linked-producer'));

    expect(await listProducers(spoolDir, runId)).toEqual(['a-producer', 'b-producer']);
    const expected =
      (await stat(join(first.directory, '000001.jsonl'))).size +
      (await stat(join(second.directory, '000001.jsonl'))).size;
    expect(await runDirBytes(spoolDir, runId)).toBe(expected);
    expect(await runDirBytes(spoolDir, 'no-such-run')).toBe(0);
    expect(await listProducers(spoolDir, 'no-such-run')).toEqual([]);
  });

  // Review P3 "a FIFO at a spool path hangs the CLI": the open is O_NONBLOCK, so a
  // non-regular file is rejected by the descriptor check instead of blocking the process.
  it('refuses a FIFO planted at a spool file instead of blocking on it', async () => {
    if (process.platform === 'win32') return; // mkfifo/O_NONBLOCK are POSIX-only
    const spool = await open();
    const fifo = join(spool.directory, '000001.jsonl');
    execFileSync('mkfifo', [fifo]);

    const chunkFailure = await failureOf(collect(spool.readUnacked()));
    expect(chunkFailure).toMatchObject({ code: 'spool_unsafe_path' });

    // The CLI-owned cursor is opened the same way.
    await unlink(fifo);
    execFileSync('mkfifo', [join(spool.directory, spoolCursorFileName)]);
    expect(await failureOf(spool.cursor())).toMatchObject({ code: 'spool_unsafe_path' });
  });

  // Review P3 "directory checks are path-based (TOCTOU)": purge re-checks both directories
  // right before `rm -r`, so a directory swapped for a symlink is never deleted through.
  it('purge refuses a run or producer directory swapped for a symlink', async () => {
    if (process.platform === 'win32') return; // symlinks need privileges on Windows
    const spool = await open();
    await spool.append(makeEvents([1]).map((event) => event.line));
    const victim = join(root, 'victim');
    await mkdir(victim);
    await writeFile(join(victim, 'keep.txt'), 'keep', { mode: spoolFileMode });

    // The producer directory becomes a symlink to the victim tree.
    const stashedProducer = join(root, 'stashed-producer');
    await rename(spool.directory, stashedProducer);
    await symlink(victim, spool.directory);
    expect(await failureOf(spool.purge())).toMatchObject({ code: 'spool_unsafe_path' });
    expect(await readFile(join(victim, 'keep.txt'), 'utf8')).toBe('keep');

    // Restore, then the run directory becomes the symlink: still refused, still nothing
    // deleted from the target.
    await unlink(spool.directory);
    await rename(stashedProducer, spool.directory);
    await writeFile(join(victim, 'run.txt'), 'run-keep', { mode: spoolFileMode });
    const stashedRun = join(root, 'stashed-run');
    await rename(spool.runDirectory, stashedRun);
    await symlink(victim, spool.runDirectory);
    expect(await failureOf(spool.purge())).toMatchObject({ code: 'spool_unsafe_path' });
    expect(await readFile(join(victim, 'run.txt'), 'utf8')).toBe('run-keep');
    expect(await readFile(join(victim, 'keep.txt'), 'utf8')).toBe('keep');
    expect((await stat(stashedRun)).isDirectory()).toBe(true);
  });
});
