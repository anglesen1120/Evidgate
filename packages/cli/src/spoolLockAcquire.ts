// The acquire half of the lock protocol: claiming `spool.lock` by hard-linking a fully
// written temp file, breaking a stale lock by a verified rename-aside takeover, and
// releasing only the inode we still own. Every step keeps its order (write, fsync, close,
// link, rename) because a crash between any two of them must leave a readable lock.
import { randomUUID } from 'node:crypto';
import { link, lstat, open, readdir, rename, unlink } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

import { CliError } from './errors.js';
import {
  inspectionHint,
  isProcessAlive,
  LockInspectionError,
  readSpoolLock,
  readSpoolLockPath,
} from './spoolLockRead.js';
import {
  defaultTakeoverAttempts,
  spoolLockFileName,
  spoolLockFileMode,
  staleProbeDelayMs,
  SpoolLockedError,
  type SpoolLockFile,
  type SpoolLockOptions,
} from './spoolLockTypes.js';

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
  /** `(dev, ino)` of the lock inode, so release can find it after a takeover renamed it. */
  readonly dev: number;
  readonly ino: number;
}

/** Identity of the inode the lock record lives in. */
interface LockClaim {
  readonly dev: number;
  readonly ino: number;
}

/** A lock record read from the path that beat us, or a file that is not a lock at all. */
type ObservedLock =
  | { readonly kind: 'lock'; readonly pid: number; readonly startedAt: string }
  | { readonly kind: 'unreadable' };

/** What one loser learned about the holder that beat it. */
type Observation =
  | { readonly kind: 'held'; readonly pid?: number }
  | { readonly kind: 'stale'; readonly observed: ObservedLock };

/** What a verified takeover attempt did. */
type TakeoverOutcome =
  | { readonly kind: 'taken' }
  | { readonly kind: 'retry' }
  | { readonly kind: 'held'; readonly pid?: number };

/**
 * Claims the lock by hard-linking a fully written temp file to `spool.lock`, so exactly one
 * process wins and the path never shows a partial record. A loser observes the holder: a
 * live pid is `spool_locked`; a dead pid (or a foreign file) is broken by a verified
 * takeover and the link is retried a bounded number of times.
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
    const claim = await createLockAtomically(directory, path, pid, startedAt);
    if (claim !== null) return { path, pid, startedAt, ...claim };
    const observation = await observeHeldLock(directory, alive);
    if (observation.kind === 'held') throw new SpoolLockedError(path, observation.pid);
    const outcome = await takeOverObservedLock(directory, path, observation.observed, alive);
    if (outcome.kind === 'held') throw new SpoolLockedError(path, outcome.pid);
    // 'taken' and 'retry' both leave the slot to the next attempt, which links again.
  }
  throw new SpoolLockedError(path);
}

/** The whole lock record: `{ pid, started_at }` and nothing else. */
function lockRecord(pid: number, startedAt: string): string {
  return `${JSON.stringify({ pid, started_at: startedAt })}\n`;
}

/**
 * Creates `spool.lock` atomically *with* its content. The record goes to a unique temp file
 * in the same directory (`O_CREAT|O_EXCL`, mode 0600), is fsynced, and is then hard-linked
 * to `path`: `link` is the single atomic step that decides the winner, and it never exposes
 * a partially written lock. Returns the claimed inode, or `null` when another process
 * already holds the lock.
 */
async function createLockAtomically(
  directory: string,
  path: string,
  pid: number,
  startedAt: string,
): Promise<LockClaim | null> {
  const tempPath = join(directory, `${spoolLockFileName}.${pid}.${randomUUID()}.tmp`);
  const handle = await open(tempPath, 'wx', spoolLockFileMode);
  let claim: LockClaim;
  try {
    await handle.writeFile(lockRecord(pid, startedAt), 'utf8');
    await handle.sync();
    const { dev, ino } = await handle.stat();
    claim = { dev, ino };
  } catch (error) {
    await handle.close().catch(() => undefined);
    await removeIfPresent(tempPath);
    throw error;
  }
  try {
    // `close` sits inside the guarded region: a failed close must not leave the staging
    // name behind (the `finally` removes it whatever happened).
    await handle.close();
    await link(tempPath, path);
    return claim;
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    if (code === 'EEXIST') return null;
    if (isHardLinkUnsupportedErrno(code)) throw hardLinkUnsupportedError(code);
    throw error;
  } finally {
    // The lock keeps its inode through the link; the temp name is only a staging entry.
    await removeIfPresent(tempPath);
  }
}

/**
 * Errnos a `link` fails with when the filesystem cannot hard-link the record into place
 * (FAT/exFAT, some network and overlay mounts). The lock would need an unsound fallback, so
 * the acquisition is refused with an actionable error instead.
 */
export function isHardLinkUnsupportedErrno(code: string | undefined): boolean {
  return (
    code === 'EPERM' ||
    code === 'ENOTSUP' ||
    code === 'EOPNOTSUPP' ||
    code === 'EXDEV' ||
    code === 'ENOSYS'
  );
}

function hardLinkUnsupportedError(code: string | undefined): CliError {
  return new CliError(
    `cannot create the spool lock (${code ?? 'unknown OS error'}): the spool directory must be on a filesystem that supports hard links`,
    {
      code: 'spool_unsafe_path',
      hint: 'point EVIDGATE_SPOOL_DIR at a local filesystem that supports hard links',
    },
  );
}

/**
 * Reads the lock that beat us and decides whether its holder is alive. A record naming a
 * live pid is final; a dead pid is stale at once. An unparseable file (torn, empty, foreign,
 * a FIFO or a symlink) is re-read once after {@link staleProbeDelayMs} so that a foreign
 * writer caught mid-create is not mistaken for a crashed one.
 */
async function observeHeldLock(
  directory: string,
  alive: (pid: number) => boolean,
): Promise<Observation> {
  for (let probe = 0; probe < 2; probe += 1) {
    if (probe > 0) await delay(staleProbeDelayMs);
    const holder = await readSpoolLock(directory);
    if (holder === null) continue;
    if (alive(holder.pid)) return { kind: 'held', pid: holder.pid };
    return {
      kind: 'stale',
      observed: { kind: 'lock', pid: holder.pid, startedAt: holder.startedAt },
    };
  }
  return { kind: 'stale', observed: { kind: 'unreadable' } };
}

/**
 * Breaks a stale lock without ever destroying a successor's lock.
 *
 * `rename` is the only path operation that removes an entry atomically: whatever sits at
 * `spool.lock` at that instant moves aside, so a lock created after our read can never be
 * unlinked by us. The quarantined file is re-read and deleted only when it is still the lock
 * we observed as stale — same `(pid, started_at)`, pid still dead, or still unparseable.
 * Anything else means we moved a lock we had not observed: it is put back with
 * link-if-absent (`link` fails rather than overwrite, so a lock created in the meantime
 * wins) and reported as held.
 *
 * Residual window: a third process that links its own lock in the instant between our
 * `rename` and the restore makes the restore fail with EEXIST. It then holds the path
 * legitimately, and the lock we moved cannot be put back — POSIX path operations cannot
 * close that window. It needs a stale lock plus three racing CLIs, and release is content
 * checked, so the displaced holder still never deletes its successor's lock.
 */
async function takeOverObservedLock(
  directory: string,
  path: string,
  observed: ObservedLock,
  alive: (pid: number) => boolean,
): Promise<TakeoverOutcome> {
  const quarantined = join(directory, `${spoolLockFileName}.${process.pid}.${randomUUID()}.stale`);
  try {
    await rename(path, quarantined);
  } catch (error) {
    // ENOENT: another loser moved the stale lock first; the caller retries the link.
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return { kind: 'retry' };
    throw error;
  }
  let moved: SpoolLockFile | null;
  try {
    moved = await readSpoolLockPath(quarantined);
  } catch (error) {
    // The path is empty at this instant: hand the quarantined lock back before failing, so
    // the failure never leaves a live holder's lock detached from its path.
    const errno = error instanceof LockInspectionError ? error.errno : undefined;
    const restored = await restoreQuarantinedLock(quarantined, path);
    throw new CliError(
      restored
        ? `cannot inspect the quarantined spool lock (${errno ?? 'unknown OS error'}); the takeover was abandoned and the lock put back`
        : `cannot inspect the quarantined spool lock (${errno ?? 'unknown OS error'}); the takeover was abandoned and another lock holds the path`,
      { code: 'spool_unsafe_path', hint: inspectionHint },
    );
  }
  const stillTheObservedStaleLock =
    matchesObserved(observed, moved) && (moved === null || !alive(moved.pid));
  if (!stillTheObservedStaleLock) {
    await restoreQuarantinedLock(quarantined, path);
    const current = await readSpoolLock(directory);
    return current === null ? { kind: 'retry' } : { kind: 'held', pid: current.pid };
  }
  await removeIfPresent(quarantined);
  return { kind: 'taken' };
}

/** Whether a quarantined lock is byte-for-byte the state we observed before the rename. */
function matchesObserved(observed: ObservedLock, moved: SpoolLockFile | null): boolean {
  if (observed.kind === 'unreadable') return moved === null;
  return moved !== null && moved.pid === observed.pid && moved.startedAt === observed.startedAt;
}

/**
 * Puts a quarantined lock back at `path` unless a newer lock already took the slot.
 * Returns whether the moved lock is back at the path.
 */
async function restoreQuarantinedLock(quarantined: string, path: string): Promise<boolean> {
  let restored = true;
  try {
    await link(quarantined, path);
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    // EEXIST: a newer lock owns the path. ENOENT: the quarantined file is already gone.
    restored = false;
    if (code !== 'EEXIST' && code !== 'ENOENT') throw error;
  } finally {
    await removeIfPresent(quarantined);
  }
  return restored;
}

async function removeIfPresent(path: string): Promise<void> {
  await unlink(path).catch((error: unknown) => {
    if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
  });
}

/**
 * Removes the lock only when it is still ours: a lock taken over by another process (or
 * rewritten by it) is left untouched, so a slow holder can never delete its successor's
 * lock. When the path no longer carries a lock at all, a concurrent verified takeover may
 * have our record quarantined (renamed aside) at the moment we released; that copy is
 * removed by inode, so no phantom `spool.lock` naming live pid can survive.
 */
async function releaseSpoolLock(held: HeldSpoolLock): Promise<void> {
  let holder: SpoolLockFile | null;
  try {
    holder = await readSpoolLockPath(held.path);
  } catch {
    // The lock cannot be inspected, so it cannot be removed safely either: leave it in
    // place (our pid dies with this process, so a later run sees a stale lock) and never
    // replace `fn`'s result or error with a release failure.
    return;
  }
  if (holder === null) {
    await scavengeQuarantinedLock(held);
    return;
  }
  if (holder.pid !== held.pid || holder.startedAt !== held.startedAt) return;
  await removeIfPresent(held.path);
}

/**
 * Deletes a quarantined copy of our own lock. A takeover renames the lock aside before it
 * decides whether to break it; if this holder releases inside that window, its unlink finds
 * no path and the taker's restore finds no quarantined file either, so the record would
 * survive as a lock naming a live pid that no longer holds anything. `(dev, ino)` still
 * identifies the inode under whatever name the taker chose; only that exact inode is removed.
 */
async function scavengeQuarantinedLock(held: HeldSpoolLock): Promise<void> {
  const directory = dirname(held.path);
  let names: string[];
  try {
    names = await readdir(directory);
  } catch {
    return;
  }
  const prefix = `${spoolLockFileName}.`;
  for (const name of names) {
    if (!name.startsWith(prefix) || !name.endsWith('.stale')) continue;
    const candidate = join(directory, name);
    const stats = await lstat(candidate).catch(() => null);
    if (stats === null || stats.dev !== held.dev || stats.ino !== held.ino) continue;
    await removeIfPresent(candidate);
  }
}
