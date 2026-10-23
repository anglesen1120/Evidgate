// 01.3.2-T1.2: the 00 §9.2 error envelope as consumed by callers of the SDK.
//
// `ErrorEnvelope` itself is generated from the spec (`../gen/types.gen.js` — the schema
// is the source of truth, never hand-mirrored). This module only names the two anonymous
// member shapes so interceptors and UI error mapping (00 §5.6 W5) can write signatures
// against them, and pins the shape with a structural guard.

import type { ErrorEnvelope } from '../gen/index.js';

export type { ErrorEnvelope };

/** One entry of `error.details` — `{ index?, path?, reason }` (00 §9.2; `reason` required). */
export type ErrorEnvelopeDetail = NonNullable<ErrorEnvelope['error']['details']>[number];

/** The `error` member of `ErrorEnvelope`. */
export type ErrorEnvelopeBody = ErrorEnvelope['error'];

/**
 * Structural guard for the 00 §9.2 envelope. Used by callers that receive the wire body
 * (e.g. `RequestResult.error` is the raw response body) and need to narrow before mapping
 * it to a typed UI/CLI error.
 */
export function isErrorEnvelope(value: unknown): value is ErrorEnvelope {
  if (typeof value !== 'object' || value === null) {
    return false;
  }
  const error = (value as Record<string, unknown>)['error'];
  if (typeof error !== 'object' || error === null) {
    return false;
  }
  const body = error as Record<string, unknown>;
  if (
    typeof body['code'] !== 'string' ||
    typeof body['message'] !== 'string' ||
    typeof body['request_id'] !== 'string' ||
    typeof body['retryable'] !== 'boolean'
  ) {
    return false;
  }
  const details = body['details'];
  return (
    details === undefined ||
    (Array.isArray(details) &&
      details.every(
        (d) =>
          typeof d === 'object' &&
          d !== null &&
          typeof (d as Record<string, unknown>)['reason'] === 'string',
      ))
  );
}
