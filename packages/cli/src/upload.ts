// 04.2.4-T1.1/T1.2: `uploadFile()` — one artifact end to end: `POST /v1/artifacts/
// upload-url` → signed presigned PUT → `POST /v1/artifacts/{a}/complete` (04.2.4 §2
// AC1–AC4, §5, §6, §8; openapi `createArtifactUploadUrl` / `completeArtifact`).
//
// Reading discipline (04.2.4 §8 "single streaming pass / no double read of multi-GB
// traces"): the signed checksum header must exist BEFORE the PUT, and the only way to know
// it is to read the file. So the file is read TWICE with O(1) memory each time — pass 1
// hashes it in bounded chunks, pass 2 streams it as the PUT body while hashing again — and
// the second pass is verified against the first: if the bytes changed between the passes
// (any size change or any digest change) the request is aborted before `complete` and the
// upload fails `upload_checksum_mismatch`. No pass ever holds the file, or a window of it
// larger than one chunk, in memory. The plan's "one pass" means "no whole-file buffering,
// no temp copy", which is exactly what this does; `upload.test.ts` asserts the bound.
//
// Idempotency (T85/T92, 04.2.4 AC1/AC3):
// - the `upload-url` `Idempotency-Key` is the AC1 formula — sha256 over the length-prefixed
//   UTF-8 fields `run_id, producer_id, execution_key, attempt_no, kind, file_sha256` — so a
//   retried request for the same artifact replays the stored response and never mints a
//   second row. A renewal mint (the presigned URL expired before any byte was accepted)
//   appends the mint number as a seventh length-prefixed field, so it is still deterministic
//   across restarts but can never replay the dead row's stored response;
// - `complete` carries its own deterministic key (artifact id + digest + size), so a
//   resumed `complete` after a crash is the same request, not a second outcome.
//
// Retry/expiry (04.2.4 §5, §8): only 408/429/5xx/transport are retried (delegated to
// `uploadRetry.ts`, which reuses `sender.ts`'s backoff/`Retry-After`/timeout). A PUT refused
// with 403/404/410 means the presigned URL is dead (T18: 24 h `pending/`, ≤ 15 min URL): the
// CLI re-requests `upload-url` as a fresh mint and retries the PUT against the new row,
// bounded by the wall clock and a small mint cap. Deterministic 4xx are never retried and
// surface verbatim.
//
// Secret hygiene (S3, 04 §6): the presigned URL is a bearer credential. It is never
// printed, never written to the journal, never put in an error message, and the PUT is
// made with the operator's bare `fetch` — NOT through the API client — so the API bearer
// is never sent to the object store. Only the server's `error.code`, `request_id` and the
// HTTP status are echoed.
import { createHash, type Hash } from 'node:crypto';
import { Readable } from 'node:stream';

import {
  completeArtifact,
  createArtifactUploadUrl,
  isErrorEnvelope,
  type Client,
  type CompleteResponse,
  type UploadUrlResponse,
} from '@evidgate/sdk';

import { createApiClient } from './client.js';
import type { ResolvedConfig } from './config.js';
import { CliError } from './errors.js';
import { serverErrorFromResponse, transportError } from './serverError.js';
import { isRetryable, retryAfter } from './sender.js';
import { reopenUploadFile, type ArtifactKind, type UploadFile } from './uploadPaths.js';
import {
  artifactIdPattern,
  isSpoolLocked,
  journalFileName,
  type UploadIdentity,
  type UploadJournal,
  type UploadJournalPatch,
} from './uploadJournal.js';
import {
  deadlineExceeded,
  retryAttempts,
  type Attempt,
  type UploadRetryDeps,
} from './uploadRetry.js';

import type { FileHandle } from 'node:fs/promises';

/** The digest form the server's `sha256` body member and the journal use. */
export const checksumPattern = /^sha256:[0-9a-f]{64}$/;
/**
 * How many times one invocation may mint a fresh `upload-url` row after an expired
 * presigned URL. Each mint is a new server row (the old one expires in `pending/`), so the
 * cap keeps a broken clock or a misconfigured store from littering the tenant.
 */
export const maxMintAttempts = 3;
/** The chunk size of both passes: 64 KiB, so memory stays flat for a multi-GB trace. */
export const uploadChunkBytes = 64 * 1024;
/**
 * How long the presigned PUT may make NO body progress before it is aborted and retried.
 * The body of a multi-GB trace legitimately takes minutes, so the PUT has no per-request
 * cap (`sender.ts`'s 30 s applies to the two JSON calls); a connection that stops moving is
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

const systemTimers: UploadTimers = {
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
  /** `pending` right after `complete`; the secret scan is asynchronous (T19). */
  readonly scanState: string | null;
  /** The artifact row's mint (`0` for the first `upload-url`). */
  readonly mintNo: number;
}

/** A `CliError` that also carries the HTTP status, so 401 can purge the token cache. */
export class UploadFailedError extends CliError {
  readonly status: number | undefined;

  constructor(
    message: string,
    options: ConstructorParameters<typeof CliError>[1],
    status?: number,
  ) {
    super(message, options);
    this.status = status;
  }
}

/** Inputs of the AC1 `upload-url` key: exactly the values the request body is built from. */
export interface UploadUrlKeyInput {
  readonly runId: string;
  readonly producerId: string;
  readonly executionKey: string;
  readonly attemptNo: number;
  readonly kind: ArtifactKind;
  /** `sha256:<64 hex>` — the local digest, not the base64 form the header uses. */
  readonly fileSha256: string;
  /** The mint number: `0` is the AC1 key, `n` a deterministic renewal (see the module doc). */
  readonly mintNo: number;
}

/**
 * The `upload-url` `Idempotency-Key` (04.2.4 AC1, T85/T92).
 *
 * The pre-image is a length-prefixed concatenation — `` <utf8 bytes>:<value> `` per field —
 * not a bare `‖`, because a bare concatenation is ambiguous (`ab|c` and `a|bc` hash the
 * same). Field order is fixed, `attempt_no` is its decimal spelling, and `kind`/`file_sha256`
 * are canonical already. `mint_no` is appended only when it is not `0`, so the first key is
 * exactly `sha256(run_id ‖ producer_id ‖ execution_key ‖ attempt_no ‖ kind ‖ file_sha256)`
 * and every renewal is a different, equally deterministic key. 64 hex characters, well
 * inside the route's 128-character cap, and identical across process restarts.
 */
export function deriveUploadUrlIdempotencyKey(input: UploadUrlKeyInput): string {
  const fields = [
    input.runId,
    input.producerId,
    input.executionKey,
    String(input.attemptNo),
    input.kind,
    input.fileSha256,
  ];
  let preImage = fields.map((field) => `${Buffer.byteLength(field, 'utf8')}:${field}`).join('');
  if (input.mintNo > 0) {
    const mint = String(input.mintNo);
    preImage += `${Buffer.byteLength(mint, 'utf8')}:${mint}`;
  }
  return createHash('sha256').update(preImage, 'utf8').digest('hex');
}

/** Inputs of the derived `complete` key. */
export interface CompleteKeyInput {
  readonly artifactId: string;
  readonly fileSha256: string;
  readonly size: number;
}

/**
 * The `complete` `Idempotency-Key` (T92): the lowercase hex SHA-256 of the UTF-8 JSON array
 * `['evidgate.artifact-complete-key.v1', artifact_id, file_sha256, size]`. A JSON array is
 * unambiguous and versioned like `deriveFinalizeIdempotencyKey`, so a resumed `complete`
 * is the same request while a different artifact never reuses the key.
 */
export function deriveCompleteIdempotencyKey(input: CompleteKeyInput): string {
  return createHash('sha256')
    .update(
      JSON.stringify([
        'evidgate.artifact-complete-key.v1',
        input.artifactId,
        input.fileSha256,
        input.size,
      ]),
      'utf8',
    )
    .digest('hex');
}

/**
 * Pass 1: hashes the open descriptor in bounded chunks and requires the byte count to equal
 * the `fstat` size. Never buffers the file; a file that changes length while it is hashed is
 * `upload_checksum_mismatch` rather than a digest for bytes nobody will upload.
 */
export async function hashUploadFile(file: UploadFile): Promise<string> {
  const hash = createHash('sha256');
  let bytes = 0;
  // The descriptor, not the path: this pins the exact inode the guard `fstat`ed, so a
  // symlink swapped in between `realpath` and the open cannot be hashed instead.
  const stream = file.handle.createReadStream({
    start: 0,
    autoClose: false,
    highWaterMark: uploadChunkBytes,
  });
  for await (const chunk of stream) {
    const bytesChunk = chunk as Uint8Array;
    hash.update(bytesChunk);
    bytes += bytesChunk.byteLength;
  }
  if (bytes !== file.size) {
    throw checksumMismatch(
      `the file changed while it was hashed (${file.size} bytes expected, ${bytes} read)`,
    );
  }
  return `sha256:${hash.digest('hex')}`;
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

  let artifactId: string;
  let mintNo: number;
  if (resumable !== undefined) {
    mintNo = resumable.mint_no;
    if (resumable.complete_done) {
      // A re-run of an upload this journal already committed: the row is terminal, so the
      // call answers without a request (it never mints a second artifact row).
      return {
        artifactId: resumable.artifact_id,
        state: 'ready',
        bytes: size,
        scanState: null,
        mintNo,
      };
    }
    if (resumable.put_done) {
      // The bytes are already in the store: a restart calls ONLY `complete` (AC3, §6).
      artifactId = resumable.artifact_id;
    } else {
      // Re-request with the same key: the stored response replays the same artifact row.
      ({ artifactId, mintNo } = await mintAndPut({
        ...request,
        client,
        deadlineAt,
        mintNo,
        identity,
      }));
    }
  } else {
    mintNo = journal === undefined ? 0 : await journal.nextMintNo(identity);
    ({ artifactId, mintNo } = await mintAndPut({
      ...request,
      client,
      deadlineAt,
      mintNo,
      identity,
    }));
  }

  const outcome = await completeWithRetry({
    ...request,
    client,
    deadlineAt,
    artifactId,
  });

  if (outcome.state === 'rejected') {
    // `complete_done` stays false: a rejection is replayable, and a re-run must ask the
    // server again (the same row answers `rejected` again) instead of reporting `ready`.
    const reason =
      outcome.failure_code === null ? 'unknown' : failureCodeLabel(outcome.failure_code);
    throw new UploadFailedError(`the artifact ${artifactId} was rejected at complete: ${reason}`, {
      code: 'artifact_rejected',
      hint:
        'this row stays rejected; fix the cause and re-run — a changed file has a new ' +
        `digest and mints a new row, otherwise delete <spool>/<run>/<producer>/uploads/${journalFileName(identity, artifactId)} to start over`,
    });
  }

  // AC3: a `ready` artifact is the stored object, so the server must report exactly the
  // bytes that were signed. A missing count is not `0` and is not the local size: it is a
  // response that cannot prove the artifact exists (openapi `CompleteResponse.bytes` is
  // nullable, so `null` is a shape-valid answer with no evidence behind it).
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
  if (journal !== undefined) {
    await updateJournalBestEffort(
      journal,
      artifactId,
      { complete_done: true },
      request.deps.journalBestEffortWaitMs ?? journalBestEffortWaitMs,
    );
  }
  return {
    artifactId,
    state: 'ready',
    bytes,
    scanState: outcome.scan_state ?? null,
    mintNo,
  };
}

/**
 * A journal write that must never turn a finished transfer into a failure (04.2.4 §12.5):
 * once the bytes are in the store, a busy producer lock only costs the next run one repeat
 * of the interrupted step (`put_done` lost: the same bytes are PUT again, or a fresh mint
 * when the presigned URL has expired; `complete_done` lost: the idempotent `complete` is
 * called again). The wait itself is capped at `maxWaitMs` — `events send` may hold the lock
 * for minutes, and spending the rest of the upload deadline on it would make `complete` end
 * in `deadline_exceeded` for a transfer that already succeeded. Any other journal failure (a
 * corrupt or unsafe record) still surfaces: it means the resume state cannot be trusted.
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
  o: UploadRequest & {
    readonly client: Client;
    readonly deadlineAt: number;
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
      // T18: a presigned URL cannot be re-signed for an existing row, so the dead row is
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
  o: UploadRequest & {
    readonly client: Client;
    readonly deadlineAt: number;
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

/**
 * The PUT, retried only for 429/408/5xx/transport. A 403/404/410 is the expired-URL signal
 * (T18): it is never retried against the same signature, `mintAndPut` re-mints instead.
 *
 * The attempt's `signal` from `retryAttempts` is deliberately NOT used: it carries the 30 s
 * per-request timeout of the two JSON calls, and a multi-GB trace legitimately takes longer
 * than that. `putOnce` builds its own signal from the remaining upload deadline plus the
 * idle (no body progress) timeout.
 */
async function putWithRetry(
  o: UploadRequest & {
    readonly presigned: UploadUrlResponse;
    readonly deadlineAt: number;
  },
): Promise<void> {
  await retryAttempts(
    async () => {
      try {
        await putOnce({
          presigned: o.presigned,
          file: o.file,
          sha256: o.sha256,
          fetch: o.deps.fetch,
          deadlineAt: o.deadlineAt,
          stallMs: o.deps.putStallTimeoutMs ?? putStallTimeoutMs,
          timers: o.deps.timers ?? systemTimers,
          now: o.deps.now,
        });
        return { ok: true as const, value: undefined };
      } catch (error) {
        if (
          error instanceof PresignExpiredError ||
          error instanceof ChecksumMismatchError ||
          (error instanceof CliError && error.code === checksumMismatchCode)
        ) {
          throw error;
        }
        if (error instanceof PutRejectedError) {
          return {
            ok: false as const,
            error: error.cliError,
            retryable: false,
          };
        }
        if (error instanceof PutRetryableError) {
          return {
            ok: false as const,
            error: error.cliError,
            retryable: true,
            ...(error.retryAfterMs === undefined ? {} : { retryAfterMs: error.retryAfterMs }),
          };
        }
        // A signed-header or presigned-URL guard refused the response: deterministic, so it
        // surfaces as-is instead of becoming a retried transport failure.
        if (error instanceof CliError) {
          return { ok: false as const, error, retryable: false };
        }
        return {
          ok: false as const,
          error: transportError(error),
          retryable: true,
        };
      }
    },
    { deadlineAt: o.deadlineAt, deps: o.deps, describe: describeUploadError },
  );
}

/** One `complete` call, resumed from the journal when the process died after `put_done`. */
async function completeWithRetry(
  o: UploadRequest & {
    readonly client: Client;
    readonly deadlineAt: number;
    readonly artifactId: string;
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
        // §5: 401 needs the auth hint; 409/404/422 are deterministic and never retried.
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

const authHint = 'run `evidgate auth login` to refresh the cached CI token';

/** 401 means the cached token is dead: surface the server code with the refresh hint (§5). */
function withAuthHint(error: UploadFailedError, status: number): UploadFailedError {
  if (status !== 401) {
    return error;
  }
  return new UploadFailedError(error.message, { ...errorOptions(error), hint: authHint }, status);
}

/** The presigned PUT itself: exactly the signed headers, the file streamed from disk. */
async function putOnce(o: {
  readonly presigned: UploadUrlResponse;
  readonly file: UploadFile;
  readonly sha256: string;
  readonly fetch: typeof fetch;
  readonly deadlineAt: number;
  readonly stallMs: number;
  readonly timers: UploadTimers;
  readonly now: () => number;
}): Promise<void> {
  // Only the remaining upload deadline bounds this request: the 30 s per-request cap of the
  // two JSON calls would abort any trace that takes longer than half a minute to send.
  const remaining = o.deadlineAt - o.now();
  if (!Number.isFinite(remaining) || remaining <= 0) {
    throw deadlineExceeded('upload_put_failed');
  }
  const url = presignedUrl(o.presigned.upload_url);
  const headers = signedHeaders(o.presigned.required_headers, o.file.size, o.sha256);
  // A FRESH descriptor per attempt, through the same guard: the path is re-opened with
  // O_NOFOLLOW|O_NONBLOCK and its dev/ino/size must equal the ones the hash pass pinned, so
  // a file replaced (or symlinked over) between the passes is refused, not uploaded. It is
  // opened BEFORE a timer is armed, so a refusal here leaves nothing behind to keep the
  // process alive (a leaked deadline timer would pin the CLI for the whole upload budget).
  const handle = await reopenUploadFile(o.file);
  const controller = new AbortController();
  let stallTimer: unknown;
  let deadlineTimer: unknown;
  // The idle timer only guards the SENDING of the body. Two things stop it for good: the
  // generator finishing (`settleTimers` via `onFinish`, N4 — the wait for the response is
  // then bounded by the upload deadline alone, so a store that takes longer than the idle
  // budget to verify a large checksum is not aborted and re-PUT from byte 0), and this
  // function's `finally`. The flag is what makes the stop final: a read that resumes after
  // the handle was closed could otherwise call `onProgress` and arm a fresh timer that
  // nothing would ever clear (N3).
  let timersSettled = false;
  const clearStall = (): void => {
    if (stallTimer !== undefined) {
      o.timers.clear(stallTimer);
      stallTimer = undefined;
    }
  };
  const settleTimers = (): void => {
    timersSettled = true;
    clearStall();
  };
  const armStall = (): void => {
    if (timersSettled) return;
    clearStall();
    stallTimer = o.timers.set(o.stallMs, () => controller.abort(new UploadStalledError(o.stallMs)));
  };
  try {
    deadlineTimer = o.timers.set(remaining, () =>
      controller.abort(deadlineExceeded('upload_put_failed')),
    );
    const body = new BodyStream(handle, o.file.size, o.sha256, armStall, settleTimers);
    const stream = Readable.from(body, { objectMode: false });
    // A transport that drops the request (an early 2xx from a proxy, an abort) destroys this
    // stream without ever resuming the generator — and a generator that never started runs
    // no `finally`. `close` is therefore what settles `done`; without it `await body.done`
    // would wait forever (N3).
    stream.once('close', () => body.abandon());
    let response: Response;
    try {
      armStall();
      response = await o.fetch(url, {
        method: 'PUT',
        headers,
        body: stream,
        duplex: 'half',
        redirect: 'error',
        signal: controller.signal,
      });
    } catch (error) {
      // A body that aborted because the file changed is not a transport failure: it must
      // never be retried (the same mismatch would repeat) and `complete` must not be called.
      if (body.failure !== undefined) {
        throw body.failure;
      }
      throw error;
    }

    if (!response.ok) {
      body.abort();
      if (response.status === 403 || response.status === 404 || response.status === 410) {
        throw new PresignExpiredError(
          new UploadFailedError(
            `the presigned upload URL is no longer valid (HTTP ${response.status})`,
            {
              code: 'upload_put_failed',
              hint: 'the upload URL expired; the command requests a fresh one',
            },
            response.status,
          ),
        );
      }
      const refusal = new UploadFailedError(
        `the object store refused the upload (HTTP ${response.status})`,
        {
          code: 'upload_put_failed',
          hint: 'the store refused the signed request; re-run the upload',
        },
        response.status,
      );
      // §5: only 408/429/5xx are retried; 412 (a conditional or checksum failure) and the
      // other 4xx are deterministic, so the signed request is never sent again.
      if (isRetryable(response.status, undefined)) {
        throw new PutRetryableError(refusal, retryAfter(response, o.now()));
      }
      throw new PutRejectedError(refusal);
    }
    // The response may arrive before the request body has been flushed; the digest and the
    // size are only trustworthy once the generator finished.
    await body.done;
    if (body.failure !== undefined) {
      throw body.failure;
    }
    if (body.abandoned) {
      // The transport dropped the request before the body was flushed (an early 2xx from a
      // proxy, an abort). A byte-count mismatch would blame the file; the abort reason is
      // what actually ended the attempt, and a store that just closed the connection is a
      // retryable transport refusal.
      if (controller.signal.aborted) {
        throw abortReason(controller.signal);
      }
      throw new PutRetryableError(
        new UploadFailedError('the object store closed the upload before the body was sent', {
          code: 'upload_put_failed',
          hint: 'the store dropped the request early; re-run the upload',
        }),
        undefined,
      );
    }
    // The generator's last act is the digest comparison, so `done` resolving means the body
    // matched; the byte count is re-checked here because it is what `complete` verifies.
    if (body.bytes !== o.file.size) {
      throw checksumMismatch(
        `the file changed while it was uploaded (${o.file.size} bytes declared, ${body.bytes} sent)`,
      );
    }
  } finally {
    settleTimers();
    if (deadlineTimer !== undefined) o.timers.clear(deadlineTimer);
    await handle.close().catch(() => undefined);
  }
}

/**
 * The reason a request was aborted, as an Error: the timer seams abort with a
 * `deadline_exceeded` / `UploadStalledError`, and undici rejects exactly that value.
 */
function abortReason(signal: AbortSignal): Error {
  const reason: unknown = signal.reason;
  if (reason instanceof Error) return reason;
  return new UploadFailedError('the upload request was aborted', { code: 'upload_put_failed' });
}

/** The connection stopped moving: retryable, and never confused with a file change. */
class UploadStalledError extends Error {
  constructor(milliseconds: number) {
    super(`the presigned upload made no progress for ${milliseconds} ms`);
    this.name = 'UploadStalledError';
  }
}

/**
 * The PUT body: streams the file in bounded chunks while hashing, and refuses to finish if
 * the bytes are not exactly the declared size and digest — which aborts the request, so a
 * half-sent object is never completed (the `pending/` object expires by lifecycle, T18).
 * It reads the guarded descriptor `putOnce` re-opened (never a path), so a swapped file can
 * not be sent, and it reports every chunk to the idle timer.
 *
 * `done` always settles, whoever ends the request first: the generator itself, the store
 * (an early 2xx — see {@link abandon}), or `putOnce`'s refusal path ({@link abort}).
 */
class BodyStream implements AsyncIterable<Uint8Array> {
  readonly hash: Hash;
  bytes = 0;
  failure: Error | undefined;
  /** The size and the digest matched: the object is exactly the file. */
  finished = false;
  /** The transport dropped the body before it finished, so `bytes` is short. */
  abandoned = false;
  readonly done: Promise<void>;

  private resolveDone: () => void = () => undefined;
  private rejectDone: (error: unknown) => void = () => undefined;
  private settled = false;

  constructor(
    private readonly handle: FileHandle,
    private readonly size: number,
    private readonly sha256: string,
    private readonly onProgress: () => void,
    private readonly onFinish: () => void,
  ) {
    this.hash = createHash('sha256');
    // `Promise.withResolvers` is ES2024 and the workspace targets ES2022 (tsconfig.base).
    this.done = new Promise<void>((resolve, reject) => {
      this.resolveDone = resolve;
      this.rejectDone = reject;
    });
    // `done` is awaited by `putOnce`; this keeps an abort from becoming an unhandled
    // rejection when the response settled first and `done` is never awaited.
    this.done.catch(() => undefined);
  }

  async *[Symbol.asyncIterator](): AsyncGenerator<Uint8Array> {
    try {
      const stream = this.handle.createReadStream({
        start: 0,
        autoClose: false,
        highWaterMark: uploadChunkBytes,
      }) as AsyncIterable<Uint8Array>;
      for await (const chunk of stream) {
        this.bytes += chunk.byteLength;
        if (this.bytes > this.size) {
          throw checksumMismatch(
            `the file grew while it was uploaded (${this.size} bytes declared, more found)`,
          );
        }
        this.hash.update(chunk);
        this.onProgress();
        yield chunk;
      }
      if (this.bytes !== this.size) {
        throw checksumMismatch(
          `the file shrank while it was uploaded (${this.size} bytes declared, ${this.bytes} sent)`,
        );
      }
      if (`sha256:${this.hash.digest('hex')}` !== this.sha256) {
        throw checksumMismatch('the file changed between the hash pass and the upload pass');
      }
      this.finished = true;
      this.settle();
    } catch (error) {
      this.failure = toCliError(error);
      this.fail(this.failure);
      throw this.failure;
    } finally {
      // N4: the last chunk is out (or the generator is being closed), so the idle timer has
      // no meaning any more — the wait for the response is bounded by the deadline alone.
      this.onFinish();
      // N3: a generator closed by a destroyed request must still settle `done`, or
      // `putOnce` would await it forever. Idempotent: a finished or failed body is settled.
      this.abandon();
    }
  }

  /** Stops a request whose response already failed; the body is never read to the end. */
  abort(): void {
    this.settle();
  }

  /**
   * Settles `done` for a body the transport dropped before it finished. The stream's
   * `close` calls this, because a generator that never started runs no `finally` at all.
   */
  abandon(): void {
    if (this.settled) return;
    this.abandoned = true;
    this.settle();
  }

  private settle(): void {
    if (this.settled) return;
    this.settled = true;
    this.resolveDone();
  }

  private fail(error: Error): void {
    if (this.settled) return;
    this.settled = true;
    this.rejectDone(error);
  }
}

/** A sentinel so `putWithRetry` can tell a renewal from a retryable transport failure. */
class PresignExpiredError extends Error {
  constructor(readonly cliError: UploadFailedError) {
    super(cliError.message);
  }
}

/** A deterministic store refusal; surfaced verbatim, never retried. */
class PutRejectedError extends Error {
  constructor(readonly cliError: UploadFailedError) {
    super(cliError.message);
  }
}

/**
 * A retryable store refusal (408/429/5xx): the same signed PUT can be repeated while the
 * URL is alive, so the retry loop backs off and the shim keeps its `Retry-After`.
 */
class PutRetryableError extends Error {
  constructor(
    readonly cliError: UploadFailedError,
    readonly retryAfterMs: number | undefined,
  ) {
    super(cliError.message);
  }
}

/** The file changed under us: abort, never complete, never retry. */
export class ChecksumMismatchError extends CliError {
  constructor(message: string) {
    super(`the artifact upload was aborted: ${message}`, {
      code: checksumMismatchCode,
      hint: 'the file was modified while it was uploaded; stop writing it and re-run the command',
    });
  }
}

export const checksumMismatchCode = 'upload_checksum_mismatch';

function checksumMismatch(message: string): ChecksumMismatchError {
  return new ChecksumMismatchError(message);
}

function toCliError(error: unknown): Error {
  if (error instanceof CliError) {
    return error;
  }
  if (error instanceof Error) {
    return error;
  }
  return new Error('the upload body failed');
}

/**
 * The server's volatile fields are validated before anything advances (never trust a 200):
 * a body that is not the documented shape is `request_failed`, and no URL from it is used.
 */
function readUploadUrl(data: unknown): UploadUrlResponse {
  if (typeof data !== 'object' || data === null) {
    throw badResponse('the upload-url response has no body');
  }
  const body = data as Record<string, unknown>;
  const artifactId = body['artifact_id'];
  const objectKey = body['object_key'];
  const uploadUrl = body['upload_url'];
  const requiredHeaders = body['required_headers'];
  const expiresAt = body['expires_at'];
  if (typeof artifactId !== 'string' || !artifactIdPattern.test(artifactId)) {
    throw badResponse('the upload-url response carries no artifact id');
  }
  // `object_key` is required by the contract (openapi `UploadUrlResponse`), so a body
  // without one is refused exactly like a missing `artifact_id` — never defaulted to ''.
  if (typeof objectKey !== 'string' || objectKey.length === 0) {
    throw badResponse('the upload-url response carries no object key');
  }
  if (typeof uploadUrl !== 'string') {
    throw badResponse('the upload-url response carries no presigned URL');
  }
  // Required, `format: date-time`: an expiry the client cannot read is a malformed body,
  // not a URL with an unknown lifetime.
  if (typeof expiresAt !== 'string' || !Number.isFinite(Date.parse(expiresAt))) {
    throw badResponse('the upload-url response carries no parseable expiry');
  }
  if (typeof requiredHeaders !== 'object' || requiredHeaders === null) {
    throw badResponse('the upload-url response carries no signed headers');
  }
  const headers: Record<string, string> = {};
  for (const [name, value] of Object.entries(requiredHeaders as Record<string, unknown>)) {
    if (typeof value !== 'string') {
      throw badResponse('the upload-url response carries a non-string signed header');
    }
    headers[name] = value;
  }
  return {
    artifact_id: artifactId,
    object_key: objectKey,
    upload_url: uploadUrl,
    required_headers: headers,
    expires_at: expiresAt,
  };
}

/** The `complete` body: `state` decides success, `bytes` is verified against the file. */
function readComplete(data: unknown, artifactId: string): CompleteResponse {
  if (typeof data !== 'object' || data === null) {
    throw badResponse('the complete response has no body');
  }
  const body = data as Record<string, unknown>;
  const state = body['state'];
  const reported = body['artifact_id'];
  if (state !== 'ready' && state !== 'rejected') {
    throw badResponse('the complete response carries no artifact state');
  }
  if (typeof reported !== 'string' || reported.toLowerCase() !== artifactId.toLowerCase()) {
    throw badResponse('the complete response names a different artifact');
  }
  const bytes = body['bytes'];
  if (bytes !== null && bytes !== undefined && !Number.isSafeInteger(bytes)) {
    throw badResponse('the complete response reports a non-integer byte count');
  }
  return {
    artifact_id: reported,
    state,
    bytes: typeof bytes === 'number' ? bytes : null,
    scan_state: typeof body['scan_state'] === 'string' ? body['scan_state'] : null,
    failure_code: typeof body['failure_code'] === 'string' ? body['failure_code'] : null,
  };
}

function badResponse(message: string): UploadFailedError {
  return new UploadFailedError(message, { code: 'request_failed' });
}

/** The presigned URL must be an absolute http(s) URL with no embedded credential. */
function presignedUrl(raw: string): URL {
  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    throw badResponse('the upload-url response is not an absolute URL');
  }
  if (url.protocol !== 'https:' && url.protocol !== 'http:') {
    throw badResponse('the upload-url response is not an http(s) URL');
  }
  if (url.username !== '' || url.password !== '') {
    throw badResponse('the upload-url response embeds a credential');
  }
  return url;
}

/** A lower-cased header name, but only when it is a plain HTTP token: never echoed raw. */
function headerLabel(lowerName: string): string {
  return /^[a-z0-9!#$%&'*+.^_`|~-]{1,64}$/.test(lowerName) ? lowerName : 'same';
}

const failureCodeFallback = 'invalid_failure_code';

/**
 * The server's `failure_code`, as it may appear in a message. A `rejected` complete puts it
 * into the message, and the message is printed to stderr and copied by every `--json`
 * consumer, so only a plain token is echoed verbatim; anything else (an escape sequence that
 * would repaint the terminal, a newline that would forge an `evidgate:` line, an over-long
 * value) becomes {@link failureCodeFallback}. The machine-readable code therefore stays
 * stable, and the raw server text never reaches a stream (§5/§12.6). A missing code is
 * `unknown`, which is what the caller says, not this fallback.
 */
function failureCodeLabel(raw: string): string {
  return /^[A-Za-z0-9_.-]{1,64}$/.test(raw) ? raw : failureCodeFallback;
}

/**
 * Headers a presigned PUT must never carry, whatever the server signs: a credential
 * (`authorization`, `cookie`, `set-cookie`, `proxy-authorization`), the target
 * (`host`), a second framing (`transfer-encoding`, `connection`) — plus any `proxy-*`.
 * The client keeps its own bearer off the object store (04 §6/S3), and a response is not
 * allowed to talk it into doing otherwise.
 *
 * The transport-owned set (`expect`, `te`, `trailer`, `upgrade`, `keep-alive`,
 * `content-encoding`) is refused for the same reason: `fetch` throws a `TypeError` (or
 * `NotSupported`) on most of them, and a thrown `TypeError` reads as a transport failure —
 * it would be retried until the deadline for a response that will never change. Refusing
 * them here makes them the deterministic `request_failed` they are. `content-encoding` is
 * on the list because the body is streamed raw: a signed encoding would either break the
 * length/checksum framing or make the store decode bytes the CLI never encoded.
 */
const forbiddenSignedHeaders: Readonly<Record<string, true>> = {
  authorization: true,
  cookie: true,
  'set-cookie': true,
  host: true,
  'proxy-authorization': true,
  'transfer-encoding': true,
  connection: true,
  expect: true,
  te: true,
  trailer: true,
  upgrade: true,
  'keep-alive': true,
  'content-encoding': true,
};

/** RFC 9110 `token`: the only shape a header name may have (and what `fetch` accepts). */
const headerNamePattern = /^[!#$%&'*+.^_`|~0-9A-Za-z-]{1,128}$/;
/**
 * The characters a header value may carry, negated: `fetch` (undici's `headerCharRegex`)
 * refuses anything outside tab, printable ASCII and Latin-1, and a value it refuses throws a
 * `TypeError`. That throw would read as a transport failure and be retried until the
 * deadline, so the same class — CR/LF (a split field), NUL, ESC (which would repaint a
 * terminal), DEL and any higher code point — is refused here first, deterministically.
 */
const headerValuePattern = /[^\t\u0020-\u007e\u0080-\u00ff]/;

/**
 * Exactly the signed headers, validated against the local file: `Content-Length` must be the
 * declared size and the `x-amz-checksum-sha256` header must equal the base64 of the digest.
 * A response that signs neither is refused — the checksum exists to make the PUT
 * self-verifying (T18), and sending an unsigned length would defeat that.
 */
function signedHeaders(
  headers: Readonly<Record<string, string>>,
  size: number,
  sha256: string,
): Record<string, string> {
  const byLowerName = new Map<string, { readonly name: string; readonly value: string }>();
  for (const [name, value] of Object.entries(headers)) {
    const lower = name.toLowerCase();
    // A name `fetch` would refuse (a space, a colon, a bare non-ASCII byte) is a malformed
    // response, not a transport failure: the name is never echoed when it is not a token.
    if (!headerNamePattern.test(name)) {
      throw badResponse('the upload URL signed a header whose name is not an HTTP token');
    }
    if (headerValuePattern.test(value)) {
      throw badResponse(
        `the upload URL signed the ${headerLabel(lower)} header with a value the request cannot carry`,
      );
    }
    if (forbiddenSignedHeaders[lower] === true || lower.startsWith('proxy-')) {
      // A 201 can name any header; these would let the response make the CLI send a
      // credential, rewrite the target host, or smuggle a second body framing. The value is
      // never echoed — only the name.
      throw badResponse(
        `the upload URL signed the ${headerLabel(lower)} header, which is never sent to the object store`,
      );
    }
    const existing = byLowerName.get(lower);
    // Two names that fold to the same header are one signed header. The same value twice
    // is a harmless duplicate; two different values make the signed set ambiguous (the
    // transport would send whichever it kept), so the response is refused. The value is
    // signed material and never enters the message — only the header name may.
    if (existing !== undefined && existing.value !== value) {
      throw badResponse(
        `the upload URL signed the ${headerLabel(lower)} header twice with different values`,
      );
    }
    if (existing === undefined) {
      byLowerName.set(lower, { name, value });
    }
  }
  const length = byLowerName.get('content-length');
  if (length === undefined) {
    throw badResponse('the upload URL did not sign Content-Length');
  }
  if (length.value !== String(size)) {
    throw badResponse('the signed Content-Length does not match the file size');
  }
  const checksum = byLowerName.get('x-amz-checksum-sha256');
  if (checksum === undefined) {
    throw badResponse('the upload URL did not sign the checksum header');
  }
  const expected = Buffer.from(sha256.slice('sha256:'.length), 'hex').toString('base64');
  if (checksum.value !== expected) {
    throw badResponse('the signed checksum does not match the local file digest');
  }
  const result: Record<string, string> = {};
  for (const { name, value } of byLowerName.values()) {
    result[name] = value;
  }
  return result;
}

function retryable(error: unknown): Attempt<never> {
  return { ok: false, error, retryable: true };
}

function retryAfterMs(response: Response, now: number): { readonly retryAfterMs?: number } {
  const value = retryAfter(response, now);
  return value === undefined ? {} : { retryAfterMs: value };
}

/** Wraps a mapped server error with its HTTP status so a caller can act on 401. */
function withStatus(error: CliError, status: number): UploadFailedError {
  if (error instanceof UploadFailedError) {
    return error;
  }
  return new UploadFailedError(error.message, errorOptions(error), status);
}

function errorOptions(error: CliError): ConstructorParameters<typeof CliError>[1] {
  return {
    code: error.code,
    ...(error.hint === undefined ? {} : { hint: error.hint }),
    ...(error.requestId === undefined ? {} : { requestId: error.requestId }),
    ...(error.details === undefined ? {} : { details: error.details }),
  };
}

/** One line for `deadline_exceeded`: the code, status and request id, never a URL. */
export function describeUploadError(error: unknown): string {
  if (error instanceof UploadFailedError) {
    const status = error.status === undefined ? '' : ` (HTTP ${error.status})`;
    const requestId = error.requestId === undefined ? '' : ` request_id=${error.requestId}`;
    return `${error.code}${status}${requestId}`;
  }
  if (error instanceof CliError) {
    return error.code;
  }
  if (isErrorEnvelope(error)) {
    return error.error.code;
  }
  return 'transport_failed';
}
