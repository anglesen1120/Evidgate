// The tunables and seams one upload runs on, kept apart from the flow so the stall guard,
// the body stream and the retry loops read the same numbers instead of each carrying its
// own copy.
import type { ResolvedConfig } from './config.js';
import type { UploadJournal } from './uploadJournal.js';
import type { UploadFile, ArtifactKind } from './uploadPaths.js';
import type { UploadRetryDeps } from './uploadRetry.js';

/** The chunk size of both passes: 64 KiB, so memory stays flat for a multi-GB trace. */
export const uploadChunkBytes = 64 * 1024;
/**
 * How many times one invocation may mint a fresh `upload-url` row after an expired
 * presigned URL. Each mint is a new server row (the old one expires in `pending/`), so the
 * cap keeps a broken clock or a misconfigured store from littering the tenant.
 */
export const maxMintAttempts = 3;
/**
 * How long the presigned PUT may make NO body progress before it is aborted and retried.
 * The body of a multi-GB trace legitimately takes minutes, so the PUT has no per-request
 * cap (`senderLimits.ts`'s 30 s applies to the two JSON calls); a connection that stops moving is
 * caught by this idle timeout instead, which resets on every chunk.
 */
export const putStallTimeoutMs = 60_000;
/**
 * How long a best-effort post-transfer journal update (`put_done` after the PUT,
 * `complete_done` after `complete`) may wait for `spool.lock`. A live `events send` holds
 * that lock for its whole run, and the bytes are already in the store, so waiting out the
 * upload deadline for it would leave `complete` (or the process) with nothing — the update is
 * abandoned after this budget and the `spool_locked` failure is swallowed. The pre-PUT
 * `write` that records a minted row keeps the full deadline budget, because that record is
 * what makes a restart resume instead of re-mint.
 */
export const journalBestEffortWaitMs = 2_000;

/** A timer seam: the stall guard and the PUT's deadline must both be injectable. */
export interface UploadTimers {
  set(milliseconds: number, fn: () => void): unknown;
  clear(handle: unknown): void;
}

export const systemTimers: UploadTimers = {
  set: (milliseconds, fn) => setTimeout(fn, milliseconds),
  clear: (handle) => clearTimeout(handle as NodeJS.Timeout),
};

/** Everything an upload needs besides the file: identity, scope, seams. */
export interface UploadDeps extends UploadRetryDeps {
  /** Transport seam; the SDK calls and the raw PUT both go through it. */
  readonly fetch: typeof fetch;
  /** The PUT's idle timeout; defaults to {@link putStallTimeoutMs}. */
  readonly putStallTimeoutMs?: number;
  /** Timer seam for the PUT's deadline and idle timeout; defaults to the global timers. */
  readonly timers?: UploadTimers;
  /**
   * The lock budget of the best-effort `put_done` / `complete_done` journal updates;
   * defaults to {@link journalBestEffortWaitMs}.
   */
  readonly journalBestEffortWaitMs?: number;
}

export interface UploadRequest {
  readonly config: ResolvedConfig;
  /** The `ci_producer` bearer; only the SDK calls see it, never the object store. */
  readonly token: string;
  readonly runId: string;
  readonly producerId: string;
  /** The planned execution the artifact belongs to (openapi: 64 lowercase hex). */
  readonly executionKey: string;
  readonly attemptNo: number;
  readonly kind: ArtifactKind;
  /** The media type the kind policy accepts (derived from the kind or `--media-type`). */
  readonly mediaType: string;
  readonly file: UploadFile;
  /** The digest of pass 1, in `sha256:<hex>` form. */
  readonly sha256: string;
  /** The resume store, or `undefined` when `EVIDGATE_SPOOL_DIR` is unset. */
  readonly journal: UploadJournal | undefined;
  /** One deadline for the whole upload (`EVIDGATE_UPLOAD_DEADLINE`). */
  readonly deadlineMs: number;
  readonly deps: UploadDeps;
}

/** What a successful upload produced. */
export interface UploadOutcome {
  readonly artifactId: string;
  readonly state: 'ready';
  /** The bytes the server reports (equal to the local size). */
  readonly bytes: number;
  /** `pending` right after `complete`; the secret scan is asynchronous. */
  readonly scanState: string | null;
  /** The artifact row's mint (`0` for the first `upload-url`). */
  readonly mintNo: number;
}
