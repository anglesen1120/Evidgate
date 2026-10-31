// Packing: spool lines into `{"events":[...]}` bodies with a derived `Idempotency-Key`.
//
// Two invariants decide the design: the body is the exact spool line texts, concatenated and
// never re-serialised, so a resend after a lost ACK or a restart sends the same bytes and the
// server's body hash cannot drift; and packing is streaming, because a 256 MiB spool must
// never land in memory.
//
// Before the schema verdict, each line is also checked against the directory it was read
// from: its `producer_id` must be that producer directory, and a `run_id` it declares must be
// the run being sent. A spool is a made-to-be-tampered input (any local process may write it),
// so a planted line stops the run instead of being sent under another producer's batch key.
import { createHash } from 'node:crypto';
import { gzipSync } from 'node:zlib';

import {
  batchClose,
  batchKeyPrefix,
  batchKeyVersion,
  batchOpen,
  gzipExpansionHeadroom,
  gzipMinRatio,
  gzipMinSavingBytes,
  gzipMinServerExpansionRatio,
  maxBatchBytes,
  maxBatchEvents,
  overheadBytes,
  producerIdMember,
  runIdMember,
  separatorBytes,
} from './batchLimits.js';
import { EnvelopeInvalidError, OversizedEventError, eventIdOf } from './batchErrors.js';

import type { EnvelopeDetail } from './batchErrors.js';
import type { SpoolEvent } from './spool.js';

/**
 * One unit of work for the sender: the events of a batch, the exact body bytes to POST and
 * the derived `Idempotency-Key`.
 *
 * `body` is the raw `{"events":[...]}` JSON, at most {@link maxBatchBytes} bytes; `gzipBody`
 * is present only when compressing actually helps, and the sender decides which
 * representation to transmit.
 */
export interface Batch {
  /** The packed events, in spool order; 1..{@link maxBatchEvents} entries. */
  readonly events: readonly SpoolEvent[];
  /** `sequence` of the first event. */
  readonly firstSequence: number;
  /** `sequence` of the last event. */
  readonly lastSequence: number;
  /** The exact UTF-8 body bytes: `{"events":[` + the event lines + `]}`. */
  readonly body: Buffer;
  /**
   * The gzip-compressed body, present only when it is materially smaller than
   * {@link body} *and* its expansion ratio stays under the server's decompression-bomb
   * cap (see `decideGzip`); otherwise the raw body travels.
   */
  readonly gzipBody?: Buffer;
  /** `batch:` + sha256 hex of the versioned pre-image (see {@link deriveBatchIdempotencyKey}). */
  readonly idempotencyKey: string;
}

/** Options of {@link packBatches}. */
export interface PackBatchesOptions {
  /** The run every event belongs to; part of the idempotency pre-image. */
  readonly runId: string;
  /** The producer directory the lines came from; part of the idempotency pre-image. */
  readonly producerId: string;
  /**
   * Per-line validation, called once per event in spool order. Obtain it from
   * `createEnvelopeValidator`; it throws {@link EnvelopeInvalidError}.
   */
  readonly validate: (line: string, sequence: number) => void;
  /** Override of {@link maxBatchEvents} (tests, or a narrower server cap). */
  readonly maxEvents?: number;
  /** Override of {@link maxBatchBytes}. */
  readonly maxBytes?: number;
}

/**
 * The per-batch `Idempotency-Key`: `batch:` + the lowercase hex SHA-256 of the UTF-8 JSON array
 *
 * ```text
 * ["evidgate.batch-key.v1", run_id, producer_id, [[sequence, event_id], …]]
 * ```
 *
 * A JSON array is unambiguous (no separator can collide with a value) and the member order is
 * fixed, so the same batch derives the same key in a fresh process after a crash: a restart
 * re-sends the identical key and the server de-duplicates instead of storing twice. Any change
 * of `run_id`, `producer_id`, or of one `(sequence, event_id)` pair — including reordering —
 * derives a different key, and an empty batch's key is never reused for a non-empty one. The
 * key feeds no secret (ids only).
 */
export function deriveBatchIdempotencyKey(
  runId: string,
  producerId: string,
  events: readonly SpoolEvent[],
): string {
  const preImage = JSON.stringify([
    batchKeyVersion,
    runId,
    producerId,
    events.map((event) => [event.sequence, event.eventId]),
  ]);
  return batchKeyPrefix + createHash('sha256').update(preImage, 'utf8').digest('hex');
}

/**
 * The identity rejection of one spool line, when the line contradicts the directory it was
 * read from: `producer_id` must be the producer directory the line sits in, and a line that
 * *declares* a `run_id` must declare the run being sent. A line planted in `<run>/A/` that
 * claims producer B would otherwise travel under the batch key derived from A (and be stored
 * under the wrong producer once the server trusted the local framing), so the run stops here.
 *
 * Members that are absent (or not strings) are left to the schema: `producer_id` is
 * `required`, and any `run_id` at all is an `additionalProperties` rejection. This check only
 * adds the mismatch the schema cannot see, and never echoes the line's own value.
 */
function identityMismatchOf(
  line: string,
  runId: string,
  producerId: string,
): { readonly detail: EnvelopeDetail; readonly note: string } | undefined {
  let document: unknown;
  try {
    document = JSON.parse(line);
  } catch {
    // Not JSON at all: `validate` owns that verdict (`invalid_json`), not this check.
    return undefined;
  }
  if (typeof document !== 'object' || document === null || Array.isArray(document)) {
    return undefined;
  }
  const members = document as Record<string, unknown>;

  const declaredProducer = members[producerIdMember];
  if (typeof declaredProducer === 'string' && declaredProducer !== producerId) {
    return {
      detail: { path: `/${producerIdMember}`, reason: 'producer_mismatch' },
      note: `the line is spooled under producer "${producerId}", so its producer_id must match`,
    };
  }

  const declaredRun = members[runIdMember];
  if (typeof declaredRun === 'string' && declaredRun !== runId) {
    return {
      detail: { path: `/${runIdMember}`, reason: 'run_mismatch' },
      note: `the line declares a run_id but not the run being sent ("${runId}")`,
    };
  }
  return undefined;
}

/**
 * Packs spool events into batches: at most `maxEvents` events and at most `maxBytes` body
 * bytes pre-compression, counting the opener, one separator per gap and the closer. The
 * generator pulls from `events` only as far as the batch being built needs — a full batch is
 * yielded before the next event is pulled, so at most one batch of events (plus the single
 * candidate event in hand) is held in memory — and calls `o.validate` per line before the
 * line joins a batch.
 *
 * Order of the per-event checks: the transport cap first (an event that cannot travel fails
 * with its `event_id` even when it is also malformed), then the line↔directory identity (see
 * {@link identityMismatchOf}, which needs one `JSON.parse` per line — the same parse
 * `validate` does, on the same text), then schema validation. A later batch is never produced
 * once a line throws: the run stops at that sequence.
 */
export async function* packBatches(
  events: AsyncIterable<SpoolEvent>,
  o: PackBatchesOptions,
): AsyncGenerator<Batch> {
  const maxEvents = o.maxEvents ?? maxBatchEvents;
  const maxBytes = o.maxBytes ?? maxBatchBytes;
  if (!Number.isSafeInteger(maxEvents) || maxEvents < 1) {
    throw new RangeError('maxEvents must be a positive integer');
  }
  if (!Number.isSafeInteger(maxBytes) || maxBytes <= overheadBytes) {
    throw new RangeError(`maxBytes must be an integer greater than ${overheadBytes}`);
  }

  let batch: SpoolEvent[] = [];
  let batchBytes = overheadBytes;

  for await (const event of events) {
    const lineBytes = Buffer.byteLength(event.line, 'utf8');
    const singleBodyBytes = overheadBytes + lineBytes;
    if (singleBodyBytes > maxBytes) {
      throw new OversizedEventError(event.eventId, event.sequence, singleBodyBytes, maxBytes);
    }

    const identity = identityMismatchOf(event.line, o.runId, o.producerId);
    if (identity !== undefined) {
      const eventId = eventIdOf(event.line);
      throw new EnvelopeInvalidError({
        sequence: event.sequence,
        ...(eventId === undefined ? {} : { eventId }),
        details: [identity.detail],
        note: identity.note,
      });
    }

    o.validate(event.line, event.sequence);

    if (batch.length > 0 && batchBytes + separatorBytes + lineBytes > maxBytes) {
      yield makeBatch(o.runId, o.producerId, batch, batchBytes);
      batch = [];
      batchBytes = overheadBytes;
    }

    batchBytes += (batch.length === 0 ? 0 : separatorBytes) + lineBytes;
    batch.push(event);

    // Yield the moment the batch is full: the next pull then belongs to the next
    // batch, which is what keeps the generator one batch ahead at most.
    if (batch.length >= maxEvents) {
      yield makeBatch(o.runId, o.producerId, batch, batchBytes);
      batch = [];
      batchBytes = overheadBytes;
    }
  }

  if (batch.length > 0) {
    yield makeBatch(o.runId, o.producerId, batch, batchBytes);
  }
}

/** Builds the immutable batch value: exact bytes, gzip decision, derived key. */
function makeBatch(
  runId: string,
  producerId: string,
  events: readonly SpoolEvent[],
  bodyBytes: number,
): Batch {
  const first = events[0];
  const last = events[events.length - 1];
  if (first === undefined || last === undefined) {
    throw new Error('internal: a batch is never empty');
  }

  const body = buildBody(events, bodyBytes);
  const gzipBody = decideGzip(body);
  return {
    events: [...events],
    firstSequence: first.sequence,
    lastSequence: last.sequence,
    body,
    ...(gzipBody === undefined ? {} : { gzipBody }),
    idempotencyKey: deriveBatchIdempotencyKey(runId, producerId, events),
  };
}

/**
 * Concatenates the event lines verbatim into `{"events":[…]}` with a single
 * allocation of exactly `bodyBytes` (the caller already counted them, so no line
 * text is copied through a second JS string and the bytes stay identical across
 * retries).
 */
function buildBody(events: readonly SpoolEvent[], bodyBytes: number): Buffer {
  const body = Buffer.allocUnsafe(bodyBytes);
  let offset = body.write(batchOpen, 0, 'utf8');
  for (let index = 0; index < events.length; index += 1) {
    if (index > 0) {
      offset += body.write(',', offset, 'utf8');
    }
    const event = events[index];
    if (event === undefined) {
      throw new Error('internal: sparse batch');
    }
    offset += body.write(event.line, offset, 'utf8');
  }
  body.write(batchClose, offset, 'utf8');

  const expected = bodyBytes - Buffer.byteLength(batchClose, 'utf8');
  if (offset !== expected) {
    // A miscount would silently truncate a line; fail loud instead of sending it.
    throw new Error(`internal: batch body length mismatch (${offset} != ${expected})`);
  }
  return body;
}

/**
 * The gzip decision: compress, and keep the result only when it is at most 90 % of the raw
 * body AND saves more than 1 KiB — below that the `Content-Encoding` hop and the extra
 * buffering are not worth it. The level is fixed (and Node's gzip carries no timestamp), so
 * the same body always gzips to the same bytes.
 *
 * A gzip body is also dropped when it would compress *too well*: the server refuses a decoded
 * body larger than `MaxExpansionRatio × compressed.Length` with a non-retryable 413
 * `batch_too_large` (a decompression-bomb guard), so a batch of highly repetitive events that
 * reaches, say, 800:1 must go out as the raw body, which is at most 1 MiB and always accepted.
 * The cap is the default server ratio minus one point of margin; the comparison mirrors the
 * server's `>` boundary, so an exactly-at-the-cap ratio still travels compressed.
 */
function decideGzip(body: Buffer): Buffer | undefined {
  const compressed = gzipSync(body, { level: 6 });
  const saving = body.length - compressed.length;
  if (compressed.length > body.length * gzipMinRatio || saving <= gzipMinSavingBytes) {
    return undefined;
  }
  const maxExpandedBytes =
    compressed.length * (gzipMinServerExpansionRatio - gzipExpansionHeadroom);
  if (body.length > maxExpandedBytes) {
    // Compressing this batch would be refused as an expansion bomb; the raw body fits.
    return undefined;
  }
  return compressed;
}
