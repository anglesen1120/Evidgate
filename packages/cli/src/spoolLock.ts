// 04.2.3-T1.1: the CLI-side single-writer lock on `<spool_dir>/<run_id>/<producer_id>`.
//
// D2 of the 04.2.3 contract: `events send` and `runs finalize` are the only CLI writers of
// a producer directory, and they must never interleave with a second CLI process (the
// merged reporter takes no lock). The lock is an `O_CREAT|O_EXCL` file, `spool.lock`,
// mode 0600, whose whole content is `{ pid, started_at }` — no token, no argv, nothing a
// support bundle could leak. The holder is stale only when its pid is dead on this host
// (pid reuse is accepted as a risk: a lock naming a live pid is never taken over).
//
// The lock is advisory within this package: it guards a directory, not an fd, so every
// reader/writer of the spool goes through `withSpoolLock`.
import { constants as fsConstants } from 'node:fs';
import { open, unlink } from 'node:fs/promises';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

import type { FileHandle } from 'node:fs/promises';

import { CliError } from './errors.js';

/** The lock file name inside a producer directory. */
export const spoolLockFileName = 'spool.lock';
/** `spool.lock` is created 0600 like every other spool file. */
export const spoolLockFileMode = 0o600;

/**
 * `O_NOFOLLOW` is undefined on Windows, where the POSIX symlink hardening does not apply
 * (the same degrade the merged writer uses).
 */
const O_NOFOLLOW: number = process.platform === 'win32' ? 0 : fsConstants.O_NOFOLLOW;

/**
 * A holder that has created the lock file but not yet written its content is
 * indistinguishable from a crashed one; re-read once after this pause before deciding it
 * is stale.
 */
const staleProbeDelayMs = 10;
/** Bounded takeover attempts, so two processes racing over a dead lock cannot loop. */
const defaultTakeoverAttempts = 3;

/** The lock file's whole content (task file §4 / D2). Nothing else is ever written. */
export interface SpoolLockFile {
  readonly pid: number;
  readonly startedAt: string;
}

export interface SpoolLockOptions {
  /** Pid recorded in the lock. Defaults to `process.pid`; injected by tests. */
  readonly pid?: number;
  /** Clock for `started_at`. Defaults to `new Date()`. */
  readonly now?: () => Date;
  /** Liveness probe. Defaults to {@link isProcessAlive}; injected by tests. */
  readonly isProcessAlive?: (pid: number) => boolean;
  /** Attempts allowed while breaking a stale lock. Defaults to 3. */
  readonly maxTakeoverAttempts?: number;
}

/** `spool_locked`: a live process holds this producer directory (exit 3, contracts/cli.md). */
export class SpoolLockedError extends CliError {
  /** The lock file that is held. */
  readonly lockPath: string;
  /** The live holder's pid, when the lock file named one. */
  readonly holderPid?: number;

  constructor(lockPath: string, holderPid?: number) {
    super(
      holderPid === undefined
        ? `the spool directory is locked by another process (${lockPath})`
        : `the spool directory is locked by live process ${holderPid} (${lockPath})`,
      {
        code: 'spool_locked',
        hint: 'wait for the running send/finalize to finish, then retry',
      },
    );
    this.name = 'SpoolLockedError';
    this.lockPath = lockPath;
    if (holderPid !== undefined) this.holderPid = holderPid;
  }
}

/**
 * Whether `pid` names a live process on this host. `EPERM` means the process exists but is
 * owned by another user, which still counts as alive; only `ESRCH` means it is gone.
 */
export function isProcessAlive(pid: number): boolean {
  if (!Number.isSafeInteger(pid) || pid <= 0) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return (error as NodeJS.ErrnoException).code !== 'ESRCH';
  }
}

/**
 * Runs `fn` while holding the exclusive spool lock of `directory`, releasing it afterwards
 * (also when `fn` throws). A live holder makes this fail with `spool_locked`; a lock whose
 * pid is dead is taken over.
 */
export async function withSpoolLock<T>(
  directory: string,
  fn: () => Promise<T>,
  options: SpoolLockOptions = {},
): Promise<T> {
  const held = await acquireSpoolLock(directory, options);
  try {
    return await fn();
  } finally {
    await releaseSpoolLock(held);
  }
}

interface HeldSpoolLock {
  readonly path: string;
  readonly pid: number;
  readonly startedAt: string;
}

/**
 * Creates the lock file with `O_CREAT|O_EXCL`, so exactly one process wins. A loser reads
 * the holder: a live pid is `spool_locked`; a dead pid (or an unreadable lock after a
 * short re-read) is stale, and the loser unlinks it and tries again a bounded number of
 * times.
 */
async function acquireSpoolLock(
  directory: string,
  options: SpoolLockOptions,
): Promise<HeldSpoolLock> {
  const path = join(directory, spoolLockFileName);
  const pid = options.pid ?? process.pid;
  const now = options.now ?? (() => new Date());
  const alive = options.isProcessAlive ?? isProcessAlive;
  const attempts = options.maxTakeoverAttempts ?? defaultTakeoverAttempts;
  for (let attempt = 0; attempt < attempts; attempt += 1) {
    const startedAt = now().toISOString();
    const handle = await createExclusive(path);
    if (handle !== null) {
      try {
        // `{ pid, started_at }` and nothing else: the lock must never carry credential
        // material, so `readSpoolLock` and the spec can assert the exact field set.
        await handle.writeFile(`${JSON.stringify({ pid, started_at: startedAt })}\n`, 'utf8');
        await handle.sync();
      } catch (error) {
        await handle.close().catch(() => undefined);
        await unlink(path).catch(() => undefined);
        throw error;
      }
      await handle.close();
      return { path, pid, startedAt };
    }
    let holder = await readSpoolLock(directory);
    if (holder === null) {
      // The holder may be between `open` and `writeFile`; one short re-read separates a
      // create/write race from a lock left by a crash.
      await delay(staleProbeDelayMs);
      holder = await readSpoolLock(directory);
    }
    if (holder !== null && alive(holder.pid)) {
      throw new SpoolLockedError(path, holder.pid);
    }
    await unlink(path).catch((error: unknown) => {
      const code = (error as NodeJS.ErrnoException).code;
      // ENOENT: another loser already removed the stale lock.
      if (code !== 'ENOENT') throw error;
    });
  }
  throw new SpoolLockedError(path);
}

/** `open(path, 'wx')` = `O_CREAT|O_EXCL|O_WRONLY`; `null` when another process holds it. */
async function createExclusive(path: string): Promise<FileHandle | null> {
  try {
    return await open(path, 'wx', spoolLockFileMode);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'EEXIST') return null;
    throw error;
  }
}

/**
 * Reads a producer directory's lock, or `null` when there is none (or when it cannot be
 * read as a lock: a torn, empty, symlinked or foreign file is treated as stale by the
 * caller, never as a token-bearing document).
 */
export function readSpoolLock(directory: string): Promise<SpoolLockFile | null> {
  return readSpoolLockPath(join(directory, spoolLockFileName));
}

/**
 * Removes the lock only when it is still ours: a lock taken over by another process (or
 * rewritten by it) is left untouched, so a slow holder can never delete its successor's
 * lock.
 */
async function releaseSpoolLock(held: HeldSpoolLock): Promise<void> {
  const holder = await readSpoolLockPath(held.path);
  if (holder === null) return;
  if (holder.pid !== held.pid || holder.startedAt !== held.startedAt) return;
  await unlink(held.path).catch((error: unknown) => {
    if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
  });
}

async function readSpoolLockPath(path: string): Promise<SpoolLockFile | null> {
  let text: string;
  try {
    const handle = await open(path, fsConstants.O_RDONLY | O_NOFOLLOW);
    try {
      text = await handle.readFile('utf8');
    } finally {
      await handle.close();
    }
  } catch {
    return null;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return null;
  }
  if (typeof parsed !== 'object' || parsed === null) return null;
  const record: object = parsed;
  const pid: unknown = 'pid' in record ? record.pid : undefined;
  const startedAt: unknown = 'started_at' in record ? record.started_at : undefined;
  if (typeof pid !== 'number' || !Number.isSafeInteger(pid) || pid <= 0) return null;
  if (typeof startedAt !== 'string' || startedAt.length === 0) return null;
  return { pid, startedAt };
}
