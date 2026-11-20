// Classifying one attempt's answer: a transport failure is always retryable and carries no
// status, a non-2xx keeps the server's own code and request id, and only a retryable status
// (or the envelope's own `retryable: false` veto) decides whether another attempt follows.
import { CliError } from './errors.js';
import { serverErrorFromResponse, transportError } from './serverError.js';

import { transportFailedCode } from './senderLimits.js';
import { failure } from './senderFailure.js';
import { isRetryable, retryAfter } from './senderRetry.js';

import type { Batch } from './batchPack.js';
import type { SendFailedError } from './senderFailure.js';
import type { BatchAckSummary, SendDeps } from './senderTypes.js';

/** One attempt ended in a verified ack, or in a failure the caller may or may not retry. */
export type AttemptOutcome =
  | { readonly kind: 'acked'; readonly summary: BatchAckSummary }
  | {
      readonly kind: 'refused';
      readonly error: SendFailedError;
      readonly retryable: boolean;
      readonly retryAfterMs?: number;
    };

/**
 * A 2xx whose body the client could not read to the end rejects the SDK call with the
 * read seam's `response_read_failed`. It is a lost connection, not an answer, so it takes
 * the transport path; the class is matched by its contract code.
 */
export function isResponseReadFailure(error: unknown): boolean {
  return error instanceof CliError && error.code === 'response_read_failed';
}

/** No response at all: always retryable, and it carries no status. */
export function transportRefusal(batch: Batch, error: unknown): AttemptOutcome {
  const transport = transportError(error);
  return {
    kind: 'refused',
    error: failure(batch, {
      code: transportFailedCode,
      message: transport.message,
      requestId: transport.requestId,
      status: undefined,
    }),
    retryable: true,
  };
}

/** A non-2xx answer: the server's own code and request id, and whether it is worth retrying. */
export function httpRefusal(
  batch: Batch,
  response: Response,
  error: unknown,
  deps: SendDeps,
): AttemptOutcome {
  const refusal = serverErrorFromResponse(error, response, {
    code: 'request_failed',
    message: `the event batch was refused (HTTP ${response.status})`,
  });
  const retryAfterMs = retryAfter(response, deps.now());
  return {
    kind: 'refused',
    error: failure(batch, {
      code: refusal.code,
      message: refusal.message,
      requestId: refusal.requestId,
      status: response.status,
    }),
    retryable: isRetryable(response.status, error),
    ...(retryAfterMs === undefined ? {} : { retryAfterMs }),
  };
}
