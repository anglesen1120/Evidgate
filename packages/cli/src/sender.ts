// The sender posts each packed batch over the one SDK client, at most `maxInFlight` in
// flight under ONE deadline. A retry reuses the same bytes, key and event ids, so a lost
// ACK cannot create a second identity, and every ack is verified before the cursor moves.
//
// This module is the concurrency floor and the single request: dispatch, the lazy pull, the
// serialised ack chain, one attempt and the first fatal error. The caps, retry policy, ack
// verdict and failure types live in `senderLimits.ts`, `senderRetry.ts`, `senderAck.ts`,
// `senderFailure.ts` and `senderRefusal.ts`.
import { postRunEvents, type Client, type EventBatch } from '@evidgate/sdk';

import { createApiClient } from './client.js';

import { maxInFlight } from './senderLimits.js';
import { verifyAcks } from './senderAck.js';
import { deadlineExceeded, stoppedElsewhere } from './senderFailure.js';
import {
  httpRefusal,
  isResponseReadFailure,
  transportRefusal,
  type AttemptOutcome,
} from './senderRefusal.js';
import { attemptSignal, backoffDelay } from './senderRetry.js';

import type { Batch } from './batchPack.js';
import type { SendFailedError } from './senderFailure.js';
import type { BatchAckSummary, Job, SendDeps, SendOptions } from './senderTypes.js';

export { maxInFlight } from './senderLimits.js';
export { SendFailedError } from './senderFailure.js';
export { attemptSignal, backoffDelay, isRetryable, retryAfter } from './senderRetry.js';

export type { SendFailedErrorOptions, SendDeps, SendOptions } from './senderTypes.js';
export type { BatchAckSummary } from './senderTypes.js';
export type { RetryAfterOptions } from './senderRetry.js';

/** The running totals and the one fatal flag the whole send shares. */
interface SendState {
  /** The first failure, which also ends every sibling batch's retry loop. */
  fatal: unknown;
  /** The iterator said it has no more batches. */
  exhausted: boolean;
  delivered: number;
  accepted: number;
  duplicates: number;
}

/** One send's fixed collaborators, threaded through the dispatch helpers. */
interface SendContext {
  readonly client: Client;
  readonly options: SendOptions;
  readonly deadlineAt: number;
  readonly deps: SendDeps;
  readonly pending: Set<Job>;
  readonly state: SendState;
  readonly enqueueAck: (batch: Batch, summary: BatchAckSummary) => Promise<void>;
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
  const state: SendState = {
    fatal: undefined,
    exhausted: false,
    delivered: 0,
    accepted: 0,
    duplicates: 0,
  };
  const context: SendContext = {
    client,
    options,
    deadlineAt,
    deps,
    pending,
    state,
    enqueueAck: createAckChain(onBatchAcked),
  };

  try {
    await pumpBatches({ iterator, limit, context });
  } finally {
    // P3-10: the iterator owns the spool chunk it is reading, so an abandoned one keeps
    // that descriptor open until the process exits. Close it on every exit.
    await closeIterator(iterator, state.fatal);
  }

  if (state.fatal !== undefined) {
    throw state.fatal instanceof Error
      ? state.fatal
      : new Error('the send failed', { cause: state.fatal });
  }
  return { batches: state.delivered, accepted: state.accepted, duplicates: state.duplicates };
}

/**
 * The serialised ack chain: the command writes the cursor on every ack, so two acks
 * resolving at the same time must not interleave two cursor writes.
 */
function createAckChain(
  onBatchAcked: (batch: Batch, summary: BatchAckSummary) => Promise<void>,
): (batch: Batch, summary: BatchAckSummary) => Promise<void> {
  let ackQueue: Promise<void> = Promise.resolve();
  return (batch, summary) => {
    const next = ackQueue.then(() => onBatchAcked(batch, summary));
    // The chain continues even after a rejection, so one failing cursor write does not
    // swallow the acks of the batches that already settled.
    ackQueue = next.then(
      () => undefined,
      () => undefined,
    );
    return next;
  };
}

/**
 * The dispatch loop: prune settled jobs, pull lazily and only up to the concurrency limit,
 * then wait for the first in-flight job. Once something failed nothing more is pulled; the
 * already-dispatched batches keep going.
 */
async function pumpBatches(o: {
  readonly iterator: AsyncIterator<Batch>;
  readonly limit: number;
  readonly context: SendContext;
}): Promise<void> {
  for (;;) {
    for (const job of o.context.pending) {
      if (job.settled) {
        o.context.pending.delete(job);
      }
    }
    await pullUpToLimit(o);
    if (o.context.pending.size === 0) {
      return;
    }
    await Promise.race(Array.from(o.context.pending, (job) => job.promise));
  }
}

/** Fills the in-flight set up to the limit; a failed pull or a done iterator ends the fill. */
async function pullUpToLimit(o: {
  readonly iterator: AsyncIterator<Batch>;
  readonly limit: number;
  readonly context: SendContext;
}): Promise<void> {
  const state = o.context.state;
  while (state.fatal === undefined && !state.exhausted && o.context.pending.size < o.limit) {
    let next: IteratorResult<Batch>;
    try {
      next = await o.iterator.next();
    } catch (error) {
      if (state.fatal === undefined) {
        state.fatal = error;
      }
      return;
    }
    if (next.done === true) {
      state.exhausted = true;
      return;
    }
    if (state.fatal !== undefined) {
      // A dispatched batch failed while we were pulling. This pulled batch is dropped
      // unacked, so the cursor cannot advance past it.
      return;
    }
    dispatchBatch(o.context, next.value);
  }
}

/** Starts one batch and remembers it so the loop can wait on it and drop it when settled. */
function dispatchBatch(context: SendContext, batch: Batch): void {
  const job: Job = { settled: false, promise: Promise.resolve() };
  job.promise = (async (): Promise<void> => {
    try {
      await deliverBatch(context, batch);
    } catch (error) {
      // First failure wins: the later ones are consequences of the same outage.
      if (context.state.fatal === undefined) {
        context.state.fatal = error;
      }
    } finally {
      job.settled = true;
    }
  })();
  context.pending.add(job);
}

/** Sends one batch to completion, then delivers its ack and folds it into the totals. */
async function deliverBatch(context: SendContext, batch: Batch): Promise<void> {
  const summary = await sendBatch(
    context.client,
    context.options,
    batch,
    context.deadlineAt,
    context.deps,
    () => context.state.fatal !== undefined,
  );
  await context.enqueueAck(batch, summary);
  context.state.delivered += 1;
  context.state.accepted += summary.accepted;
  context.state.duplicates += summary.duplicates;
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
 * `Idempotency-Key` and the event ids are the batch's own on every attempt.
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

/** The identity body serializer: the batcher already produced the exact wire bytes. */
const rawBody = (body: unknown): unknown => body;

/**
 * One request. The SDK returns `response: undefined` when the transport itself failed
 * (connection refused, the request timeout aborting the request, a refused redirect). A
 * 2xx whose body dies mid-read rejects instead: that is the same lost connection.
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

  const settled = await postRunEvents({
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
  }).then(
    (outcome) => ({ outcome }),
    (error: unknown) => {
      // Only a body that died after a 2xx answer is retried as a transport loss; every
      // other rejection keeps propagating unchanged.
      if (!isResponseReadFailure(error)) {
        throw error;
      }
      return { readFailure: error };
    },
  );

  if ('readFailure' in settled) {
    return transportRefusal(batch, settled.readFailure);
  }
  const result = settled.outcome;

  const response = result.response;
  if (response === undefined) {
    return transportRefusal(batch, result.error);
  }

  if (response.ok) {
    return { kind: 'acked', summary: verifyAcks(batch, result.data, response) };
  }

  return httpRefusal(batch, response, result.error, deps);
}

/** `Idempotency-Key` (belt-and-braces on top of `event_id` dedupe) plus gzip when used. */
function requestHeaders(batch: Batch, gzip: boolean): Record<string, string> {
  const headers: Record<string, string> = { 'Idempotency-Key': batch.idempotencyKey };
  if (gzip) {
    headers['Content-Encoding'] = 'gzip';
  }
  return headers;
}
