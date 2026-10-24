// 09.1.1-T3.2 (00 §5.6 W5, §9.1; ADR-0008 rules 1 and 3): every API request carries a
// fresh `X-Request-Id` and a W3C `traceparent`; anything outside same-origin `/v1` passes
// through untouched.
import { DOCUMENT } from '@angular/common';
import type { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';

import { isApiRequestUrl } from './api-origin';
import { newUuid } from './random-id';

const TRACE_VERSION = '00';
const TRACE_FLAGS = '01';
const TRACE_ID_BYTES = 16;
const SPAN_ID_BYTES = 8;

/** Lowercase hex for `byteLength` random bytes, regenerated while every byte is zero. */
function randomNonZeroHex(byteLength: number): string {
  const bytes = new Uint8Array(byteLength);
  for (;;) {
    crypto.getRandomValues(bytes);
    if (bytes.some((byte) => byte !== 0)) {
      return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('');
    }
  }
}

/** W3C trace context: `00-<32 hex trace id>-<16 hex span id>-01`, never all-zero ids. */
export function createTraceparent(): string {
  const traceId = randomNonZeroHex(TRACE_ID_BYTES);
  const spanId = randomNonZeroHex(SPAN_ID_BYTES);
  return `${TRACE_VERSION}-${traceId}-${spanId}-${TRACE_FLAGS}`;
}

/**
 * Adds `X-Request-Id` (one UUID per outgoing request) and `traceparent` to API requests
 * only, never overwriting a header the caller already set (names compare case-insensitively
 * through `HttpHeaders`).
 */
export const requestIdInterceptor: HttpInterceptorFn = (request, next) => {
  const pageHref = inject(DOCUMENT).location.href;
  if (!isApiRequestUrl(request.url, pageHref)) {
    return next(request);
  }

  let headers = request.headers;
  if (!headers.has('X-Request-Id')) {
    // `newUuid()` rather than `crypto.randomUUID()`: the latter is missing on a non-secure
    // context (plain http outside localhost) and would throw before the request leaves.
    headers = headers.set('X-Request-Id', newUuid());
  }
  if (!headers.has('traceparent')) {
    headers = headers.set('traceparent', createTraceparent());
  }
  return headers === request.headers ? next(request) : next(request.clone({ headers }));
};
