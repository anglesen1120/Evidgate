// Every descriptor the spool opens goes through one owner check: the path must be a real
// file or directory owned by this user, opened with O_NOFOLLOW so a planted symlink is
// refused rather than followed, and a descriptor is closed again before an error leaves.
import { closeSync, constants as fsConstants, fsyncSync, openSync } from 'node:fs';
import { chmod, lstat, mkdir, open, readdir } from 'node:fs/promises';
import { join } from 'node:path';

import { unsafePath } from './spoolErrors.js';
import { O_NOFOLLOW, O_NONBLOCK, spoolDirMode } from './spoolLimits.js';

import type { Dirent } from 'node:fs';
import type { FileHandle } from 'node:fs/promises';

/** Sums regular files under `root`, never following a symlink. Missing tree = 0 bytes. */
export async function directoryBytes(root: string): Promise<number> {
  let total = 0;
  const pending: string[] = [root];
  while (pending.length > 0) {
    const directory = pending.pop();
    if (directory === undefined) continue;
    let entries: Dirent[];
    try {
      entries = await readdir(directory, { withFileTypes: true });
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') continue;
      throw error;
    }
    for (const entry of entries) {
      if (entry.isSymbolicLink()) continue;
      const child = join(directory, entry.name);
      if (entry.isDirectory()) {
        pending.push(child);
        continue;
      }
      try {
        const stats = await lstat(child);
        if (stats.isFile()) total += stats.size;
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code === 'ENOENT') continue;
        throw error;
      }
    }
  }
  return total;
}

/** `mkdir -p` that tolerates "something is already there" (`hardenDirectory` names it). */
export async function mkdirOwned(directory: string): Promise<void> {
  await mkdir(directory, { recursive: true, mode: spoolDirMode }).catch((error: unknown) => {
    const code = (error as NodeJS.ErrnoException).code;
    // EEXIST/ENOENT: a file, a directory or a dangling symlink is already at the path.
    if (code !== 'EEXIST' && code !== 'ENOENT') throw error;
  });
}

/**
 * Requires `path` to be a real directory (never a symlink) owned by this process. Shared
 * by {@link hardenDirectory} and `ProducerSpool.purge`, which re-checks the run and
 * producer directories immediately before its recursive `rm` (a path checked at open time
 * can be swapped for a symlink before the removal: TOCTOU).
 */
export async function assertRealOwnedDirectory(path: string): Promise<void> {
  const stats = await lstat(path);
  if (stats.isSymbolicLink()) {
    throw unsafePath(`refusing to use symlinked spool directory ${path}`);
  }
  if (!stats.isDirectory()) {
    throw unsafePath(`spool path ${path} is not a directory`);
  }
  if (process.platform === 'win32') return;
  const uid = process.getuid?.();
  if (uid !== undefined && stats.uid !== uid) {
    throw unsafePath(`spool directory ${path} is owned by uid ${stats.uid}, not ${uid}`);
  }
}

/**
 * A directory the CLI creates or reuses under `spool_dir` must be a real directory owned
 * by this process (a planted symlink could redirect every write) and is forced to 0700.
 */
export async function hardenDirectory(path: string): Promise<void> {
  await assertRealOwnedDirectory(path);
  if (process.platform === 'win32') return;
  await chmod(path, spoolDirMode);
}

/**
 * Opens a spool file, verifying it is a regular file of ours and forcing its mode.
 *
 * `O_NOFOLLOW` and `O_NONBLOCK` are always OR-ed in: the first refuses a planted symlink
 * at the final path component, and the second makes an `open` of a FIFO or a device return
 * instead of parking the CLI in the thread pool forever, so the regular-file check below
 * can reject it with `spool_unsafe_path`.
 */
export async function openVerifiedFile(
  path: string,
  flags: number,
  mode?: number,
): Promise<FileHandle> {
  let handle: FileHandle;
  try {
    handle = await open(path, flags | O_NOFOLLOW | O_NONBLOCK, mode);
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    if (code === 'ELOOP') {
      throw unsafePath(`refusing to follow symlinked spool path ${path}`);
    }
    // A FIFO opened for writing with O_NONBLOCK and no reader: a path swapped for a FIFO
    // between the existence check and this open, never a regular spool file.
    if (code === 'ENXIO') {
      throw unsafePath(`refusing to open non-regular spool file ${path}`);
    }
    throw error;
  }
  try {
    const stats = await handle.stat();
    if (!stats.isFile()) {
      throw unsafePath(`refusing to use non-regular spool file ${path}`);
    }
    if (process.platform !== 'win32') {
      const uid = process.getuid?.();
      if (uid !== undefined && stats.uid !== uid) {
        throw unsafePath(`spool file ${path} is owned by uid ${stats.uid}, not ${uid}`);
      }
      if (mode !== undefined) await handle.chmod(mode);
    }
    return handle;
  } catch (error) {
    await handle.close();
    throw error;
  }
}

/** Reads a spool file through an O_NOFOLLOW, ownership-checked descriptor. */
export async function readVerifiedText(path: string): Promise<string> {
  const handle = await openVerifiedFile(path, fsConstants.O_RDONLY | O_NOFOLLOW);
  try {
    return await handle.readFile('utf8');
  } finally {
    await handle.close();
  }
}

export async function pathExists(path: string): Promise<boolean> {
  try {
    const stats = await lstat(path);
    return stats.isFile() || stats.isSymbolicLink();
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return false;
    throw error;
  }
}

/** `write()` may write fewer bytes than asked (ENOSPC, EINTR, quota); loop until done. */
export async function writeAll(handle: FileHandle, buffer: Buffer): Promise<void> {
  let written = 0;
  while (written < buffer.byteLength) {
    const { bytesWritten } = await handle.write(buffer, written, buffer.byteLength - written);
    if (bytesWritten <= 0) throw new Error('spool write made no progress');
    written += bytesWritten;
  }
}

/** Directory fsync is a durability nicety; a filesystem that refuses it must not fail the run. */
export function fsyncDirectorySync(directory: string): void {
  if (process.platform === 'win32') return;
  try {
    const fd = openSync(directory, 'r');
    try {
      fsyncSync(fd);
    } finally {
      closeSync(fd);
    }
  } catch {
    // Best effort (some network mounts refuse O_RDONLY fsync on a directory).
  }
}
