// The shapes of the upload journal: what one record holds (the full `upload-url` identity
// plus the two completion flags) and how a caller asks a write to wait for a busy producer
// lock. They live apart from the record codec so the crash-safe write path and its callers
// share one description of the file.
import type { ArtifactKind } from './uploadPaths.js';

/** The journal directory inside a producer directory. */
export const uploadsDirectoryName = 'uploads';

/**
 * The upload an artifact belongs to: every value the `upload-url` request is built from.
 * Two uploads with the same bytes but a different execution, kind, attempt or media type
 * are different rows, so all of these must match before a record is resumed.
 */
export interface UploadIdentity {
  /** The file digest, in the server's `sha256:<hex>` form. */
  readonly file_sha256: string;
  /** The declared byte count. */
  readonly size: number;
  /** The artifact kind (openapi `createArtifactUploadUrl.kind`). */
  readonly kind: ArtifactKind;
  /** The planned execution (64 lowercase hex). */
  readonly execution_key: string;
  /** The execution attempt (integer ≥ 1). */
  readonly attempt_no: number;
  /** The media type sent with the mint. */
  readonly media_type: string;
}

/** One journal record: what an interrupted upload needs to resume where it stopped. */
export interface UploadJournalEntry extends UploadIdentity {
  /** The server's artifact id (a UUID); part of the record's file name. */
  readonly artifact_id: string;
  /** The object was PUT successfully; a restart only calls `complete`. */
  readonly put_done: boolean;
  /** `complete` answered for this row (`ready` or a committed `rejected`). */
  readonly complete_done: boolean;
  /** Which upload-url mint this row came from (0 for the first); see `upload.ts`. */
  readonly mint_no: number;
}

/**
 * How a journal write waits for a busy `spool.lock`. `sleep` and `now` are injected by the
 * specs (a virtual clock); production uses the real ones.
 */
export interface UploadJournalLockOptions {
  /** Absolute upload deadline (`now() + EVIDGATE_UPLOAD_DEADLINE`). */
  readonly deadlineAt?: number;
  /** Clock in milliseconds; it bounds the wait. */
  readonly now?: () => number;
  /** Virtual sleep; the wait between two lock attempts. */
  readonly sleep?: (milliseconds: number) => Promise<void>;
  /**
   * The journal's own cap on one wait, overridable per call (see
   * {@link UploadJournalWriteOptions}). By default the wait is bounded by the upload deadline
   * alone (`deadlineAt`), because `events send` holds the same lock for its whole run;
   * without a deadline a short fallback keeps a stuck writer from blocking forever.
   */
  readonly maxWaitMs?: number;
}

/**
 * A per-call override of the lock budget. The post-transfer best-effort updates
 * (`put_done`, `complete_done`) use it: the bytes are already in the store, so costing the
 * rest of the upload deadline for a lock `events send` may hold for its whole run is worse
 * than recounting the transfer on the next run.
 */
export interface UploadJournalWriteOptions {
  /** A cap on THIS call's wait for `spool.lock`, on top of the journal's own budget. */
  readonly maxWaitMs?: number;
}

export interface UploadJournalOptions extends UploadJournalLockOptions {
  /** The producer directory (`<spool_dir>/<run_id>/<producer_id>`), already validated. */
  readonly producerDirectory: string;
  /** The producer's exclusive lock (`ProducerSpool.withLock`), reused, never re-implemented. */
  readonly withLock: <T>(fn: () => Promise<T>) => Promise<T>;
}

/** The fields a journal update may change; the identity fields never change. */
export interface UploadJournalPatch {
  readonly put_done?: boolean;
  readonly complete_done?: boolean;
}
