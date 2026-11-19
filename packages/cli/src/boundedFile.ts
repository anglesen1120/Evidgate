// One capped regular-file read shared by CLI commands: the open flags, the fstat check and
// the read-until-EOF loop live here, so every command refuses a special, empty or oversize
// file the same way and maps the failure to its own code and message.
import { constants } from 'node:fs';
import { open, type FileHandle } from 'node:fs/promises';

import { O_NOFOLLOW } from './spoolLimits.js';

/** Why a bounded read produced no bytes. No path, content or message is carried. */
export type BoundedFileFailure =
  | { readonly kind: 'unreadable'; readonly errno: string }
  | { readonly kind: 'not_regular_file' }
  | { readonly kind: 'empty' }
  | { readonly kind: 'too_large' };

export type BoundedFileResult =
  | {
      readonly ok: true;
      readonly bytes: Uint8Array;
      /** Identity of the descriptor that was read, for a caller's path-swap check. */
      readonly dev: number;
      readonly ino: number;
    }
  | { readonly ok: false; readonly failure: BoundedFileFailure };

export interface BoundedReadOptions {
  /**
   * Refuse a symlink at `path` (`ELOOP`) instead of following it. Off by default because
   * a caller that confines the realpath itself decides whether a symlink is acceptable.
   */
  readonly noFollow?: boolean;
}

// `O_NONBLOCK` keeps `open` from blocking on a FIFO — without it the non-regular-file check
// never runs, because the open waits for a writer. Regular-file reads are unaffected.
// win32 has no `O_NONBLOCK`, so the flags reduce to `O_RDONLY` (plain `'r'`).
const openReadFlags = constants.O_RDONLY | (constants.O_NONBLOCK ?? 0);

/**
 * Reads at most `maxBytes` from a regular file. A size already over the cap is refused on the
 * fstat, before any buffer exists; otherwise the buffer is the size plus one spare byte, so a
 * file exactly at the cap succeeds and the next byte refuses.
 */
export async function readFileBounded(
  path: string,
  maxBytes: number,
  options: BoundedReadOptions = {},
): Promise<BoundedFileResult> {
  const flags = openReadFlags | (options.noFollow === true ? O_NOFOLLOW : 0);
  let handle: FileHandle;
  try {
    handle = await open(path, flags);
  } catch (error) {
    return { ok: false, failure: { kind: 'unreadable', errno: errnoName(error) } };
  }
  try {
    const stats = await handle.stat();
    if (!stats.isFile()) {
      return { ok: false, failure: { kind: 'not_regular_file' } };
    }
    if (stats.size > maxBytes) {
      return { ok: false, failure: { kind: 'too_large' } };
    }
    const bytes = await readCapped(handle, stats.size, maxBytes);
    if (bytes.length > maxBytes) {
      return { ok: false, failure: { kind: 'too_large' } };
    }
    if (bytes.length === 0) {
      return { ok: false, failure: { kind: 'empty' } };
    }
    return { ok: true, bytes, dev: stats.dev, ino: stats.ino };
  } catch (error) {
    return { ok: false, failure: { kind: 'unreadable', errno: errnoName(error) } };
  } finally {
    await handle.close().catch(() => undefined);
  }
}

/**
 * Reads to EOF into a buffer sized from the fstat. A file that grew after the fstat gets one
 * wider allocation at the cap instead of a silent truncation; a file at the cap plus one byte
 * comes back one byte over, which the caller reports as too large.
 */
async function readCapped(handle: FileHandle, size: number, maxBytes: number): Promise<Uint8Array> {
  let buffer = Buffer.allocUnsafe(Math.min(size, maxBytes) + 1);
  let bytesRead = 0;
  for (;;) {
    while (bytesRead < buffer.length) {
      const read = await handle.read(buffer, bytesRead, buffer.length - bytesRead, bytesRead);
      if (read.bytesRead === 0) {
        return buffer.subarray(0, bytesRead);
      }
      bytesRead += read.bytesRead;
    }
    if (buffer.length > maxBytes) {
      return buffer.subarray(0, bytesRead);
    }
    const wider = Buffer.allocUnsafe(maxBytes + 1);
    buffer.copy(wider, 0, 0, bytesRead);
    buffer = wider;
  }
}

/** The errno code of a filesystem failure, or `unknown` — never the message or a path. */
export function errnoName(error: unknown): string {
  const code = error instanceof Error ? (error as NodeJS.ErrnoException).code : undefined;
  return typeof code === 'string' && code.length > 0 ? code : 'unknown';
}
