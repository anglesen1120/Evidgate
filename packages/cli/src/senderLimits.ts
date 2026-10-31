// The caps and codes the sender counts with. Batches in flight, the per-request timeout, the
// backoff bounds and every no-server-code `SendFailedError.code` live here so the retry, ack
// and attempt modules agree on the same numbers and tokens.

/** Batches in flight per run. A caller may only lower it. */
export const maxInFlight = 4;

/**
 * One request's own timeout: a hung connection becomes a retryable transport
 * failure instead of stalling the send. It is also bounded by what is left of the
 * overall deadline.
 */
export const perRequestTimeoutMs = 30_000;
/** Exponential backoff base and cap. */
export const retryBaseMs = 500;
export const retryCapMs = 30_000;
/** A request id is echoed at most this long — the `serverError.ts` text cap. */
export const maxRequestIdLength = 200;
/** The code a transport failure carries on a `SendFailedError`. */
export const transportFailedCode = 'transport_failed';
export const ackMismatchCode = 'ack_mismatch';
export const deadlineExceededCode = 'deadline_exceeded';
/**
 * A batch that stops because another batch (or the iterator) already failed. It never
 * reaches the caller: `sendBatches` throws the first fatal error, and the batch that took
 * this exit is only ever the second one.
 */
export const sendStoppedCode = 'send_stopped';
