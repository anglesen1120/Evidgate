// 04.2.3-T1.2: the CLI's batching half of the send path (04.2.3 §1, AC4). A spool
// line is one 00 §9.4 envelope; `POST /v1/runs/{r}/events` takes a batch
// `{"events":[...]}` capped at 500 events and 1 MiB *before* compression
// (contracts/schemas/event-envelope.v1.json, 00 §9.4, FR-04-2). This module packs
// spool lines into those bodies and derives the per-batch `Idempotency-Key`.
//
// Two invariants decide the design:
//
//  * The body is the EXACT spool line texts, concatenated — never re-serialised.
//    A resend after a lost ACK, a process restart or a re-pack therefore sends the
//    same bytes (A9/A10, AC5), so the server's body hash — and the idempotency
//    identity folded from `(sequence, event_id)` — cannot drift.
//  * Packing is streaming: `packBatches` is an async generator that holds one
//    batch of events at a time, so a 256 MiB spool never lands in memory
//    (task §8 Resource safety, AC3/AC4).
//
// Validation happens per line, before the line joins a batch, through the same
// Ajv 8 registry the server mirrors (01.3.2-T2.2 `loadContractSchemas`, rulings
// T42): an invalid envelope fails the run with the offending `sequence` named
// instead of travelling to the server and coming back as a 422. The mapped
// `details[]` rows are the 00 §9.2 `path` + `reason` pair only — never an event
// value, which may be sensitive local test data (task §8 S3 analogue).
//
// Before the schema verdict, each line is also checked against the directory it was
// read from (task §8 SC5): its `producer_id` must be that producer directory, and a
// `run_id` it declares must be the run being sent. A spool is a made-to-be-tampered
// input (any local process may write it), so a planted line stops the run instead of
// being sent under another producer's batch key.
import { createHash } from 'node:crypto';
import { gzipSync } from 'node:zlib';

import { escapeControlCharacters } from './errors.js';
import { loadPackagedContractSchemas } from './packagedSchemas.js';
import { invalidJsonReason, loadContractSchemas, type ContractSchemaSet } from './validate.js';

import type { SpoolEvent } from './spool.js';

/** Contract cap on the number of events in one batch (00 §9.3/§9.4, FR-04-2). */
export const maxBatchEvents = 500;
/** Contract cap on the batch body before compression: 1 MiB (event-envelope.v1 `x-max-bytes`). */
export const maxBatchBytes = 1_048_576;

/** The batch body opener: `{"events":[` — 11 UTF-8 bytes. */
const batchOpen = '{"events":[';
/** The batch body closer: `]}` — 2 UTF-8 bytes. */
const batchClose = ']}';
/** One byte between two concatenated lines. */
const separatorBytes = 1;
/** Bytes a batch body spends on everything that is not an event line. */
const overheadBytes = Buffer.byteLength(batchOpen, 'utf8') + Buffer.byteLength(batchClose, 'utf8');

/** Gzip must at least reach this fraction of the raw body (a 10 % saving). */
const gzipMinRatio = 0.9;
/** …and save more than this many bytes, or the `Content-Encoding` hop is not worth it. */
const gzipMinSavingBytes = 1024;
/**
 * The lowest `EVIDGATE__INGESTION__MAXEXPANSIONRATIO` this client can count on. The
 * server refuses a `gzip` body once its decoded size passes `ratio × compressed.Length`
 * (`IngestionLimits.MaxExpansionRatio`, default 20; `EventBodyDecoder.cs` `next > ratioCap`),
 * as a non-retryable 413 `batch_too_large`. A batch that compresses better than that cap
 * must therefore travel raw, which it always can: the raw body is at most 1 MiB.
 */
const gzipMinServerExpansionRatio = 20;
/** One ratio point of margin, so the decoded size stays *under* the server's comparison. */
const gzipExpansionHeadroom = 1;

/** Version tag of the idempotency-key pre-image; changing the recipe changes the tag. */
const batchKeyVersion = 'evidgate.batch-key.v1';
/** Every derived key is `batch:` + 64 hex characters — 70, well inside the 128-char route limit. */
const batchKeyPrefix = 'batch:';

/** The contract schema this module validates against (`contracts/schemas/event-envelope.v1.json`). */
const envelopeSchemaKey = 'event-envelope.v1';
/** The batch member an event line must not carry: a line IS one envelope (00 §9.4). */
const eventsMember = 'events';
/** Prefix the wrapped (single-item batch) validation puts in front of item error paths. */
const wrappedItemPrefix = '/events/0';

/** One `details[]` row of the 00 §9.2 rejection, minus the batch `index` (one line, one event). */
export interface EnvelopeDetail {
  /** Absolute JSON pointer into the event, e.g. `/event_id`; `""` names the event itself. */
  readonly path: string;
  /** The failed constraint as a lower_snake token (`required`, `format`, `additional_properties`, …). */
  readonly reason: string;
}

/**
 * One unit of work for the sender: the events of a batch, the exact body bytes to
 * POST and the derived `Idempotency-Key` (04.2.3 contract §batcher.ts).
 *
 * `body` is the raw `{"events":[...]}` JSON, at most {@link maxBatchBytes} bytes;
 * `gzipBody` is present only when compressing actually helps (AC4) and the sender
 * decides which representation to transmit.
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

/**
 * A single event cannot fit a batch body even on its own: `{"events":[<line>]}`
 * exceeds the 1 MiB pre-compression cap (AC4). Thrown from {@link packBatches}
 * before anything is sent, naming the event by `event_id` and `sequence` so the
 * operator can find the offending case.
 */
export class OversizedEventError extends Error {
  readonly eventId: string;
  readonly sequence: number;
  /** The bytes that single-event batch body would occupy (`{"events":[<line>]}`, pre-compression). */
  readonly bytes: number;

  constructor(eventId: string, sequence: number, bytes: number, maxBytes: number) {
    super(
      `event ${eventId} (sequence ${sequence}) needs a ${bytes}-byte batch body on its own; ` +
        `the limit is ${maxBytes} bytes before compression`,
    );
    this.name = 'OversizedEventError';
    this.eventId = eventId;
    this.sequence = sequence;
    this.bytes = bytes;
  }
}

/**
 * A spool line is not a valid 00 §9.4 envelope (or is not a single envelope at
 * all): the run stops at this `sequence` instead of sending a body the server
 * would reject with a 422. `details` carries the 00 §9.2 rows — `path` + `reason`
 * only, so no event value ever reaches the operator's terminal or log
 * (task §8 S3 analogue: the spool may hold test names).
 */
export class EnvelopeInvalidError extends Error {
  /** `event_id` of the offending line, when the line parsed and carried a printable one. */
  readonly eventId?: string;
  /** The spool `sequence` the invalid line declares. */
  readonly sequence: number;
  /** The mapped 00 §9.2 rows, deduplicated and capped by the shared validator. */
  readonly details: readonly EnvelopeDetail[];
  /**
   * Optional explanation appended after the rows, for a rejection the schema cannot
   * express (a line whose `producer_id`/`run_id` is not the directory/run it was read
   * from). Only trusted values — the run id and the producer directory, never the
   * line's own text — may appear in it.
   */
  readonly note?: string;

  constructor(o: {
    sequence: number;
    eventId?: string;
    details: readonly EnvelopeDetail[];
    note?: string;
  }) {
    super(
      `event at sequence ${o.sequence}${o.eventId === undefined ? '' : ` (${o.eventId})`} ` +
        `is not a valid event envelope: ${summariseDetails(o.details)}` +
        (o.note === undefined ? '' : ` (${o.note})`),
    );
    this.name = 'EnvelopeInvalidError';
    this.sequence = o.sequence;
    if (o.eventId !== undefined) {
      this.eventId = o.eventId;
    }
    if (o.note !== undefined) {
      this.note = o.note;
    }
    this.details = o.details;
  }
}

/** At most this many `details[]` rows are echoed in the message; the rest is counted. */
const maxSummaryRows = 5;
/**
 * Longest `details[].path` echoed in the message, in Unicode code points. Ajv paths
 * carry member names straight from the spool line, so a hostile 1 MiB member name (or
 * a secret-shaped one) must not flood stderr: the same 200-code-point cap
 * `serverError.ts` applies to server-supplied text (SEC4 analogue).
 */
const maxEchoedPathLength = 200;

/**
 * Caps one echoed detail path to {@link maxEchoedPathLength} code points, with an
 * ellipsis when cut, then control-char escapes it — the order `serverError.ts` uses,
 * so a `\uXXXX` escape is never cut in half and a surrogate pair is never split.
 */
function capEchoedPath(path: string): string {
  const points = Array.from(path);
  const capped =
    points.length <= maxEchoedPathLength
      ? path
      : `${points.slice(0, maxEchoedPathLength).join('')}…`;
  return escapeControlCharacters(capped);
}

/** `path=reason` for the first rows, then `(+N more)` — paths are member names, never values. */
function summariseDetails(details: readonly EnvelopeDetail[]): string {
  if (details.length === 0) {
    return 'no details';
  }

  const rows = details
    .slice(0, maxSummaryRows)
    .map(
      (detail) => `${detail.path === '' ? '<event>' : capEchoedPath(detail.path)} ${detail.reason}`,
    );
  if (details.length > maxSummaryRows) {
    rows.push(`(+${details.length - maxSummaryRows} more)`);
  }
  return rows.join(', ');
}

/**
 * The per-batch `Idempotency-Key` (04.2.3 contract §batcher.ts, 00 §7): `batch:` +
 * the lowercase hex SHA-256 of the UTF-8 JSON array
 *
 * ```text
 * ["evidgate.batch-key.v1", run_id, producer_id, [[sequence, event_id], …]]
 * ```
 *
 * A JSON array is unambiguous (no separator can collide with a value) and the
 * member order is fixed, so the same batch derives the same key in a fresh
 * process after a crash — a restart re-sends the identical `Idempotency-Key` and
 * the server de-duplicates instead of storing twice (AC2/AC5, A9). Any change of
 * `run_id`, `producer_id`, or of one `(sequence, event_id)` pair — including
 * reordering — derives a different key, and an empty batch's key is never reused
 * for a non-empty one. The key feeds no secret (ids only) and is 70 characters.
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

/** Options of {@link packBatches}. */
export interface PackBatchesOptions {
  /** The run every event belongs to; part of the idempotency pre-image. */
  readonly runId: string;
  /** The producer directory the lines came from; part of the idempotency pre-image. */
  readonly producerId: string;
  /**
   * Per-line validation, called once per event in spool order. Obtain it from
   * {@link createEnvelopeValidator}; it throws {@link EnvelopeInvalidError}.
   */
  readonly validate: (line: string, sequence: number) => void;
  /** Override of {@link maxBatchEvents} (tests, or a narrower server cap). */
  readonly maxEvents?: number;
  /** Override of {@link maxBatchBytes}. */
  readonly maxBytes?: number;
}

/** The 00 §9.4 member naming the producer a line belongs to (required by the schema). */
const producerIdMember = 'producer_id';
/** The member that ties a line to one run; the schema does not require it (yet). */
const runIdMember = 'run_id';

/**
 * The identity rejection of one spool line, when the line contradicts the directory it
 * was read from (security review SC5, 04.2.3 §8): `producer_id` must be the producer
 * directory the line sits in, and a line that *declares* a `run_id` must declare the run
 * being sent. A line planted in `<run>/A/` that claims producer B would otherwise travel
 * under the batch key derived from A (and be stored under the wrong producer once the
 * server trusted the local framing), so the run stops here instead.
 *
 * Members that are absent (or not strings) are left to the schema: `producer_id` is
 * `required`, and any `run_id` at all is an `additionalProperties` rejection — this check
 * only adds the mismatch the schema cannot see, and it never echoes the line's own value.
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
 * Packs spool events into batches (AC4): at most `maxEvents` events and at most
 * `maxBytes` body bytes pre-compression, counting the opener, one separator per
 * gap and the closer. The generator pulls from `events` only as far as the batch
 * being built needs — a full batch is yielded before the next event is pulled, so
 * at most one batch of events (plus the single candidate event in hand) is held
 * in memory — and calls `o.validate` per line before the line joins a batch.
 *
 * Order of the per-event checks: the transport cap first (an event that cannot
 * travel fails with its `event_id` even when it is also malformed), then the
 * line↔directory identity (see {@link identityMismatchOf}, which needs one
 * `JSON.parse` per line — the same parse `validate` does, on the same text), then
 * schema validation. A later batch is never produced once a line throws: the run
 * stops at that sequence (task §6 failure path).
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
 * The gzip decision (AC4): compress, and keep the result only when it is at most
 * 90 % of the raw body AND saves more than 1 KiB — below that the
 * `Content-Encoding` hop and the extra buffering are not worth it. The level is
 * fixed (and Node's gzip carries no timestamp), so the same body always gzips to
 * the same bytes.
 *
 * A gzip body is also dropped when it would compress *too well*: the server refuses
 * a decoded body larger than `MaxExpansionRatio × compressed.Length` with a
 * non-retryable 413 `batch_too_large` (a decompression-bomb guard), so a batch of
 * highly repetitive events that reaches, say, 800:1 must go out as the raw body,
 * which is at most 1 MiB and always accepted. The cap is the default server ratio
 * minus one point of margin; the comparison mirrors the server's `>` boundary, so an
 * exactly-at-the-cap ratio still travels compressed.
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

/**
 * Builds the pre-send validator for one spool line (04.2.3 contract §batcher.ts,
 * ruling T42): Ajv 8 through the shared `loadContractSchemas` registry — the same
 * compiled schemas, error mapping and `details[]` rows as the server — so a line
 * that would be a 422 never leaves the machine.
 *
 * The line is validated as a one-item batch (`{"events":[<line>]}`) rather than
 * through the registry's batch/no-batch dispatch, because a single-event object
 * takes the item branch there while a *batch-shaped* line would be accepted as a
 * body of its own. Wrapping keeps "one line = one envelope" exact: a line that
 * carries an `events` member is an unexpected member of the event object
 * (`additional_properties` at `/events`), never a nested batch. The
 * `/events/0` prefix the registry puts on item rows is stripped, so `details[]`
 * paths point into the event itself and equal the corpus sidecars
 * (`fixtures/contracts/vectors/event-envelope.v1`). The 1 MiB cap is not part of
 * this verdict — it belongs to the body that travels; see
 * {@link validateEnvelopeLine}.
 *
 * The schemas are the ones compiled into the package (`loadPackagedContractSchemas`), so a
 * command validates from an install with no repository above it — there is no directory
 * search and no current-working-directory dependency. Pass a directory only to validate
 * against another copy of `contracts/`. Both the schema load and the Ajv compile happen
 * once, when the validator is created.
 */
export function createEnvelopeValidator(
  contractsDirectory?: string,
): (line: string, sequence: number) => void {
  const schemas =
    contractsDirectory === undefined
      ? loadPackagedContractSchemas()
      : loadContractSchemas(contractsDirectory);
  return (line, sequence) => {
    const details = validateEnvelopeLine(schemas, line);
    if (details.length === 0) {
      return;
    }
    const eventId = eventIdOf(line);
    throw new EnvelopeInvalidError({
      sequence,
      ...(eventId === undefined ? {} : { eventId }),
      details,
    });
  };
}

/**
 * The mapped `details[]` rows of one line, with the wrapped-batch prefix stripped.
 *
 * The line is validated as a one-item batch (`{"events":[<line>]}`) through the
 * registry's parsed-document path rather than its batch/no-batch dispatch,
 * because a single-event object takes the item branch there while a *batch-shaped*
 * line would be accepted as a body of its own. Wrapping keeps "one line = one
 * envelope" exact: a line that carries an `events` member is an unexpected member
 * of the event object (`additional_properties` at `/events`), never a nested
 * batch.
 *
 * The 1 MiB transport cap is deliberately NOT part of this verdict: it is a
 * property of the body that travels, which `packBatches` measures on the exact
 * bytes before it validates (an oversized line is an {@link OversizedEventError},
 * never an envelope rejection). The corpus keeps both halves as separate vectors
 * — `acc-05-max-bytes-exact` (a 1 MiB event, schema-valid) and
 * `rej-16-over-max-bytes` (`body_too_large`, shape-valid) — so applying the cap
 * here would make the validator disagree with one of them.
 */
function validateEnvelopeLine(schemas: ContractSchemaSet, line: string): EnvelopeDetail[] {
  let document: unknown;
  try {
    document = JSON.parse(line);
  } catch {
    // The registry's own token for an unparseable document (00 §9.3 `invalid_json`).
    return [{ path: '', reason: invalidJsonReason }];
  }

  const result = schemas.validateParsed(envelopeSchemaKey, { [eventsMember]: [document] });
  return result.errors.map((error) => ({
    path:
      error.path === wrappedItemPrefix
        ? ''
        : error.path.startsWith(`${wrappedItemPrefix}/`)
          ? error.path.slice(wrappedItemPrefix.length)
          : error.path,
    reason: error.reason,
  }));
}

/** Longest `event_id` echoed in an error message (the schema's `maxLength`). */
const maxEchoedEventIdLength = 128;
/** Printable ASCII only: an id that is not one stays out of the message and the log. */
const printableAsciiPattern = /^[\x20-\x7e]+$/;

/**
 * The `event_id` an invalid line declares, when it is safe to echo: the line must
 * parse, the member must be a string of at most 128 printable ASCII characters.
 * Anything else (a truncated line, an id carrying control characters or a
 * would-be secret) is dropped — the sequence still names the event.
 */
function eventIdOf(line: string): string | undefined {
  let document: unknown;
  try {
    document = JSON.parse(line);
  } catch {
    return undefined;
  }
  if (typeof document !== 'object' || document === null || Array.isArray(document)) {
    return undefined;
  }
  const value = (document as Record<string, unknown>)['event_id'];
  if (
    typeof value !== 'string' ||
    value.length === 0 ||
    value.length > maxEchoedEventIdLength ||
    !printableAsciiPattern.test(value)
  ) {
    return undefined;
  }
  return value;
}
