// Reading a sidecar off disk as untrusted input: the size is capped before the content is read, the
// manifest is `lstat`ed so a symlink is never followed, and the bytes are decoded strictly. A
// failure answers a warning through the caller's `warn`, never an exception.
import { closeSync, constants, fstatSync, lstatSync, openSync, readSync } from 'node:fs';

import { MAX_BYTES, UTF8 } from './sidecarRules.js';
import type { Inspection, SidecarFs, Warn } from './sidecarTypes.js';

export const defaultFs: SidecarFs = {
  readFile: (path, maxBytes) => readBounded(path, maxBytes),
};

/**
 * The default reader: opens without following a symlink (where the platform has `O_NOFOLLOW`),
 * requires a regular file on the descriptor, and reads at most one byte past `maxBytes`, so an
 * oversized manifest is refused without being allocated in full. A missing path is `undefined`;
 * anything else that stops the read is thrown and reported by the caller as `invalid_sidecar`.
 */
function readBounded(path: string, maxBytes: number): Uint8Array | undefined {
  let fd: number;
  try {
    fd = openSync(path, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
  } catch (error) {
    const code = errorCode(error);
    if (code === 'ENOENT' || code === 'ENOTDIR') {
      return undefined;
    }
    throw error;
  }

  try {
    const stat = fstatSync(fd);
    if (!stat.isFile()) {
      throw new SidecarReadError('ENOTFILE');
    }
    const size = Math.min(stat.size, maxBytes + 1);
    const buffer = Buffer.allocUnsafe(size);
    let filled = 0;
    while (filled < size) {
      const read = readSync(fd, buffer, filled, size - filled, filled);
      if (read === 0) {
        break;
      }
      filled += read;
    }
    return buffer.subarray(0, filled);
  } finally {
    closeSync(fd);
  }
}

/** Reads and decodes one inspected manifest, or returns undefined with a warning. */
export function readText(manifestPath: string, fs: SidecarFs, warn: Warn): string | undefined {
  let bytes: Uint8Array | undefined;
  try {
    bytes = fs.readFile(manifestPath, MAX_BYTES);
  } catch (error) {
    warn('invalid_sidecar', manifestPath, `the sidecar could not be read (${errorCode(error)})`);
    return undefined;
  }
  if (bytes === undefined) {
    return undefined;
  }
  if (bytes.byteLength > MAX_BYTES) {
    warn('invalid_sidecar', manifestPath, 'the sidecar is larger than 1 MiB');
    return undefined;
  }

  try {
    return UTF8.decode(bytes);
  } catch {
    // Malformed UTF-8 must never be repaired into a U+FFFD filename that maps a different file.
    warn('invalid_sidecar', manifestPath, 'the sidecar is not valid UTF-8 text');
    return undefined;
  }
}

/** lstats the manifest: absent, not a regular file (symlinks are never followed), or a file with its size. */
export function inspectSidecar(manifestPath: string): Inspection {
  try {
    const stat = lstatSync(manifestPath);
    if (!stat.isFile()) {
      return { kind: 'invalid', reason: 'the sidecar is not a regular file' };
    }
    return { kind: 'file', size: stat.size };
  } catch (error) {
    const code = errorCode(error);
    if (code === 'ENOENT' || code === 'ENOTDIR') {
      return { kind: 'absent' };
    }
    return { kind: 'invalid', reason: `the sidecar could not be inspected (${code})` };
  }
}

/** The error code of a node error, never its message (which can echo the path). */
function errorCode(error: unknown): string {
  const code = (error as NodeJS.ErrnoException | null)?.code;
  return typeof code === 'string' ? code : 'unreadable';
}

/** A refusal of the default reader (a race past the lstat), carrying a code the caller reports. */
class SidecarReadError extends Error {
  constructor(readonly code: string) {
    super(code);
    this.name = 'SidecarReadError';
  }
}
