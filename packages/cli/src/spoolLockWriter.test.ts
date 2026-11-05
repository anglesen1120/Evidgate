// writer.lock is the reporter's side: the CLI reads it and never writes it, never follows
// a symlink or FIFO planted at it, and refuses the chunk paths while a live reporter works
// while leaving that file untouched.

import { randomUUID } from 'node:crypto';
import {
  chmod,
  lstat,
  mkdir,
  mkdtemp,
  readFile,
  readdir,
  rm,
  stat,
  symlink,
  writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { CliError } from './errors.js';
import { ProducerSpool, defaultSpoolMaxBytes } from './spool.js';
import {
  WriterLockedError,
  assertNoLiveWriter,
  readSpoolLock,
  readWriterLock,
  spoolLockFileMode,
  writerLockFileName,
} from './spoolLock.js';
import { deadPid, fixedIso, makeFifo, writerLockRecord } from './spoolLock.testing.js';
import { expectError, failureOf } from './spool.testing.js';
import { UploadJournal } from './uploadJournal.js';

describe('writer.lock — the reporter side (D-S56-Q8)', () => {
  let root: string;
  let directory: string;
  let lockPath: string;

  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), 'evidgate-writer-lock-'));
    directory = join(root, 'producer');
    await mkdir(directory, { recursive: true, mode: 0o700 });
    lockPath = join(directory, writerLockFileName);
  });

  afterEach(async () => {
    await rm(root, { recursive: true, force: true });
  });

  async function writeWriterLock(pid: number): Promise<void> {
    await writeFile(lockPath, writerLockRecord(pid), { mode: spoolLockFileMode });
  }

  it('passes when the reporter holds no lock', async () => {
    await assertNoLiveWriter(directory);
    expect(await readWriterLock(directory)).toBeNull();
    expect(await readdir(directory)).toEqual([]);
  });

  it('reads the reporter record and never writes one', async () => {
    await writeWriterLock(4242);
    expect(await readWriterLock(directory)).toEqual({ pid: 4242, startedAt: fixedIso });
    // Reading is all the CLI may do here: the lock is the reporter's, not ours.
    expect(await readdir(directory)).toEqual([writerLockFileName]);
    expect((await stat(lockPath)).mode & 0o777).toBe(spoolLockFileMode);
  });

  it('refuses with spool_locked while a live reporter holds it, naming the pid', async () => {
    await writeWriterLock(4242);
    const failure = await failureOf(assertNoLiveWriter(directory, { isProcessAlive: () => true }));
    const locked = expectError(failure, WriterLockedError);
    expect(locked.code).toBe('spool_locked');
    expect(locked.holderPid).toBe(4242);
    expect(locked.lockPath).toBe(lockPath);
    expect(locked.message).toContain('4242');
    expect(locked.hint).toContain('the reporter is still writing this producer');
    // Never waited for, never taken over, never deleted: the lock is byte-identical.
    expect(await readFile(lockPath, 'utf8')).toBe(writerLockRecord(4242));
    expect(await readdir(directory)).toEqual([writerLockFileName]);
  });

  it('refuses a lock naming this very process with the real liveness probe', async () => {
    await writeWriterLock(process.pid);
    const failure = await failureOf(assertNoLiveWriter(directory));
    expect(expectError(failure, WriterLockedError).holderPid).toBe(process.pid);
    expect(await readFile(lockPath, 'utf8')).toBe(writerLockRecord(process.pid));
  });

  it('ignores a dead holder and leaves its lock exactly in place', async () => {
    await writeWriterLock(deadPid);
    // The CLI never renames or unlinks a reporter lock: the reporter's own next start
    // performs the verified takeover, so a dead lock here can never become a live delete.
    await assertNoLiveWriter(directory, { isProcessAlive: () => false, probeDelayMs: 1 });
    expect(await readFile(lockPath, 'utf8')).toBe(writerLockRecord(deadPid));
    expect(await readdir(directory)).toEqual([writerLockFileName]);
  });

  it('re-reads an unparseable lock once, then treats it as absent', async () => {
    await writeFile(lockPath, '{"pid":', { mode: spoolLockFileMode });
    await assertNoLiveWriter(directory, { isProcessAlive: () => true, probeDelayMs: 1 });
    expect(await readFile(lockPath, 'utf8')).toBe('{"pid":');
    expect(await readdir(directory)).toEqual([writerLockFileName]);
  });

  it('never follows a symlink planted at writer.lock', async () => {
    // A followed link would report the reporter as live and refuse a healthy spool.
    const target = join(root, 'elsewhere.lock');
    await writeFile(target, writerLockRecord(4242), { mode: spoolLockFileMode });
    await symlink(target, lockPath);
    await assertNoLiveWriter(directory, { isProcessAlive: () => true, probeDelayMs: 1 });
    expect((await lstat(lockPath)).isSymbolicLink()).toBe(true);
    expect(await readFile(target, 'utf8')).toBe(writerLockRecord(4242));
  });

  it('treats a FIFO planted at writer.lock as "no lock" without blocking', async () => {
    if (process.platform === 'win32') return;
    await makeFifo(lockPath);
    const outcome = await Promise.race([
      assertNoLiveWriter(directory, { probeDelayMs: 1 }).then(() => 'passed'),
      delay(500).then(() => 'blocked'),
    ]);
    expect(outcome).toBe('passed');
    expect((await lstat(lockPath)).isFIFO()).toBe(true);
  });

  it('treats an oversized writer.lock as "no lock"', async () => {
    await writeFile(lockPath, 'x'.repeat(2048), { mode: spoolLockFileMode });
    await assertNoLiveWriter(directory, { probeDelayMs: 1 });
    expect((await stat(lockPath)).size).toBe(2048);
  });

  it('fails with spool_unsafe_path instead of reading a lock it cannot inspect', async () => {
    // Root ignores file modes, so the permission check below would not fire.
    if (process.platform === 'win32' || process.getuid?.() === 0) return;
    await writeWriterLock(deadPid);
    await chmod(lockPath, 0o000);
    const failure = await failureOf(assertNoLiveWriter(directory, { isProcessAlive: () => false }));
    // EACCES is not "no lock": the record is left alone, nothing is read as absent.
    expect(expectError(failure, CliError).code).toBe('spool_unsafe_path');
    expect(await readdir(directory)).toEqual([writerLockFileName]);
    expect((await stat(lockPath)).mode & 0o777).toBe(0o000);
  });
});

describe('ProducerSpool.withLock — writer guard (D-S56-Q8)', () => {
  let root: string;
  let spoolDir: string;

  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), 'evidgate-writer-guard-'));
    spoolDir = join(root, 'spool');
    await mkdir(spoolDir, { recursive: true, mode: 0o700 });
  });

  afterEach(async () => {
    await rm(root, { recursive: true, force: true });
  });

  function openSpool(): Promise<ProducerSpool> {
    return ProducerSpool.open({
      spoolDir,
      runId: 'run-04.3.3',
      producerId: 'producer-primary',
      maxBytes: defaultSpoolMaxBytes,
      createIfMissing: true,
    });
  }

  it('refuses the chunk paths while a live reporter writes, holding spool.lock first', async () => {
    const spool = await openSpool();
    await writeFile(join(spool.directory, writerLockFileName), writerLockRecord(4242), {
      mode: spoolLockFileMode,
    });
    let ran = false;
    const failure = await failureOf(
      spool.withLock(
        () => {
          ran = true;
          return Promise.resolve('ran');
        },
        { requireNoLiveWriter: true, isProcessAlive: () => true },
      ),
    );
    expect(expectError(failure, WriterLockedError).code).toBe('spool_locked');
    expect(ran).toBe(false);
    // The CLI lock is free again (taken first, released on the refusal) and the reporter's
    // lock is untouched: a blocked command leaves the producer directory as it found it.
    expect(await readSpoolLock(spool.directory)).toBeNull();
    expect(await readdir(spool.directory)).toEqual([writerLockFileName]);
  });

  it('runs the guarded callback when the reporter lock names a dead pid', async () => {
    const spool = await openSpool();
    await writeFile(join(spool.directory, writerLockFileName), writerLockRecord(deadPid), {
      mode: spoolLockFileMode,
    });
    const result = await spool.withLock(() => Promise.resolve('ran'), {
      requireNoLiveWriter: true,
      isProcessAlive: () => false,
      probeDelayMs: 1,
    });
    expect(result).toBe('ran');
    expect(await readFile(join(spool.directory, writerLockFileName), 'utf8')).toBe(
      writerLockRecord(deadPid),
    );
  });

  it('keeps today’s behaviour for the upload journal while the reporter writes', async () => {
    const spool = await openSpool();
    await writeFile(join(spool.directory, writerLockFileName), writerLockRecord(4242), {
      mode: spoolLockFileMode,
    });
    // Exactly the call `artifacts upload` hands the journal: the plain lock, no guard, so a
    // running test run can never block an artifact upload.
    const journal = await UploadJournal.open({
      producerDirectory: spool.directory,
      withLock: (fn) => spool.withLock(fn),
    });
    const artifactId = randomUUID();
    // 04.2.4 §12.5: the record carries the whole upload identity, not just (digest, size).
    const identity = {
      file_sha256: `sha256:${'b'.repeat(64)}`,
      size: 128,
      kind: 'trace' as const,
      execution_key: 'a3'.repeat(32),
      attempt_no: 1,
      media_type: 'application/zip',
    };
    await journal.write({
      artifact_id: artifactId,
      ...identity,
      put_done: true,
      complete_done: false,
      mint_no: 0,
    });
    expect(await journal.nextMintNo(identity)).toBe(1);
    expect((await journal.entries()).map((entry) => entry.artifact_id)).toEqual([artifactId]);
    // The reporter's lock is still the reporter's, and no CLI lock is left behind.
    expect(await readFile(join(spool.directory, writerLockFileName), 'utf8')).toBe(
      writerLockRecord(4242),
    );
    expect(await readSpoolLock(spool.directory)).toBeNull();
  });
});
