// One capped regular-file read shared by CLI commands: the open flags, the fstat check and
// the read-until-EOF loop live here, so every command refuses a special, empty or oversize
// file the same way and maps the failure to its own code and message.
import { constants } from 'node:fs';
import { open, type FileHandle } from 'node:fs/promises';

/** Why a bounded read produced no bytes. No path, content or message is carried. */
export type BoundedFileFailure =
  | { readonly kind: 'unreadable'; readonly errno: string }
  | { readonly kind: 'not_regular_file' }
  | { readonly kind: 'empty' }
  | { readonly kind: 'too_large' };

export type BoundedFileResult =
  | { readonly ok: true; readonly bytes: Uint8Array }
  | { readonly ok: false; readonly failure: BoundedFileFailure };

// `O_NONBLOCK` keeps `open` from blocking on a FIFO — without it the non-regular-file check
// never runs, because the open waits for a writer. Regular-file reads are unaffected.
// win32 has no `O_NONBLOCK`, so the flags reduce to `O_RDONLY` (plain `'r'`).
const openReadFlags = constants.O_RDONLY | (constants.O_NONBLOCK ?? 0);

/**
 * Reads at most `maxBytes` from a regular file. The buffer is one byte larger than the cap so
 * a file exactly at the cap succeeds and the first byte over refuses; the loop runs until
 * EOF, so a short read on a regular file cannot silently truncate the body.
 */
export async function readFileBounded(path: string, maxBytes: number): Promise<BoundedFileResult> {
  let handle: FileHandle;
  try {
    handle = await open(path, openReadFlags);
  } catch (error) {
    return { ok: false, failure: { kind: 'unreadable', errno: errnoName(error) } };
  }
  try {
    const stats = await handle.stat();
    if (!stats.isFile()) {
      return { ok: false, failure: { kind: 'not_regular_file' } };
    }
    const buffer = Buffer.allocUnsafe(maxBytes + 1);
    let bytesRead = 0;
    while (bytesRead < buffer.length) {
      const read = await handle.read(buffer, bytesRead, buffer.length - bytesRead, bytesRead);
      if (read.bytesRead === 0) {
        break;
      }
      bytesRead += read.bytesRead;
    }
    if (bytesRead > maxBytes) {
      return { ok: false, failure: { kind: 'too_large' } };
    }
    if (bytesRead === 0) {
      return { ok: false, failure: { kind: 'empty' } };
    }
    return { ok: true, bytes: buffer.subarray(0, bytesRead) };
  } catch (error) {
    return { ok: false, failure: { kind: 'unreadable', errno: errnoName(error) } };
  } finally {
    await handle.close().catch(() => undefined);
  }
}

/** The errno code of a filesystem failure, or `unknown` — never the message or a path. */
export function errnoName(error: unknown): string {
  const code = error instanceof Error ? (error as NodeJS.ErrnoException).code : undefined;
  return typeof code === 'string' && code.length > 0 ? code : 'unknown';
}
