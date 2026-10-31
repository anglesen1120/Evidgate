// The on-disk lock record: the file name, its size bound and its reader, kept apart from the lock
// protocol so the format has one definition. `packages/cli`'s `spoolLock.ts` writes the same
// `{ pid, started_at }` document, and anything that does not parse is treated as foreign, never as
// a token-bearing document.
import { constants as fsConstants, closeSync, fstatSync, openSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

import { EvidgateSpoolUnsafePathError } from './spoolErrors.js';
import { O_NOFOLLOW, O_NONBLOCK } from './spoolFs.js';

/** The lock file name inside a producer directory (never the CLI's `spool.lock`). */
export const WRITER_LOCK_FILE = 'writer.lock';

/** No lock record is longer than a few dozen bytes; anything larger is not a lock. */
const maxLockFileBytes = 1024;

/** The lock file's whole content. Nothing else is ever written. */
export interface WriterLockFile {
  readonly pid: number;
  readonly started_at: string;
}

/** The whole lock record: `{ pid, started_at }` and nothing else. */
export function lockRecord(pid: number, startedAt: string): string {
  return `${JSON.stringify({ pid, started_at: startedAt })}\n`;
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
export function readWriterLockPath(path: string): WriterLockFile | null {
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
