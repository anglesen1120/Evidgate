// The two rejections the batching half raises before anything is sent, and the message
// helpers that keep them safe to print: a spool line is made-to-be-tampered local input, so
// only `path` + `reason` rows and a schema-shaped `event_id` ever reach the operator.
import { escapeControlCharacters } from './errors.js';

import {
  maxEchoedEventIdLength,
  maxEchoedPathLength,
  maxSummaryRows,
  printableAsciiPattern,
} from './batchLimits.js';

/** One `details[]` row of the 00 §9.2 rejection, minus the batch `index` (one line, one event). */
export interface EnvelopeDetail {
  /** Absolute JSON pointer into the event, e.g. `/event_id`; `""` names the event itself. */
  readonly path: string;
  /** The failed constraint as a lower_snake token (`required`, `format`, `additional_properties`, …). */
  readonly reason: string;
}

/**
 * A single event cannot fit a batch body even on its own: `{"events":[<line>]}` exceeds the
 * 1 MiB pre-compression cap. Thrown from {@link packBatches} before anything is sent, naming
 * the event by `event_id` and `sequence` so the operator can find the offending case.
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
export function summariseDetails(details: readonly EnvelopeDetail[]): string {
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
 * The `event_id` an invalid line declares, when it is safe to echo: the line must
 * parse, the member must be a string of at most 128 printable ASCII characters.
 * Anything else (a truncated line, an id carrying control characters or a
 * would-be secret) is dropped — the sequence still names the event.
 */
export function eventIdOf(line: string): string | undefined {
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
