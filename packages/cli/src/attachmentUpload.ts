// The single entry the Playwright adapter uses to ship an attachment as evidence: a facade
// over `uploadFile()` so the reporter makes no policy decision of its own, resolving config,
// bearer, resume journal and allow-list guard exactly as `evidgate artifacts upload` does.
//
// The guard runs before the token is fetched, so a refused or oversize file costs no request,
// not even a token exchange. The bearer is memoised per uploader; a 401 purges the cached CI
// token and drops the memo, so the next attachment re-authenticates rather than reuse a
// bearer the server already refused. With `EVIDGATE_SPOOL_DIR` set, the journal (per run and
// producer) lets a crashed shard resume its artifact row instead of minting a second one.
//
// The caller's AbortSignal stops the wait, never work in flight: `uploadFile()` takes no
// signal, so once it starts no checkpoint runs again and a presigned PUT runs to its own
// deadline. No path, token or presigned URL is formatted into a message, and no
// artifact bytes are buffered here.
import { setTimeout as delay } from 'node:timers/promises';

import { getToken } from './auth/getToken.js';
import { deleteCachedToken } from './auth/tokenCache.js';
import { resolveConfig, type ResolvedConfig } from './config.js';
import { CliError } from './errors.js';
import { ProducerSpool } from './spool.js';
import { UploadFailedError, hashUploadFile, uploadFile, type UploadDeps } from './upload.js';
import { journalFor, type UploadJournal, type UploadJournalLockOptions } from './uploadJournal.js';
import {
  artifactKindMaxBytes,
  closeUploadFile,
  openUploadFile,
  type ArtifactKind,
} from './uploadPaths.js';
import { resolveUploadDeadlineMs } from './uploadRetry.js';

// The kind union the caller maps into is the guard's own closed list (openapi
// `createArtifactUploadUrl.kind`); re-exported so an adapter can type its mapping table
// against the CLI surface without reaching into `uploadPaths.js`.
export type { ArtifactKind } from './uploadPaths.js';

/** One attachment, with the kind the adapter has already mapped it to. */
export interface AttachmentUploadInput {
  readonly runId: string;
  /** This shard's manifest slot id (`shard-<index>`), never a guessed or suffixed one. */
  readonly producerId: string;
  /** The planned execution's sha256 (64 lowercase hex). */
  readonly executionKey: string;
  readonly attemptNo: number;
  readonly kind: ArtifactKind;
  readonly mediaType: string;
  /** The attachment path exactly as the reporter reported it (resolved by the guard). */
  readonly file: string;
}

/** What one accepted attachment produced: the id the event references and the stored size. */
export interface AttachmentUploadResult {
  readonly artifactId: string;
  readonly bytes: number;
}

/**
 * The caller's own bounds for ONE upload. Both are optional: a caller that knows nothing about
 * a drain deadline gets the uploader's own budget.
 */
export interface AttachmentUploadContext {
  /** The budget of this upload; the uploader's own default when absent. */
  readonly deadlineMs?: number;
  /**
   * The caller's drain deadline. Read at this module's checkpoints only: an abort rejects with
   * `upload_aborted` before the file is opened, after the digest, after the bearer and before
   * the engine's first mint. Once `uploadFile()` starts the signal is never checked again, so
   * that PUT is not cancelled and its result is returned.
   */
  readonly signal?: AbortSignal;
}

export interface AttachmentUploader {
  /**
   * Uploads one attachment, or rejects with the CLI's own error (a refused path is
   * `upload_path_refused`, an oversize file `upload_too_large`, an unplanned attempt the
   * server's 422, an abandoned drain `upload_aborted`, and so on). It never resolves with a
   * partial result.
   */
  upload(
    input: AttachmentUploadInput,
    ctx?: AttachmentUploadContext,
  ): Promise<AttachmentUploadResult>;
  /** The static per-kind caps the guard applies, so a caller can pre-screen without I/O. */
  readonly kindMaxBytes: Readonly<Record<ArtifactKind, number | undefined>>;
}

export interface AttachmentUploaderOptions {
  /** The resolved `EVIDGATE_*` surface; the ambient process env is never read directly. */
  readonly env: Readonly<Record<string, string | undefined>>;
  /** The allow-listed roots (Playwright outputDir(s), `--dir` equivalents), absolute. */
  readonly dirs: readonly string[];
  /** The working directory the guard resolves relative roots and the path against. */
  readonly cwd?: string;
  /** Transport seam; its `fetch` is used for the API calls AND the presigned PUT. */
  readonly fetch?: typeof fetch;
  readonly sleep?: (milliseconds: number, signal?: AbortSignal) => Promise<void>;
  readonly now?: () => number;
  readonly random?: () => number;
  /** The default deadline of ONE upload; `EVIDGATE_UPLOAD_DEADLINE` when absent. */
  readonly deadlineMs?: number;
}

/**
 * Builds the uploader. Every configuration error the CLI itself would raise is raised here,
 * synchronously, before any attachment is attempted: `missing_config` when the API url, the
 * project or a token source is unset, `invalid_config` for a malformed value (including a
 * malformed `EVIDGATE_UPLOAD_DEADLINE`, resolved once rather than per attachment).
 */
export function createAttachmentUploader(options: AttachmentUploaderOptions): AttachmentUploader {
  const config: ResolvedConfig = resolveConfig({ env: options.env, requireToken: true });
  const cwd = options.cwd ?? process.cwd();
  const deps: UploadDeps = {
    fetch: options.fetch ?? fetch,
    sleep: options.sleep ?? ((milliseconds) => delay(milliseconds)),
    now: options.now ?? Date.now,
    random: options.random ?? Math.random,
  };
  const defaultDeadlineMs = options.deadlineMs ?? resolveUploadDeadlineMs(options.env);

  // The bearer is fetched once and shared by every attachment of this run (a shard uploads
  // a trace, a video and screenshots per failing test; re-running the exchange per file
  // would multiply the token endpoint). A rejected fetch is NOT memoised: the next upload
  // tries again instead of failing forever on a cached failure.
  let tokenPromise: Promise<string> | undefined;

  async function token(): Promise<string> {
    const pending = (tokenPromise ??= getToken(config, deps).then((result) => result.token));
    try {
      return await pending;
    } catch (error) {
      if (tokenPromise === pending) {
        tokenPromise = undefined;
      }
      throw error;
    }
  }

  /**
   * `upload_aborted`: the caller's drain deadline fired, and this module stopped at its next
   * checkpoint. The code only — no path, no URL, no token (04 §6, S3).
   */
  function uploadAborted(): CliError {
    return new CliError('the attachment upload was abandoned (the drain deadline fired)', {
      code: 'upload_aborted',
    });
  }

  async function upload(
    input: AttachmentUploadInput,
    ctx?: AttachmentUploadContext,
  ): Promise<AttachmentUploadResult> {
    const deadlineMs = ctx?.deadlineMs ?? defaultDeadlineMs;
    const signal = ctx?.signal;
    // A function, not a one-time read: `aborted` flips while this call awaits.
    const aborted = (): boolean => signal?.aborted === true;
    // The budget starts HERE, before the guard: hashing a large trace is part of the upload
    // and must not sit outside the deadline the caller granted.
    const deadlineAt = deps.now() + deadlineMs;
    // Checkpoint 1: an aborted call opens nothing at all.
    if (aborted()) {
      throw uploadAborted();
    }
    // The guard runs before anything else: a file outside `dirs`, a symlink, a non-regular
    // file or one over its kind's cap is refused here, with no HTTP call made.
    const opened = await openUploadFile({
      file: input.file,
      kind: input.kind,
      dirs: options.dirs,
      cwd,
    });
    try {
      // Pass 1 of the two-pass upload. Still no request: the digest is what the `upload-url`
      // body and the signed checksum header are built from.
      const sha256 = await hashUploadFile(opened);

      // Checkpoint 2: the digest is done and nothing has been requested yet, so an abandoned
      // upload costs no token exchange and no mint.
      if (aborted()) {
        throw uploadAborted();
      }

      try {
        const bearer = await token();
        // Checkpoint 3: a token fetch may itself have outlived the deadline.
        if (aborted()) {
          throw uploadAborted();
        }
        // The journal lock waits no longer than the upload's own budget.
        const journal = await openJournal(config, input.runId, input.producerId, {
          deadlineAt,
          now: deps.now,
          sleep: (milliseconds) => deps.sleep(milliseconds),
        });
        // Checkpoint 4: the last one this module controls. What remains of the budget is
        // what the engine gets, so the open, the digest and the journal wait are inside it.
        if (aborted()) {
          throw uploadAborted();
        }
        const outcome = await uploadFile({
          config,
          token: bearer,
          runId: input.runId,
          producerId: input.producerId,
          executionKey: input.executionKey,
          attemptNo: input.attemptNo,
          kind: input.kind,
          mediaType: input.mediaType,
          file: opened,
          sha256,
          journal,
          deadlineMs: Math.max(0, deadlineAt - deps.now()),
          deps,
        });
        return { artifactId: outcome.artifactId, bytes: outcome.bytes };
      } catch (error) {
        if (error instanceof UploadFailedError && error.status === 401) {
          // Best effort: the caller's error is the one that must surface, but a bearer the
          // server just refused must not be reused by the next attachment.
          await deleteCachedToken(config).catch(() => undefined);
          tokenPromise = undefined;
        }
        throw error;
      }
    } finally {
      await closeUploadFile(opened);
    }
  }

  return Object.freeze({ upload, kindMaxBytes: artifactKindMaxBytes });
}

/**
 * The resume store, resolved exactly as `evidgate artifacts upload` resolves it: without
 * `EVIDGATE_SPOOL_DIR` the upload works the same but has no state to resume from, and with it
 * the producer directory is opened like the spool commands do and the journal writes under its
 * `spool.lock` — only around its own writes, so a long PUT never blocks `events send`.
 */
async function openJournal(
  config: ResolvedConfig,
  runId: string,
  producerId: string,
  lock: UploadJournalLockOptions,
): Promise<UploadJournal | undefined> {
  const settings = config.spoolSettings();
  const spoolDir = settings.spoolDir;
  if (spoolDir === undefined) {
    return undefined;
  }
  const spool = await ProducerSpool.open({
    spoolDir,
    runId,
    producerId,
    maxBytes: settings.spoolMaxBytes,
    createIfMissing: true,
  });
  return journalFor({
    spoolDir,
    producerDirectory: spool.directory,
    withLock: (fn) => spool.withLock(fn),
    lock,
  });
}
