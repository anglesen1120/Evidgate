// The acquire half of the lock protocol: claiming `spool.lock` by hard-linking a fully
// written temp file and breaking a stale lock only inside the takeover mutex; releasing
// lives in spoolLockRelease.ts. Every step keeps its order (write, fsync, close, link,
// rename) because a crash between any two of them must leave a readable lock.
import { randomUUID } from 'node:crypto';
import { link, lstat, open, rename } from 'node:fs/promises';
import { basename, dirname, join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

import { CliError } from './errors.js';
import { releaseSpoolLock, removeIfPresent } from './spoolLockRelease.js';
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
  takeoverLockFileName,
  type HeldSpoolLock,
  type SpoolLockFile,
  type SpoolLockHooks,
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
    await releaseSpoolLock(held, options.hooks);
  }
}

/** Identity of the inode the lock record lives in. */
interface LockClaim {
  readonly dev: number;
  readonly ino: number;
}

/** One entry on disk: which inode carries it, and what it records when it records anything. */
interface LockEntry extends LockClaim {
  readonly record: SpoolLockFile | null;
}

/** The takeover mutex this process created, identified by inode so release can verify it. */
interface HeldTakeoverMutex extends LockClaim {
  readonly path: string;
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

/** Everything one takeover attempt needs: the path, the record observed, the injected probes. */
interface TakeoverRequest {
  readonly directory: string;
  readonly path: string;
  readonly observed: ObservedLock;
  /** This contender, recorded in the takeover mutex it creates. */
  readonly pid: number;
  readonly startedAt: string;
  readonly alive: (pid: number) => boolean;
  readonly hooks: SpoolLockHooks;
}

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
  const hooks = options.hooks ?? {};
  const attempts = options.maxTakeoverAttempts ?? defaultTakeoverAttempts;
  for (let attempt = 0; attempt < attempts; attempt += 1) {
    const startedAt = now().toISOString();
    const claim = await createLockAtomically(directory, path, lockRecord(pid, startedAt));
    if (claim !== null) return { path, pid, startedAt, ...claim };
    const observation = await observeHeldLock(directory, alive);
    if (observation.kind === 'held') throw new SpoolLockedError(path, observation.pid);
    const outcome = await takeOverObservedLock({
      directory,
      path,
      observed: observation.observed,
      pid,
      startedAt,
      alive,
      hooks,
    });
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
 * Creates `path` atomically *with* its content. The record goes to a unique temp file in the
 * same directory (`O_CREAT|O_EXCL`, mode 0600), is fsynced, and is then hard-linked to
 * `path`: `link` is the single atomic step that decides the winner, and it never exposes a
 * partially written record. Returns the claimed inode, or `null` when `path` is already
 * taken.
 */
async function createLockAtomically(
  directory: string,
  path: string,
  record: string,
): Promise<LockClaim | null> {
  const tempPath = join(directory, `${basename(path)}.${randomUUID()}.tmp`);
  const handle = await open(tempPath, 'wx', spoolLockFileMode);
  let claim: LockClaim;
  try {
    await handle.writeFile(record, 'utf8');
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
 * Reads one entry without following a symlink; `null` means nothing is there. An entry that
 * is present but records nothing is re-statted after the read: when the name now carries a
 * different inode, this snapshot describes an entry that is gone, and renaming by that name
 * would move whatever took its place.
 */
async function readLockEntry(
  path: string,
  afterRead?: () => Promise<void>,
): Promise<LockEntry | null> {
  const claim = await entryClaim(path);
  if (claim === null) return null;
  const record = await readSpoolLockPath(path);
  await afterRead?.();
  if (record !== null) return { ...claim, record };
  const after = await entryClaim(path);
  if (after === null || after.dev !== claim.dev || after.ino !== claim.ino) return null;
  return { ...claim, record };
}

async function entryClaim(path: string): Promise<LockClaim | null> {
  try {
    const { dev, ino } = await lstat(path);
    return { dev, ino };
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null;
    throw error;
  }
}

/**
 * Breaks a stale lock without ever destroying a successor's lock. Only one process at a time
 * may inspect and rename, so a second contender cannot replace the record between our read
 * and our `rename`. A contender that finds a live taker is refused instead of queued: a
 * takeover lasts a few syscalls, and failing closed keeps every contender bounded.
 */
async function takeOverObservedLock(request: TakeoverRequest): Promise<TakeoverOutcome> {
  await request.hooks.beforeTakeover?.();
  const mutex = await acquireTakeoverMutex(request);
  if (mutex === null) {
    // The mutex names the taker still working on this directory; the error names it too.
    const taker = await readSpoolLockPath(join(request.directory, takeoverLockFileName)).catch(
      () => null,
    );
    return taker === null ? { kind: 'held' } : { kind: 'held', pid: taker.pid };
  }
  try {
    return await breakObservedLock(request);
  } finally {
    await releaseTakeoverMutex(mutex);
  }
}

/**
 * Serialises takers so only the mutex holder re-reads and breaks the lock. Breaking a dead
 * mutex leaves one window — a crashed taker, two simultaneous breakers, a third publisher,
 * and a contender linking into the restore gap. The post-rename verification narrows it;
 * it does not close it.
 */
async function acquireTakeoverMutex(request: TakeoverRequest): Promise<HeldTakeoverMutex | null> {
  const path = join(request.directory, takeoverLockFileName);
  for (let round = 0; round < 2; round += 1) {
    const record = lockRecord(request.pid, request.startedAt);
    const claim = await createLockAtomically(request.directory, path, record);
    if (claim !== null) return { path, ...claim };
    if (!(await breakDeadTakeoverMutex(path, request.alive, request.hooks))) return null;
  }
  return null;
}

/** Quarantines a dead mutex so a delayed breaker can verify what it actually moved. */
async function breakDeadTakeoverMutex(
  path: string,
  alive: (pid: number) => boolean,
  hooks: SpoolLockHooks,
): Promise<boolean> {
  const observed = await readLockEntry(path);
  if (observed === null) return true;
  if (observed.record !== null && alive(observed.record.pid)) return false;
  const current = await readLockEntry(path);
  if (current === null) return true;
  if (current.dev !== observed.dev || current.ino !== observed.ino) return false;
  await hooks.beforeDeadMutexReclaim?.();
  const quarantined = join(dirname(path), `takeover.${randomUUID()}.stale`);
  try {
    await rename(path, quarantined);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return true;
    throw error;
  }
  let moved: LockEntry | null;
  try {
    moved = await readLockEntry(quarantined);
  } catch (error) {
    // The moved mutex cannot be inspected: put it back instead of leaving the slot free.
    await restoreQuarantinedLock(quarantined, path);
    throw error;
  }
  // The quarantine name is gone, so nothing was taken and the slot is free to link.
  if (moved === null) return true;
  // A breaker that resumed late may have moved a live taker's mutex: delete only the record
  // we observed, put anything else back (link-if-absent) and report the mutex as held.
  const sameRecord =
    observed.record === null
      ? moved.record === null
      : moved.record !== null &&
        moved.record.pid === observed.record.pid &&
        moved.record.startedAt === observed.record.startedAt;
  if (sameRecord && (moved.record === null || !alive(moved.record.pid))) {
    await removeIfPresent(quarantined);
    return true;
  }
  await restoreQuarantinedLock(quarantined, path);
  return false;
}

/**
 * Renames the observed lock aside. The re-read under the mutex is what makes the rename
 * safe: a lock linked after our observation — a successor we never saw — is not the record
 * we observed, so it stays at the path and is reported instead of being moved.
 */
async function breakObservedLock(request: TakeoverRequest): Promise<TakeoverOutcome> {
  const entry = await readLockEntry(request.path, request.hooks.afterEntryRead);
  if (entry === null) return { kind: 'retry' };
  if (entry.record !== null && request.alive(entry.record.pid)) {
    return { kind: 'held', pid: entry.record.pid };
  }
  if (!matchesObserved(request.observed, entry.record)) return { kind: 'retry' };
  const quarantined = join(
    request.directory,
    `${spoolLockFileName}.${request.pid}.${randomUUID()}.stale`,
  );
  try {
    await rename(request.path, quarantined);
  } catch (error) {
    // ENOENT: the entry vanished under us; the caller retries the link.
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return { kind: 'retry' };
    throw error;
  }
  await request.hooks.afterQuarantine?.();
  return verifyQuarantinedLock(request, quarantined);
}

/**
 * Keeps the post-rename check as defence in depth: a writer that ignores the mutex can still
 * put a different lock at the path between our read and our `rename`. Such a lock is put
 * back with link-if-absent; a newer lock that won the path in the gap displaces it.
 */
async function verifyQuarantinedLock(
  request: TakeoverRequest,
  quarantined: string,
): Promise<TakeoverOutcome> {
  let moved: SpoolLockFile | null;
  try {
    moved = await readSpoolLockPath(quarantined);
  } catch (error) {
    const errno = error instanceof LockInspectionError ? error.errno : undefined;
    const restored = await restoreQuarantinedLock(quarantined, request.path);
    throw new CliError(
      restored
        ? `cannot inspect the quarantined spool lock (${errno ?? 'unknown OS error'}); the takeover was abandoned and the lock put back`
        : `cannot inspect the quarantined spool lock (${errno ?? 'unknown OS error'}); the takeover was abandoned and another lock holds the path`,
      { code: 'spool_unsafe_path', hint: inspectionHint },
    );
  }
  const stillTheObservedStaleLock =
    matchesObserved(request.observed, moved) && (moved === null || !request.alive(moved.pid));
  if (!stillTheObservedStaleLock) {
    await restoreQuarantinedLock(quarantined, request.path);
    const current = await readSpoolLock(request.directory);
    return current === null ? { kind: 'retry' } : { kind: 'held', pid: current.pid };
  }
  await removeIfPresent(quarantined);
  return { kind: 'taken' };
}

/**
 * Removes the mutex only while it still carries this inode. A release that cannot inspect it
 * leaves it in place: it records our pid, so the next contender breaks it once we are gone,
 * and release never masks the takeover's own outcome.
 */
async function releaseTakeoverMutex(mutex: HeldTakeoverMutex): Promise<void> {
  const current = await readLockEntry(mutex.path).catch(() => null);
  if (current === null || current.dev !== mutex.dev || current.ino !== mutex.ino) return;
  await removeIfPresent(mutex.path).catch(() => undefined);
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
