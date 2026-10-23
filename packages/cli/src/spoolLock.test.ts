// 04.2.3-T1.1: the single-writer lock specs (D2). The interesting cases are the ones the
// filesystem cannot show by itself — a lock left by a dead pid (taken over), a lock held by
// a live one (refused), a lock whose content changed between the read and the takeover
// (put back, refused), a foreign writer caught mid-create (probe), and a non-regular file
// planted at the lock path (never followed, never blocking) — so the liveness probe is
// injected; everything else is real IO in a temporary directory.
import { execFile } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { chmodSync, writeFileSync } from 'node:fs';
import {
  chmod,
  lstat,
  mkdir,
  mkdtemp,
  open,
  readFile,
  readdir,
  rename,
  rm,
  stat,
  symlink,
  writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { ProducerSpool, defaultSpoolMaxBytes } from './spool.js';
import { CliError } from './errors.js';
import { UploadJournal } from './uploadJournal.js';
import {
  SpoolLockedError,
  WriterLockedError,
  assertNoLiveWriter,
  isHardLinkUnsupportedErrno,
  isMissingLockErrno,
  isProcessAlive,
  readSpoolLock,
  readWriterLock,
  spoolLockFileMode,
  spoolLockFileName,
  withSpoolLock,
  writerLockFileName,
} from './spoolLock.js';

/**
 * `mkfifo` through a plain promise: no shell, no options to get wrong.
 * `Promise.withResolvers` is ES2024 and the workspace targets ES2022.
 */
function makeFifo(path: string): Promise<void> {
  return new Promise((resolve, reject) => {
    execFile('mkfifo', [path], (error) => {
      if (error === null) resolve();
      else reject(error instanceof Error ? error : new Error(`mkfifo ${path} failed`));
    });
  });
}

const fixedNow = new Date('2026-10-23T12:00:00.000Z');
const fixedIso = fixedNow.toISOString();
/** A pid no test process can have: the injected liveness probe decides on it. */
const deadPid = 987_654;

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

/** Parses a sampled lock file strictly: the whole content must be one lock record. */
function sampleLockPid(text: string): number {
  const parsed: unknown = JSON.parse(text);
  if (typeof parsed !== 'object' || parsed === null || !('pid' in parsed)) {
    throw new Error(`lock file content is not a lock record: ${text}`);
  }
  const pid: unknown = parsed.pid;
  if (typeof pid !== 'number' || !Number.isInteger(pid)) {
    throw new Error(`lock record has no integer pid: ${text}`);
  }
  if (!text.endsWith('}\n')) throw new Error(`lock record is not terminated: ${text}`);
  return pid;
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

  async function readLockText(): Promise<string | null> {
    try {
      return await readFile(lockPath, 'utf8');
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null;
      throw error;
    }
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
        // The staging file that carried the record is gone: the lock is its only name.
        expect(await readdir(directory)).toEqual([spoolLockFileName]);
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
    const failure = await withSpoolLock(directory, async () => {
      const inner = await failureOf(
        withSpoolLock(directory, () => {
          secondRan = true;
          return Promise.resolve();
        }),
      );
      // The loser's staging file is gone: only the holder's lock is in the directory.
      expect(await readdir(directory)).toEqual([spoolLockFileName]);
      return inner;
    });
    const locked = expectError(failure, SpoolLockedError);
    expect(locked.code).toBe('spool_locked');
    expect(locked.holderPid).toBe(process.pid);
    expect(locked.lockPath).toBe(lockPath);
    expect(secondRan).toBe(false);
  });

  it('takes over a lock whose pid is dead on this host', async () => {
    await writeLock(`${JSON.stringify({ pid: deadPid, started_at: fixedIso })}\n`);
    let ran = 0;
    const result = await withSpoolLock(
      directory,
      () => {
        ran += 1;
        return Promise.resolve('ran');
      },
      { pid: 4321, isProcessAlive: (pid) => pid !== deadPid },
    );
    expect(result).toBe('ran');
    expect(ran).toBe(1);
    // The takeover's own lock was released by the holder, and no quarantine file is left.
    expect(await readSpoolLock(directory)).toBeNull();
    expect(await readdir(directory)).toEqual([]);
  });

  it('never steals a lock held by a live pid', async () => {
    const foreign = `${JSON.stringify({ pid: 4242, started_at: fixedIso })}\n`;
    await writeLock(foreign);
    let ran = false;
    const failure = await failureOf(
      withSpoolLock(
        directory,
        () => {
          ran = true;
          return Promise.resolve();
        },
        { isProcessAlive: (pid) => pid === 4242 },
      ),
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
    expect(await readdir(directory)).toEqual([]);
  });

  it('does not take over a lock a foreign writer is still filling in', async () => {
    // A writer that creates the file first and writes its record a tick later (an older
    // CLI, or anything else): the empty file must not be mistaken for a crashed holder.
    const foreign = `${JSON.stringify({ pid: 4242, started_at: 'still-writing' })}\n`;
    const handle = await open(lockPath, 'w', spoolLockFileMode);
    const filling = (async (): Promise<void> => {
      await handle.writeFile(foreign, 'utf8');
      await handle.close();
    })();
    let ran = false;
    const failure = await failureOf(
      withSpoolLock(
        directory,
        () => {
          ran = true;
          return Promise.resolve();
        },
        { pid: 5555, isProcessAlive: (pid) => pid === 4242 },
      ),
    );
    await filling;
    const locked = expectError(failure, SpoolLockedError);
    expect(locked.holderPid).toBe(4242);
    expect(ran).toBe(false);
    expect(await readFile(lockPath, 'utf8')).toBe(foreign);
  });

  it('gives a single holder when two contenders race over one dead lock', async () => {
    const contenderPids = [1111, 2222];
    for (let round = 0; round < 3; round += 1) {
      await writeLock(`${JSON.stringify({ pid: deadPid, started_at: fixedIso })}\n`);
      let inFlight = 0;
      let maxInFlight = 0;
      const outcomes = await Promise.all(
        contenderPids.map(async (pid) => {
          let ran = false;
          const failure = await failureOf(
            withSpoolLock(
              directory,
              async () => {
                ran = true;
                inFlight += 1;
                maxInFlight = Math.max(maxInFlight, inFlight);
                // Hold long enough for the other contender to reach the takeover path.
                await delay(5);
                inFlight -= 1;
                return pid;
              },
              { pid, isProcessAlive: (probed) => contenderPids.includes(probed) },
            ),
          );
          return { pid, ran, failure };
        }),
      );
      // Never two holders at once: a loser either sees the winner alive or puts its lock
      // back and fails; the takeover never yields two live holders.
      expect(maxInFlight).toBe(1);
      expect(outcomes.filter((outcome) => outcome.ran).length).toBeGreaterThan(0);
      for (const outcome of outcomes.filter((candidate) => !candidate.ran)) {
        expect(expectError(outcome.failure, SpoolLockedError).code).toBe('spool_locked');
      }
      // Every holder released; nothing (quarantine or staging file) is left behind.
      expect(await readLockText()).toBeNull();
      expect(await readdir(directory)).toEqual([]);
    }
  });

  it('never exposes an empty or partial lock while contenders race', async () => {
    const contenderPids = [3101, 3102, 3103];
    const allowed = new Set(contenderPids);
    let sampling = true;
    let sampled = 0;
    const sampler = (async (): Promise<void> => {
      while (sampling) {
        const text = await readLockText();
        if (text !== null) {
          sampled += 1;
          // Creation is atomic with the content: any observed lock is a whole record.
          expect(contenderPids).toContain(sampleLockPid(text));
        }
      }
    })();
    try {
      await Promise.all(
        contenderPids.map((pid) =>
          failureOf(
            withSpoolLock(directory, () => delay(3), {
              pid,
              isProcessAlive: (probed) => allowed.has(probed),
            }),
          ),
        ),
      );
    } finally {
      sampling = false;
      await sampler;
    }
    expect(sampled).toBeGreaterThan(0);
    expect(await readdir(directory)).toEqual([]);
  });

  it('puts back a live lock whose content did not match the stale one it took over', async () => {
    await writeLock(`${JSON.stringify({ pid: deadPid, started_at: fixedIso })}\n`);
    const foreign = `${JSON.stringify({ pid: 4242, started_at: 'live-successor' })}\n`;
    let swapped = false;
    let ran = false;
    const failure = await failureOf(
      withSpoolLock(
        directory,
        () => {
          ran = true;
          return Promise.resolve();
        },
        {
          pid: 5555,
          // The seam of the slow loser: between its read of the dead holder and its
          // takeover, a live process replaced the lock.
          isProcessAlive: (pid) => {
            if (pid === deadPid && !swapped) {
              swapped = true;
              writeFileSync(lockPath, foreign, { mode: spoolLockFileMode });
            }
            return pid !== deadPid;
          },
        },
      ),
    );
    const locked = expectError(failure, SpoolLockedError);
    expect(swapped).toBe(true);
    expect(locked.holderPid).toBe(4242);
    expect(ran).toBe(false);
    // The lock we had not observed is back at its path, byte for byte.
    expect(await readFile(lockPath, 'utf8')).toBe(foreign);
    expect(await readdir(directory)).toEqual([spoolLockFileName]);
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

  it('scavenges a quarantined copy of its own lock instead of leaving a phantom', async () => {
    // A verified takeover renames our lock aside before it decides; we release inside that
    // window, so the path is empty and the taker's restore will find nothing either. The
    // record must not survive as a lock naming a live pid that holds nothing.
    const quarantined = join(directory, `${spoolLockFileName}.9999.${randomUUID()}.stale`);
    await withSpoolLock(
      directory,
      async () => {
        await rename(lockPath, quarantined);
      },
      { now: () => fixedNow },
    );
    expect(await readLockText()).toBeNull();
    expect(await readdir(directory)).toEqual([]);
  });

  it('releases only a lock that still carries its own pid and started_at', async () => {
    const other = `${JSON.stringify({ pid: process.pid, started_at: 'another-run' })}\n`;
    await withSpoolLock(
      directory,
      async () => {
        // Same pid, different record: a successor on this pid still owns the lock.
        await writeLock(other);
      },
      { now: () => fixedNow },
    );
    expect(await readFile(lockPath, 'utf8')).toBe(other);
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

  it('treats only "nothing is there" errnos as an absent lock', () => {
    for (const code of ['ENOENT', 'ELOOP', 'ENXIO']) {
      expect(isMissingLockErrno(code)).toBe(true);
    }
    const failures: (string | undefined)[] = [
      'EMFILE',
      'ENFILE',
      'EIO',
      'EACCES',
      'EPERM',
      'ENOTDIR',
      'EISDIR',
      undefined,
    ];
    for (const code of failures) {
      expect(isMissingLockErrno(code)).toBe(false);
    }
  });

  it('refuses the lock on a filesystem without hard links', () => {
    for (const code of ['EPERM', 'ENOTSUP', 'EOPNOTSUPP', 'EXDEV', 'ENOSYS']) {
      expect(isHardLinkUnsupportedErrno(code)).toBe(true);
    }
    for (const code of ['EEXIST', 'ENOENT', 'EMLINK', 'EIO', undefined]) {
      expect(isHardLinkUnsupportedErrno(code)).toBe(false);
    }
  });

  it('fails instead of taking over a lock it could not inspect', async () => {
    // Root ignores file modes, so the permission check below would not fire.
    if (process.platform === 'win32' || process.getuid?.() === 0) return;
    await writeLock(`${JSON.stringify({ pid: deadPid, started_at: fixedIso })}\n`);
    await chmod(lockPath, 0o000);
    let ran = false;
    const failure = await failureOf(
      withSpoolLock(
        directory,
        () => {
          ran = true;
          return Promise.resolve();
        },
        { isProcessAlive: () => false },
      ),
    );
    // EACCES is not "no lock": the dead-pid record is left alone, never taken over.
    const error = expectError(failure, CliError);
    expect(error.code).toBe('spool_unsafe_path');
    expect(ran).toBe(false);
    expect(await readdir(directory)).toEqual([spoolLockFileName]);
  });

  it('puts the lock back when the quarantined copy cannot be inspected', async () => {
    if (process.platform === 'win32' || process.getuid?.() === 0) return;
    await writeLock(`${JSON.stringify({ pid: deadPid, started_at: fixedIso })}\n`);
    let ran = false;
    const failure = await failureOf(
      withSpoolLock(
        directory,
        () => {
          ran = true;
          return Promise.resolve();
        },
        {
          // The seam of the slow loser: the lock turns unreadable between the read of the
          // dead holder and the rename-aside, so the quarantined re-read fails.
          isProcessAlive: (pid) => {
            if (pid === deadPid) chmodSync(lockPath, 0o000);
            return pid !== deadPid;
          },
        },
      ),
    );
    const error = expectError(failure, CliError);
    expect(error.code).toBe('spool_unsafe_path');
    expect(error.message).toContain('put back');
    expect(ran).toBe(false);
    // The quarantined lock is back at its path, still unreadable, and nothing else remains.
    expect(await readdir(directory)).toEqual([spoolLockFileName]);
    expect((await stat(lockPath)).mode & 0o777).toBe(0o000);
  });

  it('keeps the callback result when the lock cannot be inspected on release', async () => {
    if (process.platform === 'win32' || process.getuid?.() === 0) return;
    const result = await withSpoolLock(directory, async () => {
      await chmod(lockPath, 0o000);
      return 'released-blind';
    });
    expect(result).toBe('released-blind');
    // The lock could not be read, so it was left in place instead of unlinked blind.
    expect(await readdir(directory)).toEqual([spoolLockFileName]);

    await rm(lockPath, { force: true });
    const failure = await withSpoolLock(directory, async () => {
      await chmod(lockPath, 0o000);
      throw new Error('send failed');
    }).then(
      () => null,
      (error: unknown) => error,
    );
    // Release failing too must never mask the callback's own error.
    expect(failure).toBeInstanceOf(Error);
    expect((failure as Error).message).toBe('send failed');
    expect(await readdir(directory)).toEqual([spoolLockFileName]);
  });

  it('reads a FIFO planted at spool.lock without blocking, then takes it over', async () => {
    // `mkfifo` is POSIX-only; the Windows lock degrades to a plain open (see spoolLock.ts).
    if (process.platform === 'win32') return;
    await makeFifo(lockPath);
    // A reader without O_NONBLOCK would hang here until the test timed out.
    const outcome = await Promise.race([
      readSpoolLock(directory).then((lock) => (lock === null ? 'not-a-lock' : 'lock')),
      delay(500).then(() => 'blocked'),
    ]);
    expect(outcome).toBe('not-a-lock');
    // A non-regular file holds no record, so the CLI takes it over and the FIFO is gone.
    const result = await withSpoolLock(directory, () => Promise.resolve('ran'), {
      isProcessAlive: () => false,
    });
    expect(result).toBe('ran');
    expect(await readdir(directory)).toEqual([]);
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

  function openSpool(): Promise<ProducerSpool> {
    return ProducerSpool.open({
      spoolDir,
      runId: 'run-04.2.3',
      producerId: 'producer-primary',
      maxBytes: defaultSpoolMaxBytes,
      createIfMissing: true,
    });
  }

  it('excludes a second CLI process and re-locks after release', async () => {
    const first = await openSpool();
    const second = await openSpool();
    const failure = await first.withLock(() =>
      failureOf(second.withLock(() => Promise.resolve('second'))),
    );
    const locked = expectError(failure, SpoolLockedError);
    expect(locked.code).toBe('spool_locked');
    expect(await readSpoolLock(first.directory)).toBeNull();
    expect(await first.withLock(() => Promise.resolve('again'))).toBe('again');
  });

  it('releases the lock when the callback throws', async () => {
    const spool = await openSpool();
    const failure = await failureOf(spool.withLock(() => Promise.reject(new Error('send failed'))));
    expect(failure).toBeInstanceOf(Error);
    expect(await readSpoolLock(spool.directory)).toBeNull();
    expect(await spool.withLock(() => Promise.resolve('ok'))).toBe('ok');
  });
});

/** The whole `writer.lock` record the reporter writes (`{ pid, started_at }`), one line. */
function writerLockRecord(pid: number, startedAt = fixedIso): string {
  return `${JSON.stringify({ pid, started_at: startedAt })}\n`;
}

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
