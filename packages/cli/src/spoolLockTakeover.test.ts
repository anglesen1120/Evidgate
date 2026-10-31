// The stale-lock takeover is the only place where a contender removes a lock it did not
// create, so these specs interleave real contenders over real files in a temporary
// directory. A contender is parked at a hook and released by the spec — never by a timer —
// so the interleaving is the same on every machine.
import { randomUUID } from 'node:crypto';
import { link, mkdir, mkdtemp, readFile, readdir, rename, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { withSpoolLock } from './spoolLock.js';
import {
  deadPid,
  expectError,
  failureOf,
  fixedNow,
  writerLockRecord as lockText,
} from './spoolLock.kit.test.js';
import {
  spoolLockFileName,
  spoolLockFileMode,
  SpoolLockedError,
  takeoverLockFileName,
} from './spoolLockTypes.js';

/**
 * A rendezvous the spec opens by hand. `Promise.withResolvers` is ES2024 and the workspace
 * targets ES2022, so the executor form is the only option here.
 */
function deferred(): { readonly promise: Promise<void>; readonly resolve: () => void } {
  let open: () => void = () => undefined;
  const promise = new Promise<void>((settle) => {
    open = () => {
      settle();
    };
  });
  return { promise, resolve: open };
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
    const aParked = deferred();
    const releaseA = deferred();
    const aQuarantined = deferred();
    const releaseQuarantine = deferred();
    const bRunning = deferred();
    const releaseB = deferred();
    const cRunning = deferred();
    const releaseC = deferred();
    // Every contender is alive to the others; only the planted lock names a dead pid.
    const alivePids = [1111, 2222, 3333];
    const probe = (pid: number): boolean => alivePids.includes(pid);
    let inFlight = 0;
    let maxInFlight = 0;
    const enter = (): void => {
      inFlight += 1;
      maxInFlight = Math.max(maxInFlight, inFlight);
    };
    const leave = (): void => {
      inFlight -= 1;
    };

    // A observes the dead lock and parks before it may touch the path.
    const a = withSpoolLock(
      directory,
      () => {
        enter();
        try {
          return Promise.resolve('a');
        } finally {
          leave();
        }
      },
      {
        pid: 1111,
        now: () => fixedNow,
        isProcessAlive: probe,
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
      },
    );
    const aFailure = failureOf(a);
    await aParked.promise;

    // B takes over the dead lock, links its own and parks inside the callback.
    const b = withSpoolLock(
      directory,
      async () => {
        enter();
        bRunning.resolve();
        try {
          await releaseB.promise;
          return 'b';
        } finally {
          leave();
        }
      },
      { pid: 2222, now: () => fixedNow, isProcessAlive: probe },
    );
    await bRunning.promise;

    releaseA.resolve();
    // Either A is refused at once, or it parked after a rename it should never have done.
    const aPhase = await Promise.race([
      aFailure.then(() => 'refused' as const),
      aQuarantined.promise.then(() => 'quarantined' as const),
    ]);

    const c = withSpoolLock(
      directory,
      async () => {
        enter();
        cRunning.resolve();
        try {
          await releaseC.promise;
          return 'c';
        } finally {
          leave();
        }
      },
      { pid: 3333, now: () => fixedNow, isProcessAlive: probe },
    );
    const cPhase = await Promise.race([
      c.then(
        () => 'entered' as const,
        () => 'refused' as const,
      ),
      cRunning.promise.then(() => 'entered' as const),
    ]);

    try {
      expect(cPhase).toBe('refused');
      expect(aPhase).toBe('refused');
      const locked = expectError(await aFailure, SpoolLockedError);
      expect(locked.code).toBe('spool_locked');
      expect(locked.holderPid).toBe(2222);
      // B's lock was never moved: the path still names B and nothing is quarantined.
      expect(await readFile(lockPath, 'utf8')).toBe(lockText(2222));
      expect(await readdir(directory)).toEqual([spoolLockFileName]);
      expect(maxInFlight).toBe(1);
    } finally {
      releaseB.resolve();
      releaseQuarantine.resolve();
      releaseC.resolve();
      await Promise.allSettled([a, b, c]);
    }
    expect(await readdir(directory)).toEqual([]);
  });

  it('refuses a contender whose unreadable entry was released before its inode re-check', async () => {
    // The observed lock records nothing (an empty file a crash left behind), so it is only a
    // name: the entry is released while we read it, and a live successor takes the path.
    await writeFile(lockPath, '', { mode: spoolLockFileMode });
    const entryRead = deferred();
    const releaseA = deferred();
    const aQuarantined = deferred();
    const releaseQuarantine = deferred();
    const bRunning = deferred();
    const releaseB = deferred();
    const cRunning = deferred();
    const releaseC = deferred();
    const alivePids = [1111, 2222, 3333];
    const probe = (pid: number): boolean => alivePids.includes(pid);
    let inFlight = 0;
    let maxInFlight = 0;
    const enter = (): void => {
      inFlight += 1;
      maxInFlight = Math.max(maxInFlight, inFlight);
    };
    const leave = (): void => {
      inFlight -= 1;
    };

    const a = withSpoolLock(
      directory,
      () => {
        enter();
        try {
          return Promise.resolve('a');
        } finally {
          leave();
        }
      },
      {
        pid: 1111,
        now: () => fixedNow,
        isProcessAlive: probe,
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
      },
    );
    const aFailure = failureOf(a);
    await entryRead.promise;

    // The empty entry is released and B links its own lock into the freed name. A hard link
    // keeps the released inode alive, so B's lock cannot land on its number and the re-stat
    // below differs from the observed entry on every filesystem.
    const releasedEntry = join(root, 'released-empty-entry');
    await link(lockPath, releasedEntry);
    await rm(lockPath);
    const b = withSpoolLock(
      directory,
      async () => {
        enter();
        bRunning.resolve();
        try {
          await releaseB.promise;
          return 'b';
        } finally {
          leave();
        }
      },
      { pid: 2222, now: () => fixedNow, isProcessAlive: probe },
    );
    await bRunning.promise;

    releaseA.resolve();
    const aPhase = await Promise.race([
      aFailure.then(() => 'refused' as const),
      aQuarantined.promise.then(() => 'quarantined' as const),
    ]);
    const c = withSpoolLock(
      directory,
      async () => {
        enter();
        cRunning.resolve();
        try {
          await releaseC.promise;
          return 'c';
        } finally {
          leave();
        }
      },
      { pid: 3333, now: () => fixedNow, isProcessAlive: probe },
    );
    const cPhase = await Promise.race([
      c.then(
        () => 'entered' as const,
        () => 'refused' as const,
      ),
      cRunning.promise.then(() => 'entered' as const),
    ]);

    try {
      expect(cPhase).toBe('refused');
      expect(aPhase).toBe('refused');
      expect(expectError(await aFailure, SpoolLockedError).holderPid).toBe(2222);
      expect(await readFile(lockPath, 'utf8')).toBe(lockText(2222));
      expect(await readdir(directory)).toEqual([spoolLockFileName]);
      expect(maxInFlight).toBe(1);
    } finally {
      releaseB.resolve();
      releaseQuarantine.resolve();
      releaseC.resolve();
      await Promise.allSettled([a, b, c]);
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
    const reclaimParked = deferred();
    const releaseReclaim = deferred();
    const quarantined = deferred();
    const releaseQuarantine = deferred();
    // Every contender is alive to the others; the planted lock and mutex name dead pids.
    const alivePids = [1111, 2222];
    const probe = (pid: number): boolean => alivePids.includes(pid);
    // Which contenders got as far as holding the lock: exactly one may ever appear here.
    const holders: string[] = [];

    const delayed = withSpoolLock(
      directory,
      () => {
        holders.push('delayed');
        return Promise.resolve('delayed');
      },
      {
        pid: 1111,
        now: () => fixedNow,
        isProcessAlive: probe,
        hooks: {
          beforeDeadMutexReclaim: async () => {
            reclaimParked.resolve();
            await releaseReclaim.promise;
          },
        },
      },
    );
    const delayedFailure = failureOf(delayed);
    await reclaimParked.promise;

    // The fast contender reclaims that mutex, takes it, quarantines the stale lock and then
    // holds the mutex while the delayed breaker resumes.
    const fast = withSpoolLock(
      directory,
      () => {
        holders.push('fast');
        return Promise.resolve('fast');
      },
      {
        pid: 2222,
        now: () => fixedNow,
        isProcessAlive: probe,
        hooks: {
          afterQuarantine: async () => {
            quarantined.resolve();
            await releaseQuarantine.promise;
          },
        },
      },
    );
    await quarantined.promise;

    releaseReclaim.resolve();
    try {
      const locked = expectError(await delayedFailure, SpoolLockedError);
      expect(locked.code).toBe('spool_locked');
      // The refused breaker names the taker in the mutex it moved, and that mutex is back.
      expect(locked.holderPid).toBe(2222);
      expect(await readFile(mutexPath, 'utf8')).toBe(lockText(2222));
    } finally {
      releaseQuarantine.resolve();
    }
    expect(await fast).toBe('fast');
    // One holder, and it is the contender whose live mutex the delayed breaker found.
    expect(holders).toEqual(['fast']);
    expect(await readdir(directory)).toEqual([]);
  });
});
