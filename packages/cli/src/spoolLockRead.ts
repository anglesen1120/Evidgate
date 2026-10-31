// The read half of the lock protocol: classifying whatever sits at a lock path. Only a
// missing entry (or a file that is not a trustworthy lock) reads as "no lock"; a filesystem
// error the CLI could not classify must never read as "stale", because that would let a live
// lock be taken over.
import { constants as fsConstants } from 'node:fs';
import { open } from 'node:fs/promises';
import { join } from 'node:path';

import { CliError } from './errors.js';
import { O_NOFOLLOW, O_NONBLOCK } from './spoolLimits.js';

import { maxLockFileBytes, spoolLockFileName, type SpoolLockFile } from './spoolLockTypes.js';

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
 * Errnos from inspecting the lock path that mean "there is no lock record here": the file is
 * gone, a symlink we refuse to follow, or a FIFO/device with no reader. Any other errno is a
 * real IO or permission failure and must never be read as "stale".
 */
export function isMissingLockErrno(code: string | undefined): boolean {
  return code === 'ENOENT' || code === 'ELOOP' || code === 'ENXIO';
}

export const inspectionHint =
  'check the spool directory is on a healthy, owned filesystem, then retry';

/** `spool_unsafe_path` from an inspection failure, carrying the errno for re-framing. */
export class LockInspectionError extends CliError {
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
export async function readSpoolLockPath(path: string): Promise<SpoolLockFile | null> {
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
 * Reads a producer directory's lock, or `null` when there is none (or when it cannot be
 * read as a lock: a torn, empty, symlinked or foreign file is treated as stale by the
 * caller, never as a token-bearing document). A lock the filesystem could not be asked
 * about (I/O, descriptor exhaustion, permissions) raises `CliError` instead of reading as
 * absent, so such a failure can never turn into a takeover of a live lock.
 */
export function readSpoolLock(directory: string): Promise<SpoolLockFile | null> {
  return readSpoolLockPath(join(directory, spoolLockFileName));
}
