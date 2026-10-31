// 04.2.3-T1.3: the sender — every batch the batcher packed is POSTed to
// `POST /v1/runs/{r}/events` (contracts/openapi.yaml:368-444) over the one SDK client
// (`client.ts`: validated base URL, bearer, `redirect: 'error'`), at most `maxInFlight`
// batches in flight, under ONE deadline for the whole send.
//
// Delivery rules this module owns (04.2.3 §2 AC1/AC5, §5, §7, §8; task §9):
// - the raw bytes the batcher produced are posted as-is (optionally gzipped) with the
//   batch's own `Idempotency-Key`; a retry resends the SAME bytes, the SAME key and
//   therefore the SAME `event_id`s, so a lost ACK can never create a second identity;
// - only 408/429/5xx and transport failures are retried, with full-jitter exponential
//   backoff (500 ms x2, cap 30 s); `Retry-After` (delta-seconds or HTTP-date) is honoured
//   as a floor over that backoff, so `Retry-After: 0` still backs off. An envelope
//   `retryable: false` stops the retry even on a 5xx;
// - 401 and every other 4xx fail fast: the server's `error.code` and `request_id` reach
//   the caller verbatim (T9) and the batch is never silently retried;
// - every ack is verified (one ack per event, same `event_id`/position, status
//   `accepted`/`duplicate`); anything else is `ack_mismatch` and nothing advances
//   ("never advance on doubt", D7/§6);
// - `duplicate` acks count as delivered (a resend after a lost ACK is expected);
// - on the first fatal error the sender stops pulling, starts no further retry attempt or
//   backoff wait on any other batch, lets the in-flight batches settle (their acks still
//   reach `onBatchAcked`, so the cursor moves over them), then throws that first fatal
//   error; the batch iterator is closed on every exit, so its spool chunk is never leaked;
// - the bearer token is never part of a message: refusals reuse `serverErrorFromResponse`
//   (which redacts every registered secret), transport failures use the fixed
//   `transportError` text, and a deadline message names only code/status/request id.
import { isErrorEnvelope, postRunEvents, type Client, type EventBatch } from '@evidgate/sdk';

import type { Batch } from './batcher.js';
import { createApiClient } from './client.js';
import type { ResolvedConfig } from './config.js';
import { escapeControlCharacters } from './errors.js';
import { redact } from './redact.js';
import { serverErrorFromResponse, transportError } from './serverError.js';

/** Batches in flight per run (04.2.3 §8 backoff discipline). A caller may only lower it. */
export const maxInFlight = 4;

/**
 * One request's own timeout (§9.1): a hung connection becomes a retryable transport
 * failure instead of stalling the send. It is also bounded by what is left of the
 * overall deadline.
 */
const perRequestTimeoutMs = 30_000;
/** Exponential backoff base and cap (04.2.3 §5; 00 §9.1). */
const retryBaseMs = 500;
const retryCapMs = 30_000;
/** A request id is echoed at most this long — the `serverError.ts` text cap. */
const maxRequestIdLength = 200;
/** The code a transport failure carries on a `SendFailedError` (task §T1.3 surface). */
const transportFailedCode = 'transport_failed';
const ackMismatchCode = 'ack_mismatch';
const deadlineExceededCode = 'deadline_exceeded';
/**
 * A batch that stops because another batch (or the iterator) already failed. It never
 * reaches the caller: `sendBatches` throws the first fatal error, and the batch that took
 * this exit is only ever the second one.
 */
const sendStoppedCode = 'send_stopped';

/** The effects the send needs, all injected so a spec is deterministic (no real I/O). */
export interface SendDeps {
  readonly fetch: typeof fetch;
  /** Virtual sleep; a spec advances its clock here instead of waiting. */
  readonly sleep: (milliseconds: number, signal?: AbortSignal) => Promise<void>;
  /** Clock in milliseconds; it drives the one deadline (a spec advances it in `sleep`). */
  readonly now: () => number;
  /** Uniform sample in `[0, 1]` for full jitter; a spec fixes it to 0 or 1. */
  readonly random: () => number;
}

/** Everything a send needs besides the batches and the injected effects. */
export interface SendOptions {
  /** The resolved `EVIDGATE_*` configuration (`createApiClient` reads `apiUrl`). */
  readonly config: ResolvedConfig;
  /** The CI producer token; registered for redaction by `createApiClient`. */
  readonly token: string;
  /** The run whose producer slot receives the batches. */
  readonly runId: string;
  /** The one deadline for the whole send, in milliseconds. */
  readonly deadlineMs: number;
  /** Lower the concurrency for a spec; never raises it above `maxInFlight`. */
  readonly maxInFlight?: number;
}

/** What one delivered batch contributed (the `duplicate` count is delivered, too). */
export interface BatchAckSummary {
  readonly accepted: number;
  readonly duplicates: number;
}

export interface SendFailedErrorOptions {
  /**
   * The server's `error.code` verbatim, or `deadline_exceeded`/`transport_failed`/
   * `ack_mismatch` when no server code applies.
   */
  readonly code: string;
  /** The server's `request_id` verbatim, when the answer carried one. */
  readonly requestId?: string;
  /** The HTTP status, when an answer was received (absent for a transport failure). */
  readonly status?: number;
  readonly firstSequence: number;
  readonly lastSequence: number;
}

/**
 * The one failure a send raises. `code`, `requestId` and `status` are the server's values
 * verbatim (T9), so `events send` can print `evidgate: error producer_slot_taken` plus the
 * request id without re-interpreting anything; the batch's sequence range names what did
 * not advance the cursor.
 */
export class SendFailedError extends Error {
  readonly code: string;
  readonly requestId?: string;
  readonly status?: number;
  readonly firstSequence: number;
  readonly lastSequence: number;

  constructor(message: string, options: SendFailedErrorOptions) {
    super(message);
    this.name = 'SendFailedError';
    this.code = options.code;
    this.firstSequence = options.firstSequence;
    this.lastSequence = options.lastSequence;
    if (options.requestId !== undefined) {
      this.requestId = options.requestId;
    }
    if (options.status !== undefined) {
      this.status = options.status;
    }
  }
}

/** One attempt ended in a verified ack, or in a failure the caller may or may not retry. */
type AttemptOutcome =
  | { readonly kind: 'acked'; readonly summary: BatchAckSummary }
  | {
      readonly kind: 'refused';
      readonly error: SendFailedError;
      readonly retryable: boolean;
      readonly retryAfterMs?: number;
    };

/** One dispatched batch; `settled` lets the dispatch loop drop it without a `finally`. */
interface Job {
  settled: boolean;
  promise: Promise<void>;
}

/**
 * Sends every batch the iterator yields.
 *
 * `onBatchAcked` runs once per delivered batch, in completion order and never
 * concurrently (the calls are serialised, so the command can turn them into a
 * contiguous-prefix `spool.ack` safely). `duplicate` acks are delivered, not errors.
 *
 * Returns the totals; throws `SendFailedError` for the first fatal failure after the
 * in-flight batches have settled (and had their acks delivered). An error thrown by the
 * batch iterator itself (e.g. `spool_corrupt`) is re-thrown unchanged after the same
 * settle, so the spool error keeps its own type.
 */
export async function sendBatches(
  batches: AsyncIterable<Batch>,
  options: SendOptions,
  deps: SendDeps,
  onBatchAcked: (batch: Batch, summary: BatchAckSummary) => Promise<void>,
): Promise<{ batches: number; accepted: number; duplicates: number }> {
  const client = createApiClient(options.config, options.token, deps.fetch);
  const deadlineAt = deps.now() + options.deadlineMs;
  const limit = concurrencyLimit(options.maxInFlight);
  const iterator = batches[Symbol.asyncIterator]();
  const pending = new Set<Job>();

  let fatal: unknown;
  let exhausted = false;
  let delivered = 0;
  let accepted = 0;
  let duplicates = 0;

  // P3-4/PF4: one fatal error has to end every other batch's retry loop at once. Without
  // it a sibling already backing off a 503/429 would keep sleeping until the deadline
  // (30 min by default) before the first error was reported.
  const stopRequested = (): boolean => fatal !== undefined;

  // The command writes the cursor on every ack, so the calls are chained: two acks
  // resolving at the same time must not interleave two cursor writes.
  let ackQueue: Promise<void> = Promise.resolve();
  const enqueueAck = (batch: Batch, summary: BatchAckSummary): Promise<void> => {
    const next = ackQueue.then(() => onBatchAcked(batch, summary));
    // The chain continues even after a rejection, so one failing cursor write does not
    // swallow the acks of the batches that already settled.
    ackQueue = next.then(
      () => undefined,
      () => undefined,
    );
    return next;
  };

  const deliver = async (batch: Batch): Promise<void> => {
    const summary = await sendBatch(client, options, batch, deadlineAt, deps, stopRequested);
    await enqueueAck(batch, summary);
    delivered += 1;
    accepted += summary.accepted;
    duplicates += summary.duplicates;
  };

  const dispatch = (batch: Batch): void => {
    const job: Job = { settled: false, promise: Promise.resolve() };
    job.promise = (async (): Promise<void> => {
      try {
        await deliver(batch);
      } catch (error) {
        // First failure wins: the later ones are consequences of the same outage.
        if (fatal === undefined) {
          fatal = error;
        }
      } finally {
        job.settled = true;
      }
    })();
    pending.add(job);
  };

  try {
    for (;;) {
      for (const job of pending) {
        if (job.settled) {
          pending.delete(job);
        }
      }
      // Pull lazily and only up to the concurrency limit. Once something failed nothing
      // more is pulled; the already-dispatched batches keep going.
      while (fatal === undefined && !exhausted && pending.size < limit) {
        let next: IteratorResult<Batch>;
        try {
          next = await iterator.next();
        } catch (error) {
          if (fatal === undefined) {
            fatal = error;
          }
          break;
        }
        if (next.done === true) {
          exhausted = true;
          break;
        }
        if (fatal !== undefined) {
          // A dispatched batch failed while we were pulling. This pulled batch is dropped
          // unacked, so the cursor cannot advance past it.
          break;
        }
        dispatch(next.value);
      }
      if (pending.size === 0) {
        break;
      }
      await Promise.race(Array.from(pending, (job) => job.promise));
    }
  } finally {
    // P3-10: the iterator owns the spool chunk it is reading, so an abandoned one keeps
    // that descriptor open until the process exits. Close it on every exit.
    await closeIterator(iterator, fatal);
  }

  if (fatal !== undefined) {
    throw fatal instanceof Error ? fatal : new Error('the send failed', { cause: fatal });
  }
  return { batches: delivered, accepted, duplicates };
}

/**
 * Closes the batch iterator. A close failure never hides the outcome the send already has:
 * an earlier fatal error wins, while a clean send reports its own close failure.
 */
async function closeIterator(iterator: AsyncIterator<Batch>, fatal: unknown): Promise<void> {
  try {
    await iterator.return?.(undefined);
  } catch (error) {
    if (fatal === undefined) {
      throw error;
    }
  }
}

/** The effective concurrency: a caller may lower it, never raise it past `maxInFlight`. */
function concurrencyLimit(requested: number | undefined): number {
  if (requested === undefined || !Number.isFinite(requested)) {
    return maxInFlight;
  }
  return Math.min(Math.max(Math.trunc(requested), 1), maxInFlight);
}

/**
 * One batch, retried until it is acked or the deadline/failure ends it. The bytes, the
 * `Idempotency-Key` and the event ids are the batch's own on every attempt (AC5).
 *
 * `stopRequested` is the send's own fatal flag: once a sibling batch (or the iterator)
 * has failed nothing else can win, so this batch starts no further attempt and takes no
 * further backoff wait (P3-4). A request already in flight is left to finish, so its ack
 * still reaches `onBatchAcked`.
 */
async function sendBatch(
  client: Client,
  options: SendOptions,
  batch: Batch,
  deadlineAt: number,
  deps: SendDeps,
  stopRequested: () => boolean,
): Promise<BatchAckSummary> {
  let lastFailure: SendFailedError | undefined;
  let attempts = 0;

  for (;;) {
    if (stopRequested()) {
      throw stoppedElsewhere(batch);
    }
    if (deadlineAt - deps.now() <= 0) {
      throw deadlineExceeded(batch, lastFailure);
    }
    attempts += 1;
    const outcome = await attemptOnce(client, options, batch, deadlineAt, deps);
    if (outcome.kind === 'acked') {
      return outcome.summary;
    }
    lastFailure = outcome.error;
    if (!outcome.retryable) {
      throw outcome.error;
    }

    const remaining = deadlineAt - deps.now();
    if (remaining <= 0) {
      throw deadlineExceeded(batch, lastFailure);
    }
    // The wait is the later of `Retry-After` and this attempt's jittered backoff, so a
    // `Retry-After: 0` (or an HTTP date already past) still backs off instead of spinning
    // on repeated 429s. A wait that would reach the deadline is a deadline failure, not a
    // shorter retry.
    const waitMs = Math.max(outcome.retryAfterMs ?? 0, backoffDelay(attempts, deps.random()));
    if (waitMs >= remaining) {
      throw deadlineExceeded(batch, lastFailure);
    }
    if (stopRequested()) {
      // The failure landed while this attempt was in flight: do not hold the send open
      // with a backoff that can no longer deliver anything.
      throw stoppedElsewhere(batch);
    }
    await deps.sleep(waitMs);
  }
}

/**
 * The batch that stops because the send already failed elsewhere. `sendBatches` reports
 * the first fatal error, so this one is never the error the caller sees; it exists so a
 * stopped batch is never mistaken for a delivered one.
 */
function stoppedElsewhere(batch: Batch): SendFailedError {
  return failure(batch, {
    code: sendStoppedCode,
    message:
      `the send stopped before batch ${batch.firstSequence}-${batch.lastSequence} ` +
      'was acknowledged',
    requestId: undefined,
    status: undefined,
  });
}

/** The identity body serializer: the batcher already produced the exact wire bytes. */
const rawBody = (body: unknown): unknown => body;

/**
 * One request. The SDK returns `response: undefined` when the transport itself failed
 * (connection refused, the request timeout aborting the request, a refused redirect).
 */
async function attemptOnce(
  client: Client,
  options: SendOptions,
  batch: Batch,
  deadlineAt: number,
  deps: SendDeps,
): Promise<AttemptOutcome> {
  const gzipBody = batch.gzipBody;
  const gzip = gzipBody !== undefined;
  const payload = gzipBody ?? batch.body;

  const result = await postRunEvents({
    client,
    path: { r: options.runId },
    // `PostRunEventsData` types `body` as the JSON `EventBatch` and `headers` as the CSRF
    // header only; this route needs the raw bytes the batcher produced (optionally
    // gzipped) plus the batch key, so both are widened here. The identity serializer
    // replaces the SDK's default `JSON.stringify`, which would turn a Buffer into
    // `{"type":"Buffer",…}`: `createConfig()` installs `jsonBodySerializer` in the
    // client's own config, and only a per-call serializer wins over it.
    body: payload as unknown as EventBatch,
    bodySerializer: rawBody,
    headers: requestHeaders(batch, gzip),
    signal: attemptSignal(deadlineAt, deps.now()),
  });

  const response = result.response;
  if (response === undefined) {
    const transport = transportError(result.error);
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

  if (response.ok) {
    return { kind: 'acked', summary: verifyAcks(batch, result.data, response) };
  }

  const refusal = serverErrorFromResponse(result.error, response, {
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
    retryable: isRetryable(response.status, result.error),
    ...(retryAfterMs === undefined ? {} : { retryAfterMs }),
  };
}

/** `Idempotency-Key` (belt-and-braces on top of `event_id` dedupe) plus gzip when used. */
function requestHeaders(batch: Batch, gzip: boolean): Record<string, string> {
  const headers: Record<string, string> = { 'Idempotency-Key': batch.idempotencyKey };
  if (gzip) {
    headers['Content-Encoding'] = 'gzip';
  }
  return headers;
}

/**
 * The per-request timeout, additionally bounded by what is left of the send deadline, so
 * a hung connection can never outlive the deadline. Node unrefs both timers.
 */
export function attemptSignal(deadlineAt: number, now: number): AbortSignal {
  const timeout = AbortSignal.timeout(perRequestTimeoutMs);
  const remaining = deadlineAt - now;
  if (!Number.isFinite(remaining) || remaining <= 0) {
    return timeout;
  }
  return AbortSignal.any([timeout, AbortSignal.timeout(Math.ceil(remaining))]);
}

/**
 * Retryable per 04.2.3 §5: 408, 429 and 5xx, unless the 00 §9.2 envelope says
 * `retryable: false` (a permanent 5xx must not be retried). Every other status — 401,
 * 403, 404, 409, 413, 422 — is permanent.
 */
export function isRetryable(status: number, error: unknown): boolean {
  const retryable = status === 408 || status === 429 || status >= 500;
  if (!retryable) {
    return false;
  }
  return !(isErrorEnvelope(error) && error.error.retryable === false);
}

/** Delta-seconds or the HTTP-date form `Retry-After` is allowed to take. */
const deltaSecondsPattern = /^[0-9]+$/;
const httpDatePattern = /^[A-Za-z]{3}, \d{2} [A-Za-z]{3} \d{4} \d{2}:\d{2}:\d{2} GMT$/;

export interface RetryAfterOptions {
  /**
   * Accept only those two forms. `Date.parse` also reads text like `-5` or `1.5` as a past
   * date, and a caller for which 0 would mean "retry at once" must not honour one.
   */
  readonly strict?: boolean;
}

/**
 * `Retry-After` as delta-seconds or an HTTP-date. An absent or unparsable value is
 * `undefined` (the caller falls back to jittered backoff); a date in the past is 0.
 * The caller treats this value as a floor under the jittered backoff, so a 0 never means
 * "retry at once".
 */
export function retryAfter(
  response: Response,
  now: number,
  options: RetryAfterOptions = {},
): number | undefined {
  const raw = response.headers.get('retry-after');
  if (raw === null) {
    return undefined;
  }
  const value = raw.trim();
  if (value === '') {
    return undefined;
  }
  if (deltaSecondsPattern.test(value)) {
    return Number(value) * 1000;
  }
  if (options.strict === true && !httpDatePattern.test(value)) {
    return undefined;
  }
  const date = Date.parse(value);
  if (Number.isNaN(date)) {
    return undefined;
  }
  return Math.max(date - now, 0);
}

/** Full-jitter backoff: `random() * min(500 ms x 2^(attempt-1), 30 s)` (00 §9.1). */
export function backoffDelay(attempts: number, sample: number): number {
  const ceiling = Math.min(retryBaseMs * 2 ** (attempts - 1), retryCapMs);
  const bounded = Math.min(Math.max(sample, 0), 1);
  return Math.min(Math.floor(bounded * ceiling), retryCapMs);
}

/**
 * Verifies a 200 body before anything advances (D7, "never advance on doubt"): one ack
 * per event, in request order, for the same `event_id`, with status `accepted` or
 * `duplicate`, and with the body's `accepted`/`duplicates` counts matching the acks.
 * `duplicate` counts as delivered (it is a resend of a batch whose ACK was lost).
 */
function verifyAcks(batch: Batch, data: unknown, response: Response): BatchAckSummary {
  const body: Record<string, unknown> =
    typeof data === 'object' && data !== null ? (data as Record<string, unknown>) : {};
  const acks = body['acks'];
  const expected = batch.events.length;
  if (!Array.isArray(acks) || acks.length !== expected) {
    throw ackMismatch(batch, response, 'the number of acknowledgements did not match the batch');
  }

  let accepted = 0;
  let duplicates = 0;
  for (let index = 0; index < expected; index += 1) {
    const event = batch.events[index];
    const ack: unknown = acks[index];
    if (event === undefined || typeof ack !== 'object' || ack === null) {
      throw ackMismatch(batch, response, `acknowledgement ${index} was missing`);
    }
    const record = ack as Record<string, unknown>;
    if (record['index'] !== index) {
      throw ackMismatch(batch, response, `acknowledgement ${index} was out of order`);
    }
    // The server echoes a Guid (`event_id` is `format: uuid`); the wire spelling is
    // lowercase, so a UUID the producer wrote in another case is the same identity.
    const eventId = record['event_id'];
    const sameEvent =
      typeof eventId === 'string' &&
      (eventId === event.eventId || eventId.toLowerCase() === event.eventId.toLowerCase());
    if (!sameEvent) {
      throw ackMismatch(batch, response, `acknowledgement ${index} named another event`);
    }
    if (record['ack'] === 'accepted') {
      accepted += 1;
    } else if (record['ack'] === 'duplicate') {
      duplicates += 1;
    } else {
      throw ackMismatch(batch, response, `acknowledgement ${index} was not accepted or duplicate`);
    }
  }

  if (body['accepted'] !== accepted || body['duplicates'] !== duplicates) {
    throw ackMismatch(batch, response, 'the acknowledgement counts did not match the acks');
  }
  return { accepted, duplicates };
}

/** A 200 whose body cannot be trusted; nothing advanced, and the request id is kept. */
function ackMismatch(batch: Batch, response: Response, reason: string): SendFailedError {
  return failure(batch, {
    code: ackMismatchCode,
    message: `the event batch was acknowledged inconsistently: ${reason}`,
    requestId: responseRequestId(response),
    status: response.status,
  });
}

/** The 200's `X-Request-Id`, sanitised exactly like `serverError.ts` treats a header. */
function responseRequestId(response: Response): string | undefined {
  const raw = response.headers.get('x-request-id');
  if (raw === null || raw.length === 0) {
    return undefined;
  }
  return escapeControlCharacters(Array.from(redact(raw)).slice(0, maxRequestIdLength).join(''));
}

/**
 * The deadline failure that names the last error (its code, HTTP status and request id) —
 * never the request, the URL or the bearer.
 */
function deadlineExceeded(batch: Batch, last: SendFailedError | undefined): SendFailedError {
  let detail = 'no attempt could be made';
  if (last !== undefined) {
    const parts = [last.code];
    if (last.status !== undefined) {
      parts.push(`HTTP ${last.status}`);
    }
    if (last.requestId !== undefined) {
      parts.push(`request_id ${last.requestId}`);
    }
    detail = `the last error was ${parts.join(', ')}`;
  }
  return failure(batch, {
    code: deadlineExceededCode,
    message:
      `the send deadline expired before batch ${batch.firstSequence}-${batch.lastSequence} ` +
      `was acknowledged: ${detail}`,
    requestId: last?.requestId,
    status: last?.status,
  });
}

/** Builds a `SendFailedError` carrying the batch's range and only defined extras. */
function failure(
  batch: Batch,
  options: {
    readonly code: string;
    readonly message: string;
    readonly requestId: string | undefined;
    readonly status: number | undefined;
  },
): SendFailedError {
  return new SendFailedError(options.message, {
    code: options.code,
    firstSequence: batch.firstSequence,
    lastSequence: batch.lastSequence,
    ...(options.requestId === undefined ? {} : { requestId: options.requestId }),
    ...(options.status === undefined ? {} : { status: options.status }),
  });
}
