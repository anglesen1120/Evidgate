// The one failure a send raises and the three failures this client invents itself (a deadline,
// a batch stopped by a sibling, an untrustworthy 200). All of them carry the batch's sequence
// range so the operator knows what did not advance the cursor.
import { deadlineExceededCode, sendStoppedCode } from './senderLimits.js';

import type { Batch } from './batchPack.js';
import type { SendFailedErrorOptions } from './senderTypes.js';

/**
 * The one failure a send raises. `code`, `requestId` and `status` are the server's values
 * verbatim, so `events send` prints `evidgate: error producer_slot_taken` plus the request
 * id without re-interpreting anything; the batch's sequence range names what did not
 * advance the cursor.
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

/** Builds a `SendFailedError` carrying the batch's range and only defined extras. */
export function failure(
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

/**
 * The deadline failure that names the last error (its code, HTTP status and request id) —
 * never the request, the URL or the bearer.
 */
export function deadlineExceeded(batch: Batch, last: SendFailedError | undefined): SendFailedError {
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

/**
 * The batch that stops because the send already failed elsewhere. `sendBatches` reports
 * the first fatal error, so this one is never the error the caller sees; it exists so a
 * stopped batch is never mistaken for a delivered one.
 */
export function stoppedElsewhere(batch: Batch): SendFailedError {
  return failure(batch, {
    code: sendStoppedCode,
    message:
      `the send stopped before batch ${batch.firstSequence}-${batch.lastSequence} ` +
      'was acknowledged',
    requestId: undefined,
    status: undefined,
  });
}
