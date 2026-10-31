// The reporter-side single-writer lock on `<spool_dir>/<run_id>/<producer_id>`.
//
// Ported faithfully from `packages/cli/src/spoolLock.ts` (contracts/cli.md §11.2) so both sides
// share one record format and one takeover algorithm: the lock is `writer.lock`, mode 0600,
// whose whole content is `{ pid, started_at }` — no token, no argv, nothing a support bundle
// could leak. It is deliberately NOT the CLI's `spool.lock`: the CLI lock serialises CLI
// commands against each other, this one keeps two live reporter processes sharing
// `(run_id, producer_id)` from interleaving lines, and a shared name would make one side's
// holder look like the other's and block it wrongly. The reporter keeps no runtime import of
// `@evidgate/cli` (the package's only runtime load of the CLI is the single lazy `import()` in
// `attach.ts`, and the dependency is one-way — packages/cli never imports this adapter), and
// the writer API is synchronous (`SpoolWriter.append/flush/close`), so this port uses
// `node:fs` sync calls and a synchronous pause where the CLI awaits.
//
// Creation is atomic with the content: the record is written to a unique 0600 temp file in
// the same directory, fsynced, and hard-linked to `writer.lock`. `link` is the winner
// decision (EEXIST = held), so `writer.lock` is never observable empty or torn; that is
// what lets a lock which does not parse be treated as foreign instead of as a writer
// mid-create.
//
// Takeover of a dead holder never unlinks the path: the lock is renamed aside, the moved
// file is re-read, and it is deleted only when it still names the dead holder observed
// before the rename. Anything else is a live successor's lock and is put back with
// link-if-absent semantics. A lock naming a live pid is never taken over (pid reuse is
// accepted as a risk).
import { randomUUID } from 'node:crypto';
import {
  closeSync,
  constants as fsConstants,
  fstatSync,
  fsyncSync,
  linkSync,
  lstatSync,
  openSync,
  readFileSync,
  readdirSync,
  renameSync,
  unlinkSync,
  writeSync,
} from 'node:fs';
import { dirname, join } from 'node:path';

/** The lock file name inside a producer directory (never the CLI's `spool.lock`). */
export const WRITER_LOCK_FILE = 'writer.lock';
/** `writer.lock` is created 0600 like every other spool file. */
export const WRITER_LOCK_FILE_MODE = 0o600;

/**
 * `O_NOFOLLOW` is undefined on Windows, where the POSIX symlink hardening does not apply
 * (the same degrade the merged writer uses).
 */
const O_NOFOLLOW: number = process.platform === 'win32' ? 0 : fsConstants.O_NOFOLLOW;
/** Without `O_NONBLOCK` an `open` of a planted FIFO blocks the thread forever. */
const O_NONBLOCK: number = process.platform === 'win32' ? 0 : fsConstants.O_NONBLOCK;

/**
 * A lock file written by an older writer (or by anything else) may still be empty for a
 * moment; re-read once after this pause before deciding that an unparseable lock is stale.
 * A lock we create is never observable empty, so this only covers a foreign writer.
 */
const defaultProbeDelayMs = 10;
/** Bounded takeover attempts, so two processes racing over a dead lock cannot loop. */
const defaultTakeoverAttempts = 3;
/** No lock record is longer than a few dozen bytes; anything larger is not a lock. */
const maxLockFileBytes = 1024;

/** The lock file's whole content. Nothing else is ever written. */
export interface WriterLockFile {
  readonly pid: number;
  readonly started_at: string;
}

export interface WriterLockOptions {
  /** Pid recorded in the lock. Defaults to `process.pid`; injected by tests. */
  readonly pid?: number;
  /** Clock for `started_at`. Defaults to `new Date()`. */
  readonly now?: () => Date;
  /** Liveness probe. Defaults to {@link isProcessAlive}; injected by tests. */
  readonly isProcessAlive?: (pid: number) => boolean;
  /** Attempts allowed while breaking a stale lock. Defaults to 3. */
  readonly maxTakeoverAttempts?: number;
  /** Pause before the confirming re-read of an unreadable lock. Defaults to 10 ms. */
  readonly probeDelayMs?: number;
}

/**
 * `spool_writer_locked`: a live process already writes this `(run_id, producer_id)`.
 * The error names the holder's pid when the lock file named one.
 */
export class EvidgateSpoolWriterLockedError extends Error {
  readonly code = 'spool_writer_locked';
  /** The lock file that is held. */
  readonly lockPath: string;
  /** The live holder's pid, when the lock file named one. */
  readonly holderPid?: number;

  constructor(lockPath: string, holderPid?: number) {
    super(
      holderPid === undefined
        ? `@evidgate/playwright-reporter: the producer spool is locked by another process (${lockPath})`
        : `@evidgate/playwright-reporter: the producer spool is locked by live process ${holderPid} (${lockPath})`,
    );
    this.name = 'EvidgateSpoolWriterLockedError';
    this.lockPath = lockPath;
    if (holderPid !== undefined) this.holderPid = holderPid;
  }
}

/**
 * `spool_unsafe_path`: the lock path cannot be inspected (I/O, permissions, descriptors) or
 * the filesystem cannot hard-link the record into place. Nothing was taken over.
 */
export class EvidgateSpoolUnsafePathError extends Error {
  readonly code = 'spool_unsafe_path';
  readonly path: string;
  readonly errno: string | undefined;

  constructor(path: string, message: string, errno?: string) {
    super(`@evidgate/playwright-reporter: ${message} (${path})`);
    this.name = 'EvidgateSpoolUnsafePathError';
    this.path = path;
    if (errno !== undefined) this.errno = errno;
  }
}

/** A held lock: the path, the record written, and the inode that record lives in. */
export interface HeldWriterLock {
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
 * Claims `writer.lock` in `directory`. Exactly one process wins the `link`; a loser
 * observes the holder: a live pid is `spool_writer_locked`, a dead pid (or a foreign file)
 * is broken by a verified takeover and the link is retried a bounded number of times.
 */
export function acquireWriterLock(
  directory: string,
  options: WriterLockOptions = {},
): HeldWriterLock {
  const path = join(directory, WRITER_LOCK_FILE);
  const pid = options.pid ?? process.pid;
  const now = options.now ?? (() => new Date());
  const alive = options.isProcessAlive ?? isProcessAlive;
  const attempts = options.maxTakeoverAttempts ?? defaultTakeoverAttempts;
  const probeDelayMs = options.probeDelayMs ?? defaultProbeDelayMs;
  for (let attempt = 0; attempt < attempts; attempt += 1) {
    const startedAt = now().toISOString();
    const claim = createLockAtomically(directory, path, pid, startedAt);
    if (claim !== null) return { path, pid, startedAt, ...claim };
    const observation = observeHeldLock(directory, alive, probeDelayMs);
    if (observation.kind === 'held') {
      throw new EvidgateSpoolWriterLockedError(path, observation.pid);
    }
    const outcome = takeOverObservedLock(directory, path, observation.observed, alive);
    if (outcome.kind === 'held') throw new EvidgateSpoolWriterLockedError(path, outcome.pid);
    // 'taken' and 'retry' both leave the slot to the next attempt, which links again.
  }
  throw new EvidgateSpoolWriterLockedError(path);
}

/** The whole lock record: `{ pid, started_at }` and nothing else. */
function lockRecord(pid: number, startedAt: string): string {
  return `${JSON.stringify({ pid, started_at: startedAt })}\n`;
}

/**
 * Creates `writer.lock` atomically *with* its content. The record goes to a unique temp
 * file in the same directory (`O_CREAT|O_EXCL`, mode 0600), is fsynced, and is then
 * hard-linked to `path`: `link` is the single atomic step that decides the winner, and it
 * never exposes a partially written lock. Returns the claimed inode, or `null` when
 * another process already holds the lock.
 */
function createLockAtomically(
  directory: string,
  path: string,
  pid: number,
  startedAt: string,
): LockClaim | null {
  const tempPath = join(directory, `${WRITER_LOCK_FILE}.${pid}.${randomUUID()}.tmp`);
  let fd = -1;
  let claim: LockClaim;
  try {
    fd = openSync(tempPath, 'wx', WRITER_LOCK_FILE_MODE);
    writeAll(fd, Buffer.from(lockRecord(pid, startedAt), 'utf8'));
    fsyncSync(fd);
    const stats = fstatSync(fd);
    claim = { dev: stats.dev, ino: stats.ino };
  } catch (error) {
    closeQuietly(fd);
    removeIfPresent(tempPath);
    throw error;
  }
  try {
    // `close` sits inside the guarded region: a failed close must not leave the staging
    // name behind (the `finally` removes it whatever happened).
    closeSync(fd);
    fd = -1;
    linkSync(tempPath, path);
    return claim;
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    if (code === 'EEXIST') return null;
    if (isHardLinkUnsupportedErrno(code)) throw hardLinkUnsupportedError(path, code);
    throw error;
  } finally {
    closeQuietly(fd);
    // The lock keeps its inode through the link; the temp name is only a staging entry.
    removeIfPresent(tempPath);
  }
}

/** `writeSync` may write fewer bytes than asked (EINTR, quota); loop so no record tears. */
function writeAll(fd: number, buffer: Buffer): void {
  let written = 0;
  while (written < buffer.byteLength) {
    const n = writeSync(fd, buffer, written, buffer.byteLength - written);
    if (n <= 0) throw new Error('spool lock write made no progress');
    written += n;
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

function hardLinkUnsupportedError(
  path: string,
  code: string | undefined,
): EvidgateSpoolUnsafePathError {
  return new EvidgateSpoolUnsafePathError(
    path,
    `cannot create the spool writer lock (${code ?? 'unknown OS error'}): the spool directory must be on a filesystem that supports hard links`,
    code,
  );
}

/**
 * Reads the lock that beat us and decides whether its holder is alive. A record naming a
 * live pid is final; a dead pid is stale at once. An unparseable file (torn, empty, foreign,
 * a FIFO or a symlink) is re-read once after `probeDelayMs` so that a foreign writer caught
 * mid-create is not mistaken for a crashed one.
 */
function observeHeldLock(
  directory: string,
  alive: (pid: number) => boolean,
  probeDelayMs: number,
): Observation {
  for (let probe = 0; probe < 2; probe += 1) {
    if (probe > 0) sleepSync(probeDelayMs);
    const holder = readWriterLock(directory);
    if (holder === null) continue;
    if (alive(holder.pid)) return { kind: 'held', pid: holder.pid };
    return {
      kind: 'stale',
      observed: { kind: 'lock', pid: holder.pid, startedAt: holder.started_at },
    };
  }
  return { kind: 'stale', observed: { kind: 'unreadable' } };
}

/**
 * Breaks a stale lock without ever destroying a successor's lock.
 *
 * `rename` is the only path operation that removes an entry atomically: whatever sits at
 * `writer.lock` at that instant moves aside, so a lock created after our read can never be
 * unlinked by us. The quarantined file is re-read and deleted only when it is still the lock
 * we observed as stale — same `(pid, started_at)`, pid still dead, or still unparseable.
 * Anything else means we moved a lock we had not observed: it is put back with
 * link-if-absent (`link` fails rather than overwrite, so a lock created in the meantime
 * wins) and reported as held.
 *
 * Residual window: a third process that links its own lock in the instant between our
 * `rename` and the restore makes the restore fail with EEXIST. It then holds the path
 * legitimately, and the lock we moved cannot be put back — POSIX path operations cannot
 * close that window. It needs a stale lock plus three racing writers, and release is
 * content checked, so the displaced holder still never deletes its successor's lock.
 */
function takeOverObservedLock(
  directory: string,
  path: string,
  observed: ObservedLock,
  alive: (pid: number) => boolean,
): TakeoverOutcome {
  const quarantined = join(directory, `${WRITER_LOCK_FILE}.${process.pid}.${randomUUID()}.stale`);
  try {
    renameSync(path, quarantined);
  } catch (error) {
    // ENOENT: another loser moved the stale lock first; the caller retries the link.
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return { kind: 'retry' };
    throw error;
  }
  let moved: WriterLockFile | null;
  try {
    moved = readWriterLockPath(quarantined);
  } catch (error) {
    // The path is empty at this instant: hand the quarantined lock back before failing, so
    // the failure never leaves a live holder's lock detached from its path.
    const errno = error instanceof EvidgateSpoolUnsafePathError ? error.errno : undefined;
    const restored = restoreQuarantinedLock(quarantined, path);
    throw new EvidgateSpoolUnsafePathError(
      path,
      restored
        ? `cannot inspect the quarantined spool writer lock (${errno ?? 'unknown OS error'}); the takeover was abandoned and the lock put back`
        : `cannot inspect the quarantined spool writer lock (${errno ?? 'unknown OS error'}); the takeover was abandoned and another lock holds the path`,
      errno,
    );
  }
  const stillTheObservedStaleLock =
    matchesObserved(observed, moved) && (moved === null || !alive(moved.pid));
  if (!stillTheObservedStaleLock) {
    restoreQuarantinedLock(quarantined, path);
    const current = readWriterLock(directory);
    return current === null ? { kind: 'retry' } : { kind: 'held', pid: current.pid };
  }
  removeIfPresent(quarantined);
  return { kind: 'taken' };
}

/** Whether a quarantined lock is byte-for-byte the state we observed before the rename. */
function matchesObserved(observed: ObservedLock, moved: WriterLockFile | null): boolean {
  if (observed.kind === 'unreadable') return moved === null;
  return moved !== null && moved.pid === observed.pid && moved.started_at === observed.startedAt;
}

/**
 * Puts a quarantined lock back at `path` unless a newer lock already took the slot.
 * Returns whether the moved lock is back at the path.
 */
function restoreQuarantinedLock(quarantined: string, path: string): boolean {
  let restored = true;
  try {
    linkSync(quarantined, path);
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    // EEXIST: a newer lock owns the path. ENOENT: the quarantined file is already gone.
    restored = false;
    if (code !== 'EEXIST' && code !== 'ENOENT') throw error;
  } finally {
    removeIfPresent(quarantined);
  }
  return restored;
}

/**
 * Reads a producer directory's lock, or `null` when there is none (or when it cannot be
 * read as a lock: a torn, empty, symlinked or foreign file is treated as stale by the
 * caller, never as a token-bearing document). A lock the filesystem could not be asked
 * about (I/O, descriptor exhaustion, permissions) raises `spool_unsafe_path` instead of
 * reading as absent, so such a failure can never turn into a takeover of a live lock.
 */
export function readWriterLock(directory: string): WriterLockFile | null {
  return readWriterLockPath(join(directory, WRITER_LOCK_FILE));
}

/**
 * Removes the lock only when it is still ours: a lock taken over by another process (or
 * rewritten by it) is left untouched, so a slow holder can never delete its successor's
 * lock. When the path no longer carries a lock at all, a concurrent verified takeover may
 * have our record quarantined (renamed aside) at the moment we released; that copy is
 * removed by inode, so no phantom `writer.lock` naming a live pid can survive.
 */
export function releaseWriterLock(held: HeldWriterLock): void {
  let holder: WriterLockFile | null;
  try {
    holder = readWriterLockPath(held.path);
  } catch {
    // The lock cannot be inspected, so it cannot be removed safely either: leave it in
    // place (our pid dies with this process, so a later run sees a stale lock).
    return;
  }
  if (holder === null) {
    scavengeQuarantinedLock(held);
    return;
  }
  if (holder.pid !== held.pid || holder.started_at !== held.startedAt) return;
  removeIfPresent(held.path);
}

/**
 * Deletes a quarantined copy of our own lock. A takeover renames the lock aside before it
 * decides whether to break it; if this holder releases inside that window, its unlink finds
 * no path and the taker's restore finds no quarantined file either, so the record would
 * survive as a lock naming a live pid that no longer holds anything. `(dev, ino)` still
 * identifies the inode under whatever name the taker chose; only that exact inode is removed.
 */
function scavengeQuarantinedLock(held: HeldWriterLock): void {
  let names: string[];
  try {
    names = readdirSync(dirname(held.path));
  } catch {
    return;
  }
  const prefix = `${WRITER_LOCK_FILE}.`;
  for (const name of names) {
    if (!name.startsWith(prefix) || !name.endsWith('.stale')) continue;
    const candidate = join(dirname(held.path), name);
    let stats;
    try {
      stats = lstatSync(candidate);
    } catch {
      continue;
    }
    if (stats.dev !== held.dev || stats.ino !== held.ino) continue;
    removeIfPresent(candidate);
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

/**
 * Reads one lock record. The open is `O_NOFOLLOW|O_NONBLOCK` and the descriptor must name a
 * regular file of a plausible size, so a symlink, FIFO or device planted at the lock path
 * (by another user of a shared spool, or by a hostile writer) can neither be followed nor
 * block the writer. `null` means "no record here" only for {@link isMissingLockErrno}, a
 * non-regular or oversized file, or unparseable content; any other errno throws.
 */
function readWriterLockPath(path: string): WriterLockFile | null {
  let text: string;
  try {
    const fd = openSync(path, fsConstants.O_RDONLY | O_NOFOLLOW | O_NONBLOCK);
    try {
      const stats = fstatSync(fd);
      if (!stats.isFile() || stats.size > maxLockFileBytes) return null;
      text = readFileSync(fd, 'utf8');
    } finally {
      closeSync(fd);
    }
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    // Only "there is nothing to read" means "no lock". EMFILE/ENFILE/EIO/EACCES say the
    // filesystem could not be inspected, and calling that "stale" would let a live lock be
    // taken over, so the writer refuses instead.
    if (isMissingLockErrno(code)) return null;
    throw new EvidgateSpoolUnsafePathError(
      path,
      `cannot inspect the spool writer lock (${code ?? 'unknown OS error'}); nothing was taken over`,
      code,
    );
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
  return { pid, started_at: startedAt };
}

function removeIfPresent(path: string): void {
  try {
    unlinkSync(path);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
  }
}

function closeQuietly(fd: number): void {
  if (fd < 0) return;
  try {
    closeSync(fd);
  } catch {
    // The descriptor is going away with the error path anyway.
  }
}

/**
 * The CLI's stale probe awaits; the writer is synchronous, so the pause is a main-thread
 * `Atomics.wait`. It only runs on the foreign/unreadable-lock path, never on acquisition.
 */
function sleepSync(ms: number): void {
  if (ms <= 0) return;
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}
