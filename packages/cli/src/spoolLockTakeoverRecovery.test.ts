// Post-rename takeover outcomes: an uninspectable quarantine, a quarantine that no longer
// matches the observed lock, a mutex replaced or already gone, and two exhausted attempt
// rounds. Faults are injected at the fs calls the CLI already makes, never by timing.
import { link, mkdir, mkdtemp, readdir, readFile, rm, unlink, writeFile } from 'node:fs/promises';
import * as fsModule from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { CliError } from './errors.js';
import { withSpoolLock } from './spoolLock.js';
import { deadPid, fixedNow, writerLockRecord as lockText } from './spoolLock.testing.js';
import { expectError, failureOf } from './spool.testing.js';
import {
  spoolLockFileName,
  spoolLockFileMode,
  SpoolLockedError,
  takeoverLockFileName,
} from './spoolLockTypes.js';

type FaultOp = 'open' | 'link';

interface FsFault {
  readonly op: FaultOp;
  readonly pathSuffix: string;
  readonly code: string;
  remaining: number;
}

const injected = vi.hoisted(() => ({ faults: [] as FsFault[] }));

vi.mock('node:fs/promises', async (importOriginal) => {
  const actual = await importOriginal<typeof fsModule>();
  const faultCode = (op: FaultOp, path: string): string | null => {
    const fault = injected.faults.find(
      (candidate) =>
        candidate.op === op && candidate.remaining > 0 && path.endsWith(candidate.pathSuffix),
    );
    if (fault === undefined) return null;
    fault.remaining -= 1;
    return fault.code;
  };
  const ioFault = (code: string): NodeJS.ErrnoException => {
    const error: NodeJS.ErrnoException = new Error(`injected ${code}`);
    error.code = code;
    return error;
  };
  return {
    ...actual,
    open: (...args: Parameters<typeof actual.open>) => {
      const [path] = args;
      const code = typeof path === 'string' ? faultCode('open', path) : null;
      return code === null ? actual.open(...args) : Promise.reject(ioFault(code));
    },
    link: (...args: Parameters<typeof actual.link>) => {
      const [, created] = args;
      const code = typeof created === 'string' ? faultCode('link', created) : null;
      return code === null ? actual.link(...args) : Promise.reject(ioFault(code));
    },
  };
});

function injectFault(op: FaultOp, pathSuffix: string, code: string, times = 1): void {
  injected.faults.push({ op, pathSuffix, code, remaining: times });
}

describe('withSpoolLock — post-rename takeover', () => {
  let root: string;
  let directory: string;
  let lockPath: string;
  let mutexPath: string;

  beforeEach(async () => {
    injected.faults.length = 0;
    root = await mkdtemp(join(tmpdir(), 'evidgate-lock-verify-'));
    directory = join(root, 'producer');
    await mkdir(directory, { recursive: true, mode: 0o700 });
    lockPath = join(directory, spoolLockFileName);
    mutexPath = join(directory, takeoverLockFileName);
  });

  afterEach(async () => {
    injected.faults.length = 0;
    await rm(root, { recursive: true, force: true });
  });

  async function quarantineName(): Promise<string> {
    const names = await readdir(directory);
    const found = names.find(
      (name) => name.startsWith(`${spoolLockFileName}.`) && name.endsWith('.stale'),
    );
    if (found === undefined) throw new Error(`no quarantined lock among ${names.join(', ')}`);
    return join(directory, found);
  }

  it('puts a quarantined lock back when it cannot be inspected', async () => {
    await writeFile(lockPath, lockText(deadPid), { mode: spoolLockFileMode });
    injectFault('open', '.stale', 'EIO');
    const failure = failureOf(
      withSpoolLock(directory, () => Promise.resolve('ran'), {
        pid: 5555,
        now: () => fixedNow,
        isProcessAlive: (pid) => pid !== deadPid,
      }),
    );
    const error = expectError(await failure, CliError);
    expect(error.code).toBe('spool_unsafe_path');
    expect(error.message).toContain('cannot inspect the quarantined spool lock (EIO)');
    expect(error.message).toContain('the takeover was abandoned and the lock put back');
    // The dead lock's record is back at the path and no quarantine name survives.
    expect(await readFile(lockPath, 'utf8')).toBe(lockText(deadPid));
    expect(await readdir(directory)).toEqual([spoolLockFileName]);
  });

  it('names the other lock when an uninspectable quarantine cannot be put back', async () => {
    await writeFile(lockPath, lockText(deadPid), { mode: spoolLockFileMode });
    const successor = lockText(7777);
    injectFault('open', '.stale', 'EIO');
    const failure = failureOf(
      withSpoolLock(directory, () => Promise.resolve('ran'), {
        pid: 5555,
        now: () => fixedNow,
        isProcessAlive: (pid) => pid !== deadPid,
        hooks: {
          afterQuarantine: async () => {
            await writeFile(lockPath, successor, { mode: spoolLockFileMode });
          },
        },
      }),
    );
    const error = expectError(await failure, CliError);
    expect(error.code).toBe('spool_unsafe_path');
    expect(error.message).toContain('another lock holds the path');
    // The successor keeps the path; the quarantine was removed after the failed restore.
    expect(await readFile(lockPath, 'utf8')).toBe(successor);
    expect(await readdir(directory)).toEqual([spoolLockFileName]);
  });

  it('reports the successor that took the path when the quarantine stopped matching', async () => {
    await writeFile(lockPath, lockText(deadPid), { mode: spoolLockFileMode });
    const successor = lockText(7777);
    const failure = failureOf(
      withSpoolLock(directory, () => Promise.resolve('ran'), {
        pid: 5555,
        now: () => fixedNow,
        isProcessAlive: (pid) => pid !== deadPid,
        hooks: {
          afterQuarantine: async () => {
            await unlink(await quarantineName());
            await writeFile(lockPath, successor, { mode: spoolLockFileMode });
          },
        },
      }),
    );
    const locked = expectError(await failure, SpoolLockedError);
    expect(locked.code).toBe('spool_locked');
    expect(locked.holderPid).toBe(7777);
    expect(await readFile(lockPath, 'utf8')).toBe(successor);
    expect(await readdir(directory)).toEqual([spoolLockFileName]);
  });

  it('reports a live holder when the retained quarantine stopped matching', async () => {
    await writeFile(lockPath, lockText(deadPid), { mode: spoolLockFileMode });
    const successor = lockText(7777);
    let entered = false;
    const failure = failureOf(
      withSpoolLock(
        directory,
        () => {
          entered = true;
          return Promise.resolve('ran');
        },
        {
          pid: 5555,
          now: () => fixedNow,
          // Only the planted record is dead; the quarantine's new holder stays alive.
          isProcessAlive: (pid) => pid !== deadPid,
          hooks: {
            afterQuarantine: async () => {
              // The rename survives, but the quarantined inode now carries a live holder.
              await writeFile(await quarantineName(), successor, { mode: spoolLockFileMode });
            },
          },
        },
      ),
    );
    const locked = expectError(await failure, SpoolLockedError);
    expect(locked.code).toBe('spool_locked');
    expect(locked.holderPid).toBe(7777);
    expect(entered).toBe(false);
    // The live record is back at the original path and no quarantine name survives.
    expect(await readFile(lockPath, 'utf8')).toBe(successor);
    expect(await readdir(directory)).toEqual([spoolLockFileName]);
  });

  it('retries the link when the quarantine vanished and nothing replaced it', async () => {
    await writeFile(lockPath, lockText(deadPid), { mode: spoolLockFileMode });
    const result = await withSpoolLock(directory, () => Promise.resolve('ran'), {
      pid: 5555,
      now: () => fixedNow,
      isProcessAlive: (pid) => pid !== deadPid,
      hooks: {
        afterQuarantine: async () => {
          await unlink(await quarantineName());
        },
      },
    });
    expect(result).toBe('ran');
    expect(await readdir(directory)).toEqual([]);
  });

  it('tolerates a takeover mutex another process removed while it was held', async () => {
    await writeFile(lockPath, lockText(deadPid), { mode: spoolLockFileMode });
    const result = await withSpoolLock(directory, () => Promise.resolve('ran'), {
      pid: 5555,
      now: () => fixedNow,
      isProcessAlive: (pid) => pid !== deadPid,
      hooks: {
        afterQuarantine: async () => {
          await rm(mutexPath, { force: true });
        },
      },
    });
    expect(result).toBe('ran');
    expect(await readdir(directory)).toEqual([]);
  });

  it('leaves a takeover mutex whose inode another process replaced', async () => {
    await writeFile(lockPath, lockText(deadPid), { mode: spoolLockFileMode });
    const successorMutex = lockText(8888);
    // Pinning the held mutex keeps its inode alive, so the replacement cannot reuse the
    // number and the release always sees a different inode.
    const pinned = join(root, 'pinned-mutex');
    const result = await withSpoolLock(directory, () => Promise.resolve('ran'), {
      pid: 5555,
      now: () => fixedNow,
      isProcessAlive: (pid) => pid !== deadPid && pid !== 8888,
      hooks: {
        afterQuarantine: async () => {
          await link(mutexPath, pinned);
          await rm(mutexPath, { force: true });
          await writeFile(mutexPath, successorMutex, { mode: spoolLockFileMode });
        },
      },
    });
    expect(result).toBe('ran');
    expect(await readFile(mutexPath, 'utf8')).toBe(successorMutex);
    expect(await readdir(directory)).toEqual([takeoverLockFileName]);
  });

  it('aborts without moving anything when the takeover mutex cannot be inspected', async () => {
    await writeFile(lockPath, lockText(deadPid), { mode: spoolLockFileMode });
    await writeFile(mutexPath, lockText(4444), { mode: spoolLockFileMode });
    injectFault('open', takeoverLockFileName, 'EIO');
    const failure = failureOf(
      withSpoolLock(directory, () => Promise.resolve('ran'), {
        pid: 5555,
        now: () => fixedNow,
        isProcessAlive: (pid) => pid !== deadPid && pid !== 4444,
      }),
    );
    const error = expectError(await failure, CliError);
    expect(error.code).toBe('spool_unsafe_path');
    expect(error.message).toContain('cannot inspect the spool lock (EIO)');
    expect(await readFile(lockPath, 'utf8')).toBe(lockText(deadPid));
    expect(await readFile(mutexPath, 'utf8')).toBe(lockText(4444));
    expect((await readdir(directory)).sort()).toEqual(
      [spoolLockFileName, takeoverLockFileName].sort(),
    );
  });

  it('gives up after two mutex rounds when every attempt loses the link', async () => {
    await writeFile(lockPath, lockText(deadPid), { mode: spoolLockFileMode });
    injectFault('link', takeoverLockFileName, 'EEXIST', 2);
    const failure = failureOf(
      withSpoolLock(directory, () => Promise.resolve('ran'), {
        pid: 5555,
        now: () => fixedNow,
        isProcessAlive: (pid) => pid !== deadPid,
      }),
    );
    const locked = expectError(await failure, SpoolLockedError);
    expect(locked.code).toBe('spool_locked');
    expect(locked.holderPid).toBeUndefined();
    // Both rounds consumed a fault, so the loop ran twice before it gave up.
    expect(injected.faults.map((fault) => fault.remaining)).toEqual([0]);
    expect(await readFile(lockPath, 'utf8')).toBe(lockText(deadPid));
    expect(await readdir(directory)).toEqual([spoolLockFileName]);
  });
});
