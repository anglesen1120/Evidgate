// The filesystem primitives every spool path goes through: hardened opens, the regular-file and
// ownership refusals, bounded record reads, whole-buffer writes and directory fsyncs. Keeping them
// here means no call site can forget `O_NOFOLLOW`/`O_NONBLOCK` or the descriptor check.
import {
  chmodSync,
  closeSync,
  constants as fsConstants,
  fchmodSync,
  fsyncSync,
  fstatSync,
  lstatSync,
  openSync,
  readFileSync,
  readdirSync,
  type Stats,
  writeSync,
} from 'node:fs';
import { join, resolve, sep } from 'node:path';

import { CHUNK_FILE_DIGITS, SHARDS_FILE, SPOOL_DIR_MODE } from './spoolConfig.js';

/**
 * `O_NOFOLLOW` is undefined on Windows; degrade to a plain open there, where the POSIX
 * symlink/ownership hardening does not apply.
 */
export const O_NOFOLLOW: number = process.platform === 'win32' ? 0 : fsConstants.O_NOFOLLOW;

/**
 * A planted FIFO must never block a spool open. `O_NONBLOCK` is a no-op on a regular file; on a
 * FIFO it makes a read open return immediately (the descriptor is then refused by the
 * regular-file check) and a write open fail with `ENXIO` instead of waiting for a reader. The
 * writer paths refuse a non-regular target by `lstat` before the open, so they never get that far.
 */
export const O_NONBLOCK: number = process.platform === 'win32' ? 0 : fsConstants.O_NONBLOCK;

/**
 * Defence in depth: even after id validation, the resolved writer directory must stay inside the
 * resolved spool root.
 */
export function assertWithinSpoolDir(spoolDir: string, directory: string): void {
  const root = resolve(spoolDir);
  const target = resolve(directory);
  if (target !== root && !target.startsWith(root + sep)) {
    throw new Error(
      `@evidgate/playwright-reporter: the spool directory ${target} escapes spool_dir ${root}`,
    );
  }
}

/** Total bytes of every regular file under `directory` (the `EVIDGATE_SPOOL_MAX_BYTES` base). */
export function directoryBytes(directory: string): number {
  let total = 0;
  let entries;
  try {
    entries = readdirSync(directory, { withFileTypes: true });
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return 0;
    throw error;
  }
  for (const entry of entries) {
    const path = join(directory, entry.name);
    // Symlinks are never followed: a link out of the run directory must not be counted (or
    // read) as spool bytes.
    if (entry.isSymbolicLink()) continue;
    if (entry.isDirectory()) {
      total += directoryBytes(path);
      continue;
    }
    if (!entry.isFile()) continue;
    let fd = -1;
    try {
      fd = openSync(path, fsConstants.O_RDONLY | O_NOFOLLOW | O_NONBLOCK);
      total += fstatSync(fd).size;
    } catch {
      // A file that vanished or cannot be inspected contributes no bytes; the cap is a
      // guard rail, not an inventory.
    } finally {
      if (fd >= 0) closeSync(fd);
    }
  }
  return total;
}

/** The chunk file name of one directory: 6-digit zero-padded, exactly what the CLI reads. */
export function chunkPath(directory: string, index: number): string {
  return join(directory, `${String(index).padStart(CHUNK_FILE_DIGITS, '0')}.jsonl`);
}

/** Counts the non-empty lines of a chunk body (a torn tail has no trailing newline). */
export function countLines(text: string): number {
  let count = 0;
  for (const line of text.split('\n')) if (line.length > 0) count += 1;
  return count;
}

/**
 * A write open must never reach a planted FIFO/socket/device or a directory. `open` with
 * `O_WRONLY` on a FIFO would wait for a reader (or, with one, write spool lines into a pipe),
 * so the target is inspected first and refused when it exists and is not a regular file. A
 * symlink is refused too (`O_NOFOLLOW` would turn it into `ELOOP`); the caller's
 * `openVerified` still re-checks the descriptor for a race.
 */
export function assertWritableTargetIsRegularFile(path: string): void {
  let stats: Stats;
  try {
    stats = lstatSync(path);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return;
    throw error;
  }
  if (!stats.isFile()) {
    throw new Error(
      `@evidgate/playwright-reporter: refusing to use non-regular spool file ${path}`,
    );
  }
}

/**
 * Opens a spool file with the given flags (plus `O_NONBLOCK`, see below) and verifies the
 * descriptor: it must be a regular file owned by the current process, and (when a mode is
 * given) is forced to that mode. A non-regular file (a symlink target, FIFO or device) or a
 * foreign-owned file is rejected. On Windows the ownership/mode checks are skipped.
 * `O_NONBLOCK` is added here so no call site can forget it: it is a no-op on a regular file
 * and stops a planted FIFO from blocking whichever open reaches it first (`readChunk`, the
 * torn-tail truncation, the append or the record staging file).
 */
export function openVerified(path: string, flags: number, mode?: number): number {
  const fd = openSync(path, flags | O_NONBLOCK, mode);
  try {
    const stats = fstatSync(fd);
    if (!stats.isFile()) {
      throw new Error(
        `@evidgate/playwright-reporter: refusing to use non-regular spool file ${path}`,
      );
    }
    if (process.platform !== 'win32') {
      const uid = process.getuid?.();
      if (uid !== undefined && stats.uid !== uid) {
        throw new Error(
          `@evidgate/playwright-reporter: spool file ${path} is owned by uid ${stats.uid}, not ${uid}`,
        );
      }
      if (mode !== undefined) fchmodSync(fd, mode);
    }
    return fd;
  } catch (error) {
    closeSync(fd);
    throw error;
  }
}

/**
 * A directory the writer creates or reuses under `spool_dir` must be a real directory owned by
 * the current process; a symlink or a foreign-owned directory is rejected, and an owned one is
 * forced to 0700. `spool_dir` itself is never passed here (a symlinked spool root is allowed and
 * keeps its mode).
 */
export function hardenDirectory(path: string): void {
  const stats = lstatSync(path);
  if (stats.isSymbolicLink()) {
    throw new Error(
      `@evidgate/playwright-reporter: refusing to use symlinked spool directory ${path}`,
    );
  }
  if (!stats.isDirectory()) {
    throw new Error(`@evidgate/playwright-reporter: spool path ${path} is not a directory`);
  }
  if (process.platform === 'win32') return;
  const uid = process.getuid?.();
  if (uid !== undefined && stats.uid !== uid) {
    throw new Error(
      `@evidgate/playwright-reporter: spool directory ${path} is owned by uid ${stats.uid}, not ${uid}`,
    );
  }
  chmodSync(path, SPOOL_DIR_MODE);
}

/** Reads a small record file through an O_NOFOLLOW|O_NONBLOCK, ownership-checked descriptor. */
export function readRecordFile(path: string): string | null {
  let fd: number;
  try {
    // `O_NONBLOCK` so a planted FIFO opens without waiting for a writer; the `fstat` below
    // then refuses the non-regular descriptor instead of blocking the reporter.
    fd = openSync(path, fsConstants.O_RDONLY | O_NOFOLLOW | O_NONBLOCK);
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    if (code === 'ENOENT') return null;
    // ELOOP (a planted symlink), EACCES, EIO: a record we cannot read is refused rather
    // than read as absent, so a symlinked or unreadable record can never be replaced
    // silently — the same rule the chunks follow.
    throw new Error(
      `@evidgate/playwright-reporter: refusing to read spool record ${path} (${code ?? 'unknown OS error'})`,
    );
  }
  try {
    const stats = fstatSync(fd);
    if (!stats.isFile()) {
      throw new Error(
        `@evidgate/playwright-reporter: refusing to read non-regular spool record ${path}`,
      );
    }
    if (process.platform !== 'win32') {
      const uid = process.getuid?.();
      if (uid !== undefined && stats.uid !== uid) {
        throw new Error(
          `@evidgate/playwright-reporter: spool record ${path} is owned by uid ${stats.uid}, not ${uid}`,
        );
      }
    }
    return readFileSync(fd, 'utf8');
  } finally {
    closeSync(fd);
  }
}

/**
 * `writeSync` may write fewer bytes than asked (ENOSPC, EINTR, quota); loop until the whole
 * buffer is written so a short write cannot leave a torn line.
 */
export function writeAll(fd: number, buffer: Buffer): void {
  let written = 0;
  while (written < buffer.byteLength) {
    const n = writeSync(fd, buffer, written, buffer.byteLength - written);
    if (n <= 0) throw new Error('spool write made no progress');
    written += n;
  }
}

/** fsyncs the directory entry, so a rename survives a crash; a filesystem without it is ignored. */
export function fsyncDirectory(directory: string): void {
  if (process.platform === 'win32') return;
  try {
    const fd = openSync(directory, 'r');
    try {
      fsyncSync(fd);
    } finally {
      closeSync(fd);
    }
  } catch {
    // Directory fsync is a durability nicety; a filesystem that refuses it (some
    // network mounts) must not fail the run.
  }
}

/** The size of the existing `shards.json`, so a rewrite counts only its delta (cap cache). */
export function existingRecordBytes(directory: string): number {
  try {
    const stats = lstatSync(join(directory, SHARDS_FILE));
    return stats.isFile() ? stats.size : 0;
  } catch {
    return 0;
  }
}

/** Reads a chunk, or `null` when it does not exist yet. */
export function readChunkIfExists(path: string): Buffer | null {
  try {
    return readChunk(path);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null;
    throw error;
  }
}

/** Reads a chunk through an O_NOFOLLOW, ownership-checked descriptor. */
export function readChunk(path: string): Buffer {
  const fd = openVerified(path, fsConstants.O_RDONLY | O_NOFOLLOW);
  try {
    return readFileSync(fd);
  } finally {
    closeSync(fd);
  }
}
