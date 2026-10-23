// 04.2.3-T1.1: the single-writer lock specs (D2). The interesting cases are the two the
// filesystem cannot show by itself — a lock left by a dead pid (taken over) and a lock held
// by a live one (refused) — so the liveness probe is injected; everything else is real IO
// in a temporary directory.
import { randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, readFile, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { ProducerSpool, defaultSpoolMaxBytes } from './spool.js';
import {
  SpoolLockedError,
  isProcessAlive,
  readSpoolLock,
  spoolLockFileMode,
  spoolLockFileName,
  withSpoolLock,
} from './spoolLock.js';

const fixedNow = new Date('2026-10-23T12:00:00.000Z');
const fixedIso = fixedNow.toISOString();

function failureOf<T>(promise: Promise<T>): Promise<unknown> {
  return promise.then(
    () => null,
    (error: unknown) => error,
  );
}

function expectError<T extends Error>(
  failure: unknown,
  ctor: { new (...args: never[]): T; readonly name: string },
): T {
  if (!(failure instanceof ctor)) {
    const observed = failure instanceof Error ? failure.message : typeof failure;
    throw new Error(`expected ${ctor.name}, got ${observed}`);
  }
  return failure;
}

describe('withSpoolLock', () => {
  let root: string;
  let directory: string;
  let lockPath: string;

  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), 'evidgate-lock-'));
    directory = join(root, 'producer');
    await mkdir(directory, { recursive: true, mode: 0o700 });
    lockPath = join(directory, spoolLockFileName);
  });

  afterEach(async () => {
    await rm(root, { recursive: true, force: true });
  });

  async function writeLock(content: string): Promise<void> {
    await writeFile(lockPath, content, { mode: spoolLockFileMode });
  }

  it('holds the lock for the callback, then removes it', async () => {
    expect(await readSpoolLock(directory)).toBeNull();
    const result = await withSpoolLock(
      directory,
      async () => {
        expect(await readSpoolLock(directory)).toEqual({
          pid: process.pid,
          startedAt: fixedIso,
        });
        // The whole content is `{ pid, started_at }`: nothing else can ride along.
        const text = await readFile(lockPath, 'utf8');
        expect(text).toBe(`${JSON.stringify({ pid: process.pid, started_at: fixedIso })}\n`);
        expect((await stat(lockPath)).mode & 0o777).toBe(spoolLockFileMode);
        return 42;
      },
      { now: () => fixedNow },
    );
    expect(result).toBe(42);
    expect(await readSpoolLock(directory)).toBeNull();
    expect(await readdir(directory)).toEqual([]);
  });

  it('gives a second holder spool_locked instead of interleaving', async () => {
    let secondRan = false;
    const failure = await withSpoolLock(directory, () =>
      failureOf(
        withSpoolLock(directory, () => {
          secondRan = true;
          return Promise.resolve();
        }),
      ),
    );
    const locked = expectError(failure, SpoolLockedError);
    expect(locked.code).toBe('spool_locked');
    expect(locked.holderPid).toBe(process.pid);
    expect(locked.lockPath).toBe(lockPath);
    expect(secondRan).toBe(false);
  });

  it('takes over a lock whose pid is dead on this host', async () => {
    await writeLock(`${JSON.stringify({ pid: 987_654, started_at: fixedIso })}\n`);
    let ran = 0;
    const result = await withSpoolLock(
      directory,
      () => {
        ran += 1;
        return Promise.resolve('ran');
      },
      { pid: 4321, isProcessAlive: (pid) => pid !== 987_654 },
    );
    expect(result).toBe('ran');
    expect(ran).toBe(1);
    // The takeover's own lock was released by the holder.
    expect(await readSpoolLock(directory)).toBeNull();
  });

  it('never steals a lock held by a live pid', async () => {
    const foreign = `${JSON.stringify({ pid: 4242, started_at: fixedIso })}\n`;
    await writeLock(foreign);
    let ran = false;
    const failure = await withSpoolLock(
      directory,
      () => {
        ran = true;
        return Promise.resolve();
      },
      { isProcessAlive: (pid) => pid === 4242 },
    ).then(
      () => null,
      (error: unknown) => error,
    );
    const locked = expectError(failure, SpoolLockedError);
    expect(locked.holderPid).toBe(4242);
    expect(ran).toBe(false);
    // The live holder's lock is untouched.
    expect(await readFile(lockPath, 'utf8')).toBe(foreign);
  });

  it('treats an unreadable lock left by a crash as stale', async () => {
    await writeLock('{not json');
    const result = await withSpoolLock(directory, () => Promise.resolve('ran'), {
      isProcessAlive: () => false,
    });
    expect(result).toBe('ran');
    expect(await readSpoolLock(directory)).toBeNull();
  });

  it('leaves a lock that was taken over while the callback ran', async () => {
    await withSpoolLock(directory, async () => {
      // Another process broke in and rewrote the lock: our release must not delete its.
      await writeLock(`${JSON.stringify({ pid: 7777, started_at: 'foreign' })}\n`);
    });
    expect(await readFile(lockPath, 'utf8')).toBe(
      `${JSON.stringify({ pid: 7777, started_at: 'foreign' })}\n`,
    );
  });

  it('writes no credential material into the lock', async () => {
    // A JWT-shaped value, assembled at runtime so no literal sits in the spec.
    const jwt = [
      ['eyJ', 'hbGciOi', 'JIUzI1NiJ9'].join(''),
      ['eyJ', 'zdWIiOiJsb2NrIn0'].join(''),
      'c2ln',
    ].join('.');
    const secret = ['evg', 'lock', randomUUID().replace(/-/g, '')].join('_');
    await withSpoolLock(directory, async () => {
      const text = await readFile(lockPath, 'utf8');
      expect(text).not.toContain(jwt);
      expect(text).not.toContain(secret);
      expect(text).toMatch(/^\{"pid":\d+,"started_at":"[^"]+"\}\n$/u);
    });
  });

  it('detects liveness only for real pids', () => {
    expect(isProcessAlive(process.pid)).toBe(true);
    expect(isProcessAlive(0)).toBe(false);
    expect(isProcessAlive(-1)).toBe(false);
    expect(isProcessAlive(1.5)).toBe(false);
  });
});

describe('ProducerSpool.withLock', () => {
  let root: string;
  let spoolDir: string;

  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), 'evidgate-spool-lock-'));
    spoolDir = join(root, 'spool');
    await mkdir(spoolDir, { recursive: true, mode: 0o700 });
  });

  afterEach(async () => {
    await rm(root, { recursive: true, force: true });
  });

  function open(): Promise<ProducerSpool> {
    return ProducerSpool.open({
      spoolDir,
      runId: 'run-04.2.3',
      producerId: 'producer-primary',
      maxBytes: defaultSpoolMaxBytes,
      createIfMissing: true,
    });
  }

  it('excludes a second CLI process and re-locks after release', async () => {
    const first = await open();
    const second = await open();
    const failure = await first.withLock(() =>
      failureOf(second.withLock(() => Promise.resolve('second'))),
    );
    const locked = expectError(failure, SpoolLockedError);
    expect(locked.code).toBe('spool_locked');
    expect(await readSpoolLock(first.directory)).toBeNull();
    expect(await first.withLock(() => Promise.resolve('again'))).toBe('again');
  });

  it('releases the lock when the callback throws', async () => {
    const spool = await open();
    const failure = await failureOf(spool.withLock(() => Promise.reject(new Error('send failed'))));
    expect(failure).toBeInstanceOf(Error);
    expect(await readSpoolLock(spool.directory)).toBeNull();
    expect(await spool.withLock(() => Promise.resolve('ok'))).toBe('ok');
  });
});
