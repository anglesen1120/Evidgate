// 04.3.3-T2.2: the ONE entry the Playwright adapter uses to ship an attachment as evidence
// (04.3.3 §2 AC5–AC7, §5 "Upload call", §7, §9). The engine underneath is 04.2.4-T1's
// `uploadFile()`; this module is the seam that makes it usable from inside a test process.
//
// Why a facade instead of importing `uploadFile()` from the reporter: the reporter must
// make no policy decision of its own. The configuration surface, the bearer (fetched once
// per run, not once per attachment), the resume journal and the allowed-directory guard are
// resolved here, exactly as `evidgate artifacts upload` resolves them. The caller supplies
// only what it alone knows: the run, this shard's manifest `producer_id`, the planned
// `(execution_key, attempt_no)`, the mapped kind/media type and the attachment path.
//
// Order of operations is observable, and it is what makes a refused attachment cost no
// request at all (§8 traversal/cap rows):
// 1. `resolveConfig` runs at CREATION, so an unresolved API url or token source is the
//    CLI's own `missing_config` before a single attachment is considered;
// 2. per upload the file is opened by the allow-list guard — root containment, symlink,
//    regular-file, ownership and per-kind cap checks — and hashed BEFORE the token is
//    fetched, so neither a disallowed nor an oversize file reaches the network, not even
//    for a token exchange;
// 3. the token is fetched lazily and memoised once per uploader (one shard uploads many
//    attachments). A 401 purges the cached CI token (contracts/cli.md §7) best effort and
//    drops the memo, so the next attachment re-authenticates instead of reusing a bearer
//    the server already refused;
// 4. the resume journal is opened per `(run, producer)` when `EVIDGATE_SPOOL_DIR` is set,
//    so a crashed shard resumes its artifact rows instead of minting a second one;
// 5. the descriptor the guard pinned is closed on EVERY path, including the failing one.
//
// Secret hygiene (04 §6, S3): this module formats no path, token or presigned URL into any
// message — the refusals it rethrows are the guard's own filtered text, and the success
// value is an artifact id plus a byte count. No artifact bytes are buffered here: the
// engine streams the file in bounded chunks (04.2.4 §8).
import { setTimeout as delay } from 'node:timers/promises';

import { getToken } from './auth/getToken.js';
import { deleteCachedToken } from './auth/tokenCache.js';
import { resolveConfig, type ResolvedConfig } from './config.js';
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

/** One attachment, after the adapter mapped it to a kind (§2 AC4, AC7). */
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

export interface AttachmentUploader {
  /**
   * Uploads one attachment, or rejects with the CLI's own error (a refused path is
   * `upload_path_refused`, an oversize file `upload_too_large`, an unplanned attempt the
   * server's 422, and so on). It never resolves with a partial result.
   */
  upload(
    input: AttachmentUploadInput,
    ctx?: { readonly deadlineMs?: number },
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

  async function upload(
    input: AttachmentUploadInput,
    ctx?: { readonly deadlineMs?: number },
  ): Promise<AttachmentUploadResult> {
    const deadlineMs = ctx?.deadlineMs ?? defaultDeadlineMs;
    // The guard runs before anything else: a file outside `dirs`, a symlink, a non-regular
    // file or one over its kind's cap is refused here, with no HTTP call made.
    const opened = await openUploadFile({
      file: input.file,
      kind: input.kind,
      dirs: options.dirs,
      cwd,
    });
    try {
      // Pass 1 of the two-pass upload (04.2.4 §8). Still no request: the digest is what the
      // `upload-url` body and the signed checksum header are built from.
      const sha256 = await hashUploadFile(opened);

      try {
        const bearer = await token();
        const journal = await openJournal(config, input.runId, input.producerId, {
          deadlineAt: deps.now() + deadlineMs,
          now: deps.now,
          sleep: (milliseconds) => deps.sleep(milliseconds),
        });
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
          deadlineMs,
          deps,
        });
        return { artifactId: outcome.artifactId, bytes: outcome.bytes };
      } catch (error) {
        if (error instanceof UploadFailedError && error.status === 401) {
          // Best effort (the caller's error is the one that must surface, 04.2.1 §6): the
          // cached bearer the server just refused may not be reused by the next attachment.
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
 * The resume store, resolved exactly as `evidgate artifacts upload` resolves it (04.2.4 §9,
 * contracts/cli.md §12): without `EVIDGATE_SPOOL_DIR` the upload works the same but has no
 * state to resume from, and with it the producer directory is opened/created like the spool
 * commands do and the journal writes under its `spool.lock` — only around its own writes, so
 * a long PUT never blocks `events send`.
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
