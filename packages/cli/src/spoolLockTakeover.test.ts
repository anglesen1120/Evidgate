// Stale-lock takeover is the only path that removes a lock it did not create, so these
// specs interleave real contenders over real files, each parked at a hook and released by
// the spec rather than by a timer.
import { randomUUID } from 'node:crypto';
import { link, mkdir, mkdtemp, readFile, readdir, rename, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { withSpoolLock } from './spoolLock.js';
import { deadPid, fixedNow, writerLockRecord as lockText } from './spoolLock.testing.js';
import { expectError, failureOf } from './spool.testing.js';
import {
  spoolLockFileName,
  spoolLockFileMode,
  SpoolLockedError,
  takeoverLockFileName,
  type SpoolLockHooks,
} from './spoolLockTypes.js';

/** A rendezvous the spec opens by hand; `Promise.withResolvers` is ES2024, the workspace ES2022. */
function deferred(): { readonly promise: Promise<void>; readonly resolve: () => void } {
  let open: () => void = () => undefined;
  const promise = new Promise<void>((settle) => {
    open = () => {
      settle();
    };
  });
  return { promise, resolve: open };
}

interface ContenderOptions {
  readonly hooks?: SpoolLockHooks;
  readonly hold?: boolean;
}

interface ContenderHandle {
  readonly done: Promise<string>;
  readonly failure: Promise<unknown>;
  readonly running: Promise<void>;
  release: () => void;
}

/**
 * Launches contenders over one directory with a shared liveness probe and overlap counter.
 * A `hold` contender parks inside its callback until `release`, so another contender can
 * interleave; hooks park it before or after the quarantine rename.
 */
function contenderRace(directory: string, alivePids: readonly number[]) {
  const probe = (pid: number): boolean => alivePids.includes(pid);
  const stats = { peak: 0 };
  let inFlight = 0;
  const start = (pid: number, options: ContenderOptions = {}): ContenderHandle => {
    const running = deferred();
    const released = deferred();
    const done = withSpoolLock(
      directory,
      async () => {
        inFlight += 1;
        stats.peak = Math.max(stats.peak, inFlight);
        running.resolve();
        try {
          if (options.hold === true) await released.promise;
          return `pid-${pid}`;
        } finally {
          inFlight -= 1;
        }
      },
      {
        pid,
        now: () => fixedNow,
        isProcessAlive: probe,
        ...(options.hooks === undefined ? {} : { hooks: options.hooks }),
      },
    );
    return { done, failure: failureOf(done), running: running.promise, release: released.resolve };
  };
  return { start, stats };
}

describe('withSpoolLock — stale takeover', () => {
  let root: string;
  let directory: string;
  let lockPath: string;
  let mutexPath: string;

  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), 'evidgate-spool-takeover-'));
    directory = join(root, 'producer');
    await mkdir(directory, { recursive: true, mode: 0o700 });
    lockPath = join(directory, spoolLockFileName);
    mutexPath = join(directory, takeoverLockFileName);
  });

  afterEach(async () => {
    await rm(root, { recursive: true, force: true });
  });

  it('refuses a contender that outran the takeover window instead of moving the winner', async () => {
    await writeFile(lockPath, lockText(deadPid), { mode: spoolLockFileMode });
    // Every contender is alive to the others; only the planted lock names a dead pid.
    const race = contenderRace(directory, [1111, 2222, 3333]);
    const aParked = deferred();
    const releaseA = deferred();
    const aQuarantined = deferred();
    const releaseQuarantine = deferred();
    // A observes the dead lock and parks before it may touch the path.
    const a = race.start(1111, {
      hooks: {
        beforeTakeover: async () => {
          aParked.resolve();
          await releaseA.promise;
        },
        afterQuarantine: async () => {
          aQuarantined.resolve();
          await releaseQuarantine.promise;
        },
      },
    });
    await aParked.promise;

    // B takes over the dead lock, links its own and parks inside the callback.
    const b = race.start(2222, { hold: true });
    await b.running;

    releaseA.resolve();
    // Either A is refused at once, or it parked after a rename it should never have done.
    const aPhase = await Promise.race([
      a.failure.then(() => 'refused' as const),
      aQuarantined.promise.then(() => 'quarantined' as const),
    ]);
    const c = race.start(3333, { hold: true });
    const cPhase = await Promise.race([
      c.done.then(
        () => 'entered' as const,
        () => 'refused' as const,
      ),
      c.running.then(() => 'entered' as const),
    ]);

    try {
      expect(cPhase).toBe('refused');
      expect(aPhase).toBe('refused');
      const locked = expectError(await a.failure, SpoolLockedError);
      expect(locked).toMatchObject({ code: 'spool_locked', holderPid: 2222 });
      // B's lock was never moved: the path still names B and nothing is quarantined.
      expect(await readFile(lockPath, 'utf8')).toBe(lockText(2222));
      expect(await readdir(directory)).toEqual([spoolLockFileName]);
      expect(race.stats.peak).toBe(1);
    } finally {
      b.release();
      releaseQuarantine.resolve();
      c.release();
      await Promise.allSettled([a.done, b.done, c.done]);
    }
    expect(await readdir(directory)).toEqual([]);
  });

  it('refuses a contender whose unreadable entry was released before its inode re-check', async () => {
    // The observed lock records nothing (an empty file a crash left behind), so it is only a
    // name: the entry is released while we read it, and a live successor takes the path.
    await writeFile(lockPath, '', { mode: spoolLockFileMode });
    const race = contenderRace(directory, [1111, 2222, 3333]);
    const entryRead = deferred();
    const releaseA = deferred();
    const aQuarantined = deferred();
    const releaseQuarantine = deferred();
    const a = race.start(1111, {
      hooks: {
        afterEntryRead: async () => {
          entryRead.resolve();
          await releaseA.promise;
        },
        afterQuarantine: async () => {
          aQuarantined.resolve();
          await releaseQuarantine.promise;
        },
      },
    });
    await entryRead.promise;

    // A hard link keeps the released inode alive, so B's lock cannot reuse its number and
    // the re-stat below differs from the observed entry on every filesystem.
    const releasedEntry = join(root, 'released-empty-entry');
    await link(lockPath, releasedEntry);
    await rm(lockPath);
    const b = race.start(2222, { hold: true });
    await b.running;

    releaseA.resolve();
    const aPhase = await Promise.race([
      a.failure.then(() => 'refused' as const),
      aQuarantined.promise.then(() => 'quarantined' as const),
    ]);
    const c = race.start(3333, { hold: true });
    const cPhase = await Promise.race([
      c.done.then(
        () => 'entered' as const,
        () => 'refused' as const,
      ),
      c.running.then(() => 'entered' as const),
    ]);
    try {
      expect(cPhase).toBe('refused');
      expect(aPhase).toBe('refused');
      expect(expectError(await a.failure, SpoolLockedError).holderPid).toBe(2222);
      expect(await readFile(lockPath, 'utf8')).toBe(lockText(2222));
      expect(await readdir(directory)).toEqual([spoolLockFileName]);
      expect(race.stats.peak).toBe(1);
    } finally {
      b.release();
      releaseQuarantine.resolve();
      c.release();
      await Promise.allSettled([a.done, b.done, c.done]);
    }
    expect(await readdir(directory)).toEqual([]);
  });

  it('removes a holder’s lock released while a taker has it quarantined', async () => {
    const holderRunning = deferred();
    const releaseHolder = deferred();
    const quarantined = deferred();
    const releaseTaker = deferred();
    // The holder is a real one whose pid the taker's probe cannot see — another pid
    // namespace, say — which is the only way a live record reads as dead here.
    const holder = withSpoolLock(
      directory,
      async () => {
        holderRunning.resolve();
        await releaseHolder.promise;
        return 'holder';
      },
      { pid: 2222, now: () => fixedNow, isProcessAlive: () => true },
    );
    await holderRunning.promise;
    const taker = withSpoolLock(directory, () => Promise.resolve('taker'), {
      pid: 5555,
      now: () => fixedNow,
      isProcessAlive: (pid) => pid !== 2222,
      hooks: {
        afterQuarantine: async () => {
          quarantined.resolve();
          await releaseTaker.promise;
        },
      },
    });
    await quarantined.promise;

    releaseHolder.resolve();
    expect(await holder).toBe('holder');
    // The quarantine name went first, so the taker's restore cannot link it back.
    expect(await readdir(directory)).toEqual([takeoverLockFileName]);

    releaseTaker.resolve();
    expect(await taker).toBe('taker');
    expect(await readdir(directory)).toEqual([]);
  });

  it('removes a lock a taker restored after the holder released', async () => {
    // Both names carry the holder's record: the taker quarantined it, its restore linked
    // the same inode back to the path, and the holder released in between.
    const quarantinedPath = join(directory, `${spoolLockFileName}.5555.${randomUUID()}.stale`);
    const result = await withSpoolLock(
      directory,
      async () => {
        await rename(lockPath, quarantinedPath);
        await link(quarantinedPath, lockPath);
        return 'ran';
      },
      { now: () => fixedNow },
    );
    expect(result).toBe('ran');
    expect(await readdir(directory)).toEqual([]);
  });

  it('breaks a takeover mutex whose holder is dead and honours a live one', async () => {
    await writeFile(lockPath, lockText(deadPid), { mode: spoolLockFileMode });
    await writeFile(mutexPath, lockText(4444), { mode: spoolLockFileMode });
    const result = await withSpoolLock(directory, () => Promise.resolve('ran'), {
      pid: 5555,
      now: () => fixedNow,
      isProcessAlive: (pid) => pid !== 4444 && pid !== deadPid,
    });
    expect(result).toBe('ran');
    expect(await readdir(directory)).toEqual([]);

    const staleLock = lockText(deadPid);
    const liveMutex = lockText(6666);
    await writeFile(lockPath, staleLock, { mode: spoolLockFileMode });
    await writeFile(mutexPath, liveMutex, { mode: spoolLockFileMode });
    const failure = await failureOf(
      withSpoolLock(directory, () => Promise.resolve('ran'), {
        pid: 5555,
        now: () => fixedNow,
        isProcessAlive: (pid) => pid === 6666,
      }),
    );
    const locked = expectError(failure, SpoolLockedError);
    expect(locked.code).toBe('spool_locked');
    expect(locked.holderPid).toBe(6666);
    // A live taker's mutex and the stale lock it is still working on stay where they are.
    expect(await readFile(mutexPath, 'utf8')).toBe(liveMutex);
    expect(await readFile(lockPath, 'utf8')).toBe(staleLock);
  });

  it('refuses a breaker that resumes after another reclaimed the same dead mutex', async () => {
    // Both contenders observed the same dead mutex. The delayed one resumes only after the
    // fast one published its live mutex; deleting that by name would seat both as takers.
    await writeFile(lockPath, lockText(deadPid), { mode: spoolLockFileMode });
    await writeFile(mutexPath, lockText(4444), { mode: spoolLockFileMode });
    // Every contender is alive to the others; the planted lock and mutex name dead pids.
    const race = contenderRace(directory, [1111, 2222]);
    const reclaimParked = deferred();
    const releaseReclaim = deferred();
    const quarantined = deferred();
    const releaseQuarantine = deferred();
    const delayed = race.start(1111, {
      hooks: {
        beforeDeadMutexReclaim: async () => {
          reclaimParked.resolve();
          await releaseReclaim.promise;
        },
      },
    });
    await reclaimParked.promise;

    // The fast contender reclaims that mutex, takes it, quarantines the stale lock and then
    // holds the mutex while the delayed breaker resumes.
    const fast = race.start(2222, {
      hooks: {
        afterQuarantine: async () => {
          quarantined.resolve();
          await releaseQuarantine.promise;
        },
      },
    });
    await quarantined.promise;

    releaseReclaim.resolve();
    try {
      const locked = expectError(await delayed.failure, SpoolLockedError);
      expect(locked.code).toBe('spool_locked');
      // The refused breaker names the taker in the mutex it moved, and that mutex is back.
      expect(locked.holderPid).toBe(2222);
      expect(await readFile(mutexPath, 'utf8')).toBe(lockText(2222));
    } finally {
      releaseQuarantine.resolve();
    }
    expect(await fast.done).toBe('pid-2222');
    // One holder, and it is the contender whose live mutex the delayed breaker found.
    expect(race.stats.peak).toBe(1);
    expect(await readdir(directory)).toEqual([]);
  });
});
