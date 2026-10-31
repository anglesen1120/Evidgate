// The numbers the on-disk spool is built from: rotation sizes, the line bound every scanner
// enforces, the file modes, and the two POSIX open flags the hardening depends on. They live
// in one module so the appender, the reader and the cursor writer cannot drift apart.
import { constants as fsConstants } from 'node:fs';

/** `EVIDGATE_SPOOL_MAX_BYTES` default: 256 MiB per run directory. */
export const defaultSpoolMaxBytes = 268_435_456;
/** Spool directories are created 0700. */
export const spoolDirMode = 0o700;
/** Every spool file this module writes is 0600. */
export const spoolFileMode = 0o600;
/** A chunk rotates at 1 MiB (the merged writer's limit). */
export const spoolChunkMaxBytes = 1_048_576;
/** A chunk rotates at 1000 lines (the merged writer's limit). */
export const spoolChunkMaxLines = 1000;
/** Chunk names are zero-padded to six digits: `000001.jsonl`. */
export const spoolChunkDigits = 6;
/** The CLI-owned ack cursor. */
export const spoolCursorFileName = 'cursor.json';
/** A leftover tmp from a crash between write and rename. */
export const spoolCursorTmpFileName = 'cursor.json.tmp';
/** Envelope `producer_id`/`run_id` path-segment limit (writer parity). */
export const spoolIdMaxLength = 128;

/** Block size for streaming scans; bounds memory to one block plus one line. */
export const readBlockBytes = 64 * 1024;
/**
 * Longest tail line {@link lastTerminatedSequence} is willing to read before giving up
 * (it returns `null`, and the caller falls back to the streaming scan, whose hard bound is
 * {@link maxSpoolLineBytes}). Deliberately smaller than that bound: the tail fast path must
 * stay cheap, and a longer last line is still read correctly, just by the full scan.
 */
export const maxTailLineBytes = 1_048_576;
/**
 * Largest legal line in a chunk, in bytes: the intake's own per-line guard
 * (`maxInputLineBytes` in `commands/eventsSend.ts`, 32 MiB, kept in sync there because a
 * legal envelope must survive append → read back). A chunk is rotated at
 * {@link spoolChunkMaxBytes}, but one line is always written whole, so this — not the
 * rotation size — is the hard bound a scanner must enforce. A longer line cannot have
 * come from this CLI or from a writer following the same contract, so it is
 * `spool_corrupt` instead of a reason to grow the heap without limit.
 */
export const maxSpoolLineBytes = 32 * 1_048_576;
/**
 * `O_NOFOLLOW` is undefined on Windows; the POSIX symlink hardening degrades to a plain
 * open there (writer parity).
 */
export const O_NOFOLLOW: number = process.platform === 'win32' ? 0 : fsConstants.O_NOFOLLOW;
/**
 * `O_NONBLOCK` is a POSIX-only flag (undefined on Windows). Opening a FIFO or a device
 * with it makes the open return instead of parking the whole CLI in the thread pool; the
 * descriptor is then rejected by the regular-file check in {@link openVerifiedFile}.
 */
export const O_NONBLOCK: number = process.platform === 'win32' ? 0 : fsConstants.O_NONBLOCK;
