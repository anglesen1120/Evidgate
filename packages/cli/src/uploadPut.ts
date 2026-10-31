// The presigned PUT: exactly the signed headers, the file streamed from disk, its own
// deadline and idle timeout. The attempt's `Retry-After` and status decide whether the same
// signed request may ever be repeated; an expired URL is a re-mint, not a retry.
import { Readable } from 'node:stream';

import { CliError } from './errors.js';
import { isRetryable, retryAfter } from './sender.js';
import { transportError } from './serverError.js';
import { BodyStream, UploadStalledError } from './uploadBody.js';
import {
  ChecksumMismatchError,
  checksumMismatch,
  checksumMismatchCode,
  describeUploadError,
  UploadFailedError,
} from './uploadErrors.js';
import { presignedUrl, signedHeaders } from './uploadHeaders.js';
import {
  putStallTimeoutMs,
  systemTimers,
  type UploadRequest,
  type UploadTimers,
} from './uploadOptions.js';
import { reopenUploadFile, type UploadFile } from './uploadPaths.js';
import { deadlineExceeded, retryAttempts } from './uploadRetry.js';

import type { UploadUrlResponse } from '@evidgate/sdk';

/** A sentinel so `putWithRetry` can tell a renewal from a retryable transport failure. */
export class PresignExpiredError extends Error {
  constructor(readonly cliError: UploadFailedError) {
    super(cliError.message);
  }
}

/** A deterministic store refusal; surfaced verbatim, never retried. */
export class PutRejectedError extends Error {
  constructor(readonly cliError: UploadFailedError) {
    super(cliError.message);
  }
}

/**
 * A retryable store refusal (408/429/5xx): the same signed PUT can be repeated while the
 * URL is alive, so the retry loop backs off and the shim keeps its `Retry-After`.
 */
export class PutRetryableError extends Error {
  constructor(
    readonly cliError: UploadFailedError,
    readonly retryAfterMs: number | undefined,
  ) {
    super(cliError.message);
  }
}

/**
 * The reason a request was aborted, as an Error: the timer seams abort with a
 * `deadline_exceeded` / `UploadStalledError`, and undici rejects exactly that value.
 */
export function abortReason(signal: AbortSignal): Error {
  const reason: unknown = signal.reason;
  if (reason instanceof Error) return reason;
  return new UploadFailedError('the upload request was aborted', { code: 'upload_put_failed' });
}

interface PutOnceOptions {
  readonly presigned: UploadUrlResponse;
  readonly file: UploadFile;
  readonly sha256: string;
  readonly fetch: typeof fetch;
  readonly deadlineAt: number;
  readonly stallMs: number;
  readonly timers: UploadTimers;
  readonly now: () => number;
}

/** The idle timer of one attempt, and the two ways it stops for good. */
interface StallGuard {
  readonly arm: () => void;
  readonly settle: () => void;
}

/**
 * Arms the idle timer that aborts a PUT which stops making body progress. Two things stop it
 * for good: the generator finishing (the wait for the response is then bounded by the upload
 * deadline alone, so a store that takes longer than the idle budget to verify a large
 * checksum is not aborted and re-PUT from byte 0), and `putOnce`'s `finally`. The settled
 * flag is what makes the stop final: a read that resumes after the handle was closed could
 * otherwise call `onProgress` and arm a fresh timer that nothing would ever clear.
 */
function createStallGuard(o: {
  readonly timers: UploadTimers;
  readonly stallMs: number;
  readonly controller: AbortController;
}): StallGuard {
  let stallTimer: unknown;
  let settled = false;
  const clear = (): void => {
    if (stallTimer !== undefined) {
      o.timers.clear(stallTimer);
      stallTimer = undefined;
    }
  };
  const settle = (): void => {
    settled = true;
    clear();
  };
  const arm = (): void => {
    if (settled) return;
    clear();
    stallTimer = o.timers.set(o.stallMs, () =>
      o.controller.abort(new UploadStalledError(o.stallMs)),
    );
  };
  return { arm, settle };
}

/**
 * The presigned PUT itself: exactly the signed headers, the file streamed from disk.
 * `o.fetch` is the bare fetch, not the API client: the client carries the CI bearer, and
 * that token must never reach the object store — a signed `authorization` is refused besides,
 * so a signing response cannot force it out.
 */
async function putOnce(o: PutOnceOptions): Promise<void> {
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
  const guard = createStallGuard({ timers: o.timers, stallMs: o.stallMs, controller });
  let deadlineTimer: unknown;
  try {
    deadlineTimer = o.timers.set(remaining, () =>
      controller.abort(deadlineExceeded('upload_put_failed')),
    );
    const body = new BodyStream(handle, o.file.size, o.sha256, guard.arm, guard.settle);
    const stream = Readable.from(body, { objectMode: false });
    // A transport that drops the request (an early 2xx from a proxy, an abort) destroys this
    // stream without ever resuming the generator — and a generator that never started runs
    // no `finally`. `close` is therefore what settles `done`; without it `await body.done`
    // would wait forever.
    stream.once('close', () => body.abandon());
    await sendPutRequest(o, { url, headers, body, stream, controller, guard });
  } finally {
    guard.settle();
    if (deadlineTimer !== undefined) o.timers.clear(deadlineTimer);
    await handle.close().catch(() => undefined);
  }
}

/** Sends the body and throws what the store's answer means for this attempt. */
async function sendPutRequest(
  o: PutOnceOptions,
  attempt: {
    readonly url: URL;
    readonly headers: Record<string, string>;
    readonly body: BodyStream;
    readonly stream: Readable;
    readonly controller: AbortController;
    readonly guard: StallGuard;
  },
): Promise<void> {
  let response: Response;
  try {
    attempt.guard.arm();
    response = await o.fetch(attempt.url, {
      method: 'PUT',
      headers: attempt.headers,
      body: attempt.stream,
      duplex: 'half',
      redirect: 'error',
      signal: attempt.controller.signal,
    });
  } catch (error) {
    // A body that aborted because the file changed is not a transport failure: it must
    // never be retried (the same mismatch would repeat) and `complete` must not be called.
    if (attempt.body.failure !== undefined) {
      throw attempt.body.failure;
    }
    throw error;
  }

  if (!response.ok) {
    attempt.body.abort();
    throw refusalFor(response, o.now);
  }
  await settleBody(attempt.body, attempt.controller, o.file.size);
}

/**
 * Waits for the body the store accepted and proves it was the whole file. The response may
 * arrive before the body is flushed; the digest and the size are only trustworthy once the
 * generator finished.
 */
async function settleBody(
  body: BodyStream,
  controller: AbortController,
  size: number,
): Promise<void> {
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
  if (body.bytes !== size) {
    throw checksumMismatch(
      `the file changed while it was uploaded (${size} bytes declared, ${body.bytes} sent)`,
    );
  }
}

/** What a non-2xx store answer means: expired URL, deterministic refusal, or retryable. */
function refusalFor(response: Response, now: () => number): Error {
  if (response.status === 403 || response.status === 404 || response.status === 410) {
    return new PresignExpiredError(
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
  // Only 408/429/5xx are retried; 412 (a conditional or checksum failure) and the
  // other 4xx are deterministic, so the signed request is never sent again.
  if (isRetryable(response.status, undefined)) {
    return new PutRetryableError(refusal, retryAfter(response, now()));
  }
  return new PutRejectedError(refusal);
}

/**
 * The PUT, retried only for 429/408/5xx/transport. A 403/404/410 is the expired-URL signal:
 * it is never retried against the same signature, `mintAndPut` re-mints instead.
 *
 * The attempt's `signal` from `retryAttempts` is deliberately NOT used: it carries the 30 s
 * per-request timeout of the two JSON calls, and a multi-GB trace legitimately takes longer
 * than that. `putOnce` builds its own signal from the remaining upload deadline plus the
 * idle (no body progress) timeout.
 */
export async function putWithRetry(
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
