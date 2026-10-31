// Failure diagnostics for the reporter's `onEnd` path. A buffered failure can carry a
// resolver-supplied value, a presigned URL or the spool path, so only a fixed reason code
// or a POSIX errno name may reach stderr; anything else becomes one constant label.

/**
 * The one part of a buffered failure that is safe to log: a POSIX errno or one of the
 * adapter's fixed reason codes. A caller-crafted `code`, a resolver message or a presigned
 * URL never reaches the line — anything unrecognised degrades to a constant label.
 */
export function failureCode(failure: Error): string {
  const code: unknown = (failure as NodeJS.ErrnoException).code;
  if (typeof code !== 'string') return UNEXPECTED_FAILURE;
  return SAFE_FAILURE_CODES[code] === true || ERRNO_CODE.test(code) ? code : UNEXPECTED_FAILURE;
}

/**
 * Normalises a thrown value into an `Error`. The callbacks buffer whatever a spool, a lock or
 * an injected resolver throws, and they may throw a non-Error, but a buffered failure must
 * stay diagnosable (`failureCode` reads `.code`).
 */
export function toError(error: unknown): Error {
  return error instanceof Error ? error : new Error(String(error));
}

/** The adapter's own reason codes (`spoolErrors.ts`) plus the shard conflict. */
const SAFE_FAILURE_CODES: Readonly<Record<string, true>> = {
  invalid_config: true,
  spool_cap_exceeded: true,
  spool_shard_mismatch: true,
  invalid_shard: true,
  spool_writer_locked: true,
  spool_unsafe_path: true,
  producer_id_shard_conflict: true,
};

/** A POSIX errno name; the node fs errors the spool can raise all use this shape. */
const ERRNO_CODE = /^E[A-Z]{2,15}$/;

const UNEXPECTED_FAILURE = 'unexpected_error';

/** An explicit `producer_id` that disagrees with the shard Playwright selected. */
export class ProducerShardConflictError extends Error {
  readonly code = 'producer_id_shard_conflict';
}
