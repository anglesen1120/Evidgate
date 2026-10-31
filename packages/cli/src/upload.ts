// One artifact end to end: `upload-url` → presigned PUT → `complete`. The file is read
// twice with O(1) memory so the checksum exists before the PUT and a changed file aborts.
// Deterministic keys make a retry replay rather than mint a second row, and the presigned
// URL — a bearer credential — never reaches stdout/stderr, the journal, an error or the
// API client. This module owns the resume decision and the call sequence; the pieces it
// drives live in `uploadKeys`, `uploadHeaders`, `uploadBody`, `uploadPut` and
// `uploadResponse`.
import {
  completeArtifact,
  createArtifactUploadUrl,
  type Client,
  type CompleteResponse,
  type UploadUrlResponse,
} from '@evidgate/sdk';

import { createApiClient } from './client.js';
import { CliError } from './errors.js';
import { serverErrorFromResponse, transportError } from './serverError.js';
import { isRetryable, retryAfter } from './sender.js';
import {
  UploadFailedError,
  describeUploadError,
  withAuthHint,
  withStatus,
} from './uploadErrors.js';
import {
  checksumPattern,
  deriveCompleteIdempotencyKey,
  deriveUploadUrlIdempotencyKey,
} from './uploadKeys.js';
import { journalBestEffortWaitMs, maxMintAttempts } from './uploadOptions.js';
import type { UploadOutcome, UploadRequest } from './uploadOptions.js';
import { PresignExpiredError, putWithRetry } from './uploadPut.js';
import { failureCodeLabel, readComplete, readUploadUrl } from './uploadResponse.js';
import { retryAttempts, type Attempt } from './uploadRetry.js';
import {
  isSpoolLocked,
  journalFileName,
  type UploadIdentity,
  type UploadJournal,
  type UploadJournalEntry,
  type UploadJournalPatch,
} from './uploadJournal.js';

export { checksumPattern } from './uploadKeys.js';
export {
  deriveCompleteIdempotencyKey,
  deriveUploadUrlIdempotencyKey,
  hashUploadFile,
} from './uploadKeys.js';
export type { CompleteKeyInput, UploadUrlKeyInput } from './uploadKeys.js';
export {
  journalBestEffortWaitMs,
  maxMintAttempts,
  putStallTimeoutMs,
  uploadChunkBytes,
} from './uploadOptions.js';
export type { UploadDeps, UploadOutcome, UploadRequest, UploadTimers } from './uploadOptions.js';
export { ChecksumMismatchError, UploadFailedError, checksumMismatchCode } from './uploadErrors.js';
export { describeUploadError } from './uploadErrors.js';

/** A request plus the two values the flow derives once: the client and the deadline. */
interface UploadRun extends UploadRequest {
  readonly client: Client;
  readonly deadlineAt: number;
}

/**
 * Runs one upload. The caller has already opened the file with the allowed-directory guard
 * (`uploadPaths.ts`) and hashed it (`hashUploadFile`); this function owns the three calls,
 * the resume decision, the retries and the verification.
 */
export async function uploadFile(request: UploadRequest): Promise<UploadOutcome> {
  if (!checksumPattern.test(request.sha256)) {
    throw new CliError('the local file digest is not sha256:<64 lowercase hex>', {
      code: 'internal_error',
    });
  }
  const size = request.file.size;
  const deadlineAt = request.deps.now() + request.deadlineMs;
  const client = createApiClient(request.config, request.token, request.deps.fetch);
  // The journal identity is every value the `upload-url` body is built from: the same bytes
  // under a different execution, kind, attempt or media type are a different upload and must
  // not resume (or reuse a mint from) the other one (contracts/cli.md §12.5).
  const identity: UploadIdentity = {
    file_sha256: request.sha256,
    size,
    kind: request.kind,
    execution_key: request.executionKey,
    attempt_no: request.attemptNo,
    media_type: request.mediaType,
  };
  const journal = request.journal;
  const resumable = journal === undefined ? undefined : await journal.findResumable(identity);
  if (resumable !== undefined && resumable.complete_done) {
    // A re-run of an upload this journal already committed: the row is terminal, so the
    // call answers without a request (it never mints a second artifact row).
    return {
      artifactId: resumable.artifact_id,
      state: 'ready',
      bytes: size,
      scanState: null,
      mintNo: resumable.mint_no,
    };
  }
  const run: UploadRun = { ...request, client, deadlineAt };
  const { artifactId, mintNo } = await ensureUploaded(run, identity, resumable);
  return await completeUpload({ ...run, artifactId, mintNo, identity });
}

/** Mints the row and PUTs the bytes, from wherever a previous attempt left off. */
async function ensureUploaded(
  run: UploadRun,
  identity: UploadIdentity,
  resumable: UploadJournalEntry | undefined,
): Promise<{ artifactId: string; mintNo: number }> {
  if (resumable === undefined) {
    const mintNo = run.journal === undefined ? 0 : await run.journal.nextMintNo(identity);
    return await mintAndPut({ ...run, mintNo, identity });
  }
  if (resumable.put_done) {
    // The bytes are already in the store: a restart calls ONLY `complete`.
    return { artifactId: resumable.artifact_id, mintNo: resumable.mint_no };
  }
  // Re-request with the same key: the stored response replays the same artifact row.
  return await mintAndPut({ ...run, mintNo: resumable.mint_no, identity });
}

/** Calls `complete` and proves the row it answered for is exactly the uploaded file. */
async function completeUpload(
  o: UploadRun & {
    readonly artifactId: string;
    readonly mintNo: number;
    readonly identity: UploadIdentity;
  },
): Promise<UploadOutcome> {
  const outcome = await completeWithRetry(o);
  if (outcome.state === 'rejected') {
    // `complete_done` stays false: a rejection is replayable, and a re-run must ask the
    // server again (the same row answers `rejected` again) instead of reporting `ready`.
    throw rejectionError(o.artifactId, outcome.failure_code, o.identity);
  }
  const bytes = verifiedBytes(outcome, o.artifactId, o.file.size);
  if (o.journal !== undefined) {
    await updateJournalBestEffort(
      o.journal,
      o.artifactId,
      { complete_done: true },
      o.deps.journalBestEffortWaitMs ?? journalBestEffortWaitMs,
    );
  }
  return {
    artifactId: o.artifactId,
    state: 'ready',
    bytes,
    scanState: outcome.scan_state ?? null,
    mintNo: o.mintNo,
  };
}

/** The `rejected` answer, with the server's code (sanitised) and the way out of the row. */
function rejectionError(
  artifactId: string,
  failureCode: string | null,
  identity: UploadIdentity,
): UploadFailedError {
  const reason = failureCode === null ? 'unknown' : failureCodeLabel(failureCode);
  return new UploadFailedError(`the artifact ${artifactId} was rejected at complete: ${reason}`, {
    code: 'artifact_rejected',
    hint:
      'this row stays rejected; fix the cause and re-run — a changed file has a new ' +
      `digest and mints a new row, otherwise delete <spool>/<run>/<producer>/uploads/${journalFileName(identity, artifactId)} to start over`,
  });
}

/**
 * A `ready` artifact is the stored object, so the server must report exactly the bytes that
 * were signed. A missing count is not `0` and is not the local size: it is a response that
 * cannot prove the artifact exists (openapi `CompleteResponse.bytes` is nullable, so `null`
 * is a shape-valid answer with no evidence behind it).
 */
function verifiedBytes(outcome: CompleteResponse, artifactId: string, size: number): number {
  const bytes = outcome.bytes;
  if (bytes === null) {
    throw new UploadFailedError(
      `the complete response for ${artifactId} did not report a byte count`,
      {
        code: 'request_failed',
        hint: 'the server answered ready without the stored size; re-run the upload',
      },
    );
  }
  if (bytes !== size) {
    throw new UploadFailedError(
      `the server reported ${bytes} bytes for ${artifactId}, but the file is ${size} bytes`,
      {
        code: 'request_failed',
        hint: 'the object store and the local file disagree; re-run the upload',
      },
    );
  }
  return bytes;
}

/**
 * A journal write that must never turn a finished transfer into a failure: once the bytes
 * are in the store, a busy producer lock only costs one repeat of the interrupted step, and
 * the wait is capped so a long-held lock cannot push `complete` into `deadline_exceeded`.
 * Any other journal failure (a corrupt or unsafe record) still surfaces: it means the resume
 * state cannot be trusted.
 */
async function updateJournalBestEffort(
  journal: UploadJournal,
  artifactId: string,
  patch: UploadJournalPatch,
  maxWaitMs: number,
): Promise<void> {
  try {
    await journal.update(artifactId, patch, { maxWaitMs });
  } catch (error) {
    if (!isSpoolLocked(error)) throw error;
  }
}

/** Mints a row and PUTs the bytes, renewing the presigned URL when it expires. */
async function mintAndPut(
  o: UploadRun & {
    readonly mintNo: number;
    readonly identity: UploadIdentity;
  },
): Promise<{ artifactId: string; mintNo: number }> {
  let mintNo = o.mintNo;
  let lastExpiry: CliError | undefined;
  for (let mint = 0; mint < maxMintAttempts; mint += 1) {
    const presigned = await requestUploadUrl({ ...o, mintNo });
    const artifactId = presigned.artifact_id;
    if (o.journal !== undefined) {
      await o.journal.write({
        ...o.identity,
        artifact_id: artifactId,
        put_done: false,
        complete_done: false,
        mint_no: mintNo,
      });
    }
    try {
      await putWithRetry({ ...o, presigned });
      if (o.journal !== undefined) {
        await updateJournalBestEffort(
          o.journal,
          artifactId,
          { put_done: true },
          o.deps.journalBestEffortWaitMs ?? journalBestEffortWaitMs,
        );
      }
      return { artifactId, mintNo };
    } catch (error) {
      if (!(error instanceof PresignExpiredError)) {
        throw error;
      }
      // A presigned URL cannot be re-signed for an existing row, so the dead row is
      // abandoned (it expires under `pending/`) and a fresh mint is requested. The journal
      // row keeps its mint_no, so `nextMintNo` can never reuse that key.
      lastExpiry = error.cliError;
      mintNo += 1;
      if (o.journal !== undefined) {
        mintNo = Math.max(mintNo, await o.journal.nextMintNo(o.identity));
      }
    }
  }
  throw (
    lastExpiry ??
    new UploadFailedError('the presigned upload URL could not be renewed', {
      code: 'upload_put_failed',
      hint: 'retry the command; the server keeps minting new presigned URLs',
    })
  );
}

/** One `upload-url` call, retried per the shared policy. */
async function requestUploadUrl(
  o: UploadRun & {
    readonly mintNo: number;
  },
): Promise<UploadUrlResponse> {
  const key = deriveUploadUrlIdempotencyKey({
    runId: o.runId,
    producerId: o.producerId,
    executionKey: o.executionKey,
    attemptNo: o.attemptNo,
    kind: o.kind,
    fileSha256: o.sha256,
    mintNo: o.mintNo,
  });
  return retryAttempts(
    async (_attemptNo, signal): Promise<Attempt<UploadUrlResponse>> => {
      const result = await createArtifactUploadUrl({
        client: o.client,
        headers: { 'Idempotency-Key': key },
        body: {
          run_id: o.runId,
          producer_id: o.producerId,
          execution_key: o.executionKey,
          attempt_no: o.attemptNo,
          kind: o.kind,
          media_type: o.mediaType,
          bytes: o.file.size,
          sha256: o.sha256,
        },
        signal,
      });
      const response = result.response;
      if (response === undefined) {
        return retryable(transportError(result.error));
      }
      if (!response.ok) {
        const mapped = serverErrorFromResponse(result.error, response, {
          code: 'request_failed',
          message: `the upload-url request failed (HTTP ${response.status})`,
        });
        return {
          ok: false,
          error: withAuthHint(withStatus(mapped, response.status), response.status),
          retryable: isRetryable(response.status, result.error),
          ...retryAfterMs(response, o.deps.now()),
        };
      }
      return { ok: true, value: readUploadUrl(result.data) };
    },
    { deadlineAt: o.deadlineAt, deps: o.deps, describe: describeUploadError },
  );
}

/** One `complete` call, resumed from the journal when the process died after `put_done`. */
async function completeWithRetry(
  o: UploadRun & {
    readonly artifactId: string;
    readonly mintNo: number;
  },
): Promise<CompleteResponse> {
  const key = deriveCompleteIdempotencyKey({
    artifactId: o.artifactId,
    fileSha256: o.sha256,
    size: o.file.size,
  });
  return retryAttempts(
    async (_attemptNo, signal): Promise<Attempt<CompleteResponse>> => {
      const result = await completeArtifact({
        client: o.client,
        path: { a: o.artifactId },
        headers: { 'Idempotency-Key': key },
        signal,
      });
      const response = result.response;
      if (response === undefined) {
        return retryable(transportError(result.error));
      }
      if (!response.ok) {
        const mapped = serverErrorFromResponse(result.error, response, {
          code: 'request_failed',
          message: `the artifact complete failed (HTTP ${response.status})`,
        });
        // 401 needs the auth hint; 409/404/422 are deterministic and never retried.
        return {
          ok: false,
          error: withAuthHint(withStatus(mapped, response.status), response.status),
          retryable: isRetryable(response.status, result.error),
          ...retryAfterMs(response, o.deps.now()),
        };
      }
      return { ok: true, value: readComplete(result.data, o.artifactId) };
    },
    { deadlineAt: o.deadlineAt, deps: o.deps, describe: describeUploadError },
  );
}

function retryable(error: unknown): Attempt<never> {
  return { ok: false, error, retryable: true };
}

function retryAfterMs(response: Response, now: number): { readonly retryAfterMs?: number } {
  const value = retryAfter(response, now);
  return value === undefined ? {} : { retryAfterMs: value };
}
