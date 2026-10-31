// Ack verification: nothing advances on doubt. A 200 counts only when every event has its own
// ack, in request order, naming the same `event_id`, with `accepted`/`duplicate` counts that
// add up — otherwise the cursor stays where it was and the failure names the request id.
import { escapeControlCharacters } from './errors.js';
import { redact } from './redact.js';

import { ackMismatchCode, maxRequestIdLength } from './senderLimits.js';
import { failure } from './senderFailure.js';

import type { Batch } from './batchPack.js';
import type { BatchAckSummary } from './senderTypes.js';
import type { SendFailedError } from './senderFailure.js';

/**
 * Verifies a 200 body before anything advances ("never advance on doubt"): one ack
 * per event, in request order, for the same `event_id`, with status `accepted` or
 * `duplicate`, and with the body's `accepted`/`duplicates` counts matching the acks.
 * `duplicate` counts as delivered (it is a resend of a batch whose ACK was lost).
 */
export function verifyAcks(batch: Batch, data: unknown, response: Response): BatchAckSummary {
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
export function ackMismatch(batch: Batch, response: Response, reason: string): SendFailedError {
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
