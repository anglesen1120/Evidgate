// 04.2.3-T1.1: the CLI-side single-writer lock on `<spool_dir>/<run_id>/<producer_id>`.
//
// D2 of the 04.2.3 contract: `events send` and `runs finalize` are the only CLI writers of
// a producer directory, and they must never interleave with a second CLI process. The lock
// is `spool.lock`, mode 0600, whose whole content is `{ pid, started_at }` — no token, no
// argv, nothing a support bundle could leak.
//
// D-S56-Q8 adds the second half of the model: the reporter now holds its own
// `<producer_dir>/writer.lock` for the life of a writer, and the CLI only *inspects* that
// lock (`assertNoLiveWriter`) before it reads, acks, finalises or purges a chunk stream. A
// live reporter is `spool_locked` (never waited for, never taken over); a dead one is
// ignored and left in place, because the reporter's next start breaks it with its own
// rename-aside takeover. The CLI never creates, rewrites or deletes `writer.lock`, so it
// can never destroy a live successor's lock. The upload journal (`uploads/*.json`) does
// not pass through this check: a live test run must not block an artifact upload.
//
// Creation is atomic with the content: the record is written to a unique 0600 temp file in
// the same directory, fsynced, and hard-linked to `spool.lock`. `link` is the winner
// decision (EEXIST = held), so `spool.lock` is never observable empty or torn; that is what
// lets a lock which does not parse be treated as foreign instead of as a writer mid-create.
//
// Takeover of a dead holder never unlinks the path: the lock is renamed aside, the moved
// file is re-read, and it is deleted only when it still names the dead holder observed
// before the rename. Anything else is a live successor's lock and is put back with
// link-if-absent semantics. A lock naming a live pid is never taken over (pid reuse is
// accepted as a risk).
//
// The lock is advisory within this package: it guards a directory, not an fd, so every
// reader/writer of the spool goes through `withSpoolLock`.
import { randomUUID } from 'node:crypto';
import { constants as fsConstants } from 'node:fs';
import { link, lstat, open, readdir, rename, unlink } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

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
/** Without `O_NONBLOCK` an `open` of a planted FIFO blocks the thread pool forever. */
const O_NONBLOCK: number = process.platform === 'win32' ? 0 : fsConstants.O_NONBLOCK;

/**
 * A lock file written by an older writer (or by anything else) may still be empty for a
 * moment; re-read once after this pause before deciding that an unparseable lock is stale.
 * A lock we create is never observable empty, so this only covers a foreign writer.
 */
const staleProbeDelayMs = 10;
/** Bounded takeover attempts, so two processes racing over a dead lock cannot loop. */
const defaultTakeoverAttempts = 3;
/** No lock record is longer than a few dozen bytes; anything larger is not a lock. */
const maxLockFileBytes = 1024;

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
 * Reads a producer directory's lock, or `null` when there is none (or when it cannot be
 * read as a lock: a torn, empty, symlinked or foreign file is treated as stale by the
 * caller, never as a token-bearing document). A lock the filesystem could not be asked
 * about (I/O, descriptor exhaustion, permissions) raises `CliError` instead of reading as
 * absent, so such a failure can never turn into a takeover of a live lock.
 */
export function readSpoolLock(directory: string): Promise<SpoolLockFile | null> {
  return readSpoolLockPath(join(directory, spoolLockFileName));
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

/**
 * Errnos from inspecting the lock path that mean "there is no lock record here": the file is
 * gone, a symlink we refuse to follow, or a FIFO/device with no reader. Any other errno is a
 * real IO or permission failure and must never be read as "stale".
 */
export function isMissingLockErrno(code: string | undefined): boolean {
  return code === 'ENOENT' || code === 'ELOOP' || code === 'ENXIO';
}

const inspectionHint = 'check the spool directory is on a healthy, owned filesystem, then retry';

/** `spool_unsafe_path` from an inspection failure, carrying the errno for re-framing. */
class LockInspectionError extends CliError {
  readonly errno: string | undefined;

  constructor(errno: string | undefined) {
    super(
      `cannot inspect the spool lock (${errno ?? 'unknown OS error'}); nothing was taken over`,
      {
        code: 'spool_unsafe_path',
        hint: inspectionHint,
      },
    );
    this.name = 'LockInspectionError';
    this.errno = errno;
  }
}

/**
 * Reads one lock record. The open is `O_NOFOLLOW|O_NONBLOCK` and the descriptor must name a
 * regular file of a plausible size, so a symlink, FIFO or device planted at the lock path
 * (by another user of a shared spool, or by a hostile reporter) can neither be followed nor
 * block the CLI. `null` means "no record here" only for {@link isMissingLockErrno}, a
 * non-regular or oversized file, or unparseable content; any other errno throws.
 */
async function readSpoolLockPath(path: string): Promise<SpoolLockFile | null> {
  let text: string;
  try {
    const handle = await open(path, fsConstants.O_RDONLY | O_NOFOLLOW | O_NONBLOCK);
    try {
      const stats = await handle.stat();
      if (!stats.isFile() || stats.size > maxLockFileBytes) return null;
      text = await handle.readFile('utf8');
    } finally {
      await handle.close();
    }
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    // Only "there is nothing to read" means "no lock". EMFILE/ENFILE/EIO/EACCES say the
    // filesystem could not be inspected, and calling that "stale" would let a live lock be
    // taken over, so they abort the command instead.
    if (isMissingLockErrno(code)) return null;
    throw new LockInspectionError(code);
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

/**
 * The reporter's own lock (D-S56-Q8, `@evidgate/playwright-reporter` `writerLock.ts`):
 * `<producer_dir>/writer.lock`, mode 0600, whole content `{ pid, started_at }`, held for
 * the life of a writer. It is never the CLI's: only {@link assertNoLiveWriter} reads it.
 */
export const writerLockFileName = 'writer.lock';

/** Options of {@link assertNoLiveWriter}; both are test seams. */
export interface WriterLockCheckOptions {
  /** Liveness probe. Defaults to {@link isProcessAlive}; injected by tests. */
  readonly isProcessAlive?: (pid: number) => boolean;
  /** Pause before the confirming re-read of an unreadable lock. Defaults to 10 ms. */
  readonly probeDelayMs?: number;
}

/**
 * `spool_locked` (D-S56-Q8, exit 3): the reporter is writing this producer right now, so
 * nothing may read, ack, finalize or purge its chunk stream. The message names the pid.
 */
export class WriterLockedError extends CliError {
  /** The `writer.lock` that is held. */
  readonly lockPath: string;
  /** The live reporter's pid. */
  readonly holderPid?: number;

  constructor(lockPath: string, holderPid?: number) {
    super(
      holderPid === undefined
        ? `the reporter's writer lock is held by another process (${lockPath})`
        : `the reporter is writing this producer right now (live process ${holderPid}, ${lockPath})`,
      {
        code: 'spool_locked',
        hint: 'the reporter is still writing this producer; wait for the test run to finish, then retry',
      },
    );
    this.name = 'WriterLockedError';
    this.lockPath = lockPath;
    if (holderPid !== undefined) this.holderPid = holderPid;
  }
}

/**
 * Reads `<producer_dir>/writer.lock`, or `null` when there is none it can trust (absent,
 * symlink/FIFO/device, oversized, or unparseable). The classification is
 * {@link readSpoolLockPath}'s, so a foreign or torn file can neither be followed nor
 * read as a lock; a real inspection failure throws `spool_unsafe_path`.
 */
export function readWriterLock(directory: string): Promise<SpoolLockFile | null> {
  return readSpoolLockPath(join(directory, writerLockFileName));
}

/**
 * Refuses when the reporter holds a *live* `writer.lock` (D-S56-Q8). Called under the
 * CLI's own `spool.lock`, so a second CLI can never be inside the same producer directory.
 *
 * - live pid -> `WriterLockedError` (`spool_locked`, exit 3), naming the pid. Nothing is
 *   read, acked or purged, and the lock is not touched: the CLI never waits for, takes
 *   over or deletes a reporter's lock, so it can never delete a live successor's.
 * - dead pid -> the lock is ignored and left in place. The reporter's next start breaks it
 *   with its own verified rename-aside takeover, which is the only writer that may.
 * - unparseable / non-regular / oversized / `ENOENT|ELOOP|ENXIO` -> absent, after one
 *   re-read so a foreign writer caught mid-create is not mistaken for a dead one.
 * - any other errno -> `spool_unsafe_path` (exit 3), never "absent": a lock the CLI could
 *   not inspect must not turn into a purge of a live reporter's spool.
 */
export async function assertNoLiveWriter(
  directory: string,
  options: WriterLockCheckOptions = {},
): Promise<void> {
  const alive = options.isProcessAlive ?? isProcessAlive;
  const lockPath = join(directory, writerLockFileName);
  for (let probe = 0; probe < 2; probe += 1) {
    if (probe > 0) await delay(options.probeDelayMs ?? staleProbeDelayMs);
    const holder = await readWriterLock(directory);
    if (holder === null) continue;
    if (!alive(holder.pid)) return;
    throw new WriterLockedError(lockPath, holder.pid);
  }
}

/**
 * What a caller may ask {@link ProducerSpool.withLock} to guard beyond the CLI lock.
 * Absent (the default) is today's behaviour: the upload journal path must not be blocked
 * by a live reporter, only the chunk readers/purgers are.
 */
export interface ProducerLockOptions extends WriterLockCheckOptions {
  /** Refuse with `spool_locked` while the reporter holds `writer.lock` (D-S56-Q8). */
  readonly requireNoLiveWriter?: boolean;
}
