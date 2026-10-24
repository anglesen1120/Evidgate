// 09.1.1-T3.1 (00 §9.2, ADR-0008 decision (a) and rule 2): reduce a failed SDK call to one
// typed `ApiError`. The table is by HTTP status with the two documented code splits — 403
// `csrf_failed` and 409 `idempotency_conflict` — so every code 00 §9.2 lists is covered by its
// status and none falls through to a catch-all. A body that is not a 00 §9.2 envelope is never
// read for meaning: it becomes `unexpected`/`envelope_missing` (a proxy's HTML 502), because
// guessing a status meaning there would invent a UI state for something no Evidgate handler
// produced.
//
// Messages and details are server text; they are returned as data and never interpolated into
// markup or logged here. Nothing from the request (body, query, header value) is copied in.

import { isErrorEnvelope } from '@evidgate/sdk';
import type { ErrorEnvelopeBody, ErrorEnvelopeDetail } from '@evidgate/sdk';

import type { ApiError, ApiErrorKind } from './api-error';

/** Shared empty `details`, so a mapping allocates no array. */
const NO_DETAILS: readonly ErrorEnvelopeDetail[] = [];

/** Mapper-local codes: emitted only when the server sent no usable `error.code`. */
const CODE_ENVELOPE_MISSING = 'envelope_missing';
const CODE_UNEXPECTED_STATUS = 'unexpected_status';
const CODE_NETWORK = 'network';

/** `delta-seconds` of RFC 9110 §10.2.3: digits only, no sign, no fraction. */
const DELTA_SECONDS = /^\d+$/;

/** Every HTTP-date form (IMF-fixdate, RFC 850, asctime) starts with a day name … */
const HTTP_DATE_START = /^[A-Za-z]{3}/;

/** … and carries a clock time (`HH:MM:SS`); an ES5 date may carry neither. */
const CLOCK_TIME = /\d{1,2}:\d{2}:\d{2}/;

/**
 * Status → kind. Every status 00 §9.2 documents has its own arm; `code` only breaks the two
 * ties the spec defines (403 `csrf_failed`, 409 `idempotency_conflict`). Any other status is
 * `unexpected` — including 5xx that is not `unavailable`, because the UI has no state for it.
 */
function kindForStatus(status: number, code: string): ApiErrorKind {
  switch (status) {
    case 401:
      return 'unauthenticated';
    case 403:
      return code === 'csrf_failed' ? 'csrf_failed' : 'forbidden';
    case 404:
      return 'not_found';
    case 409:
      return code === 'idempotency_conflict' ? 'idempotency_conflict' : 'conflict';
    case 412:
      return 'precondition_failed';
    case 413:
      return 'payload_too_large';
    case 422:
      return 'validation';
    case 429:
      return 'rate_limited';
    case 503:
      return 'unavailable';
    default:
      return 'unexpected';
  }
}

/** Gateway statuses worth a same-intent retry even when the body carries no envelope. */
function retryableWithoutEnvelope(status: number): boolean {
  return status === 502 || status === 503 || status === 504;
}

/**
 * Raw header read that stays correct for a case-sensitive stand-in object as well as for a
 * real `Headers` (which is already case-insensitive).
 */
function readHeader(headers: Pick<Headers, 'get'> | null | undefined, name: string): string | null {
  if (!headers) {
    return null;
  }
  const direct = headers.get(name);
  if (direct !== null) {
    return direct;
  }
  const lower = name.toLowerCase();
  return lower === name ? null : headers.get(lower);
}

/**
 * `Retry-After` in whole seconds for the two statuses 00 §9.2 documents it on (429, 503);
 * `null` everywhere else, so a stray header never extends a backoff.
 */
function retryAfterSecondsFor(
  status: number,
  headers: Pick<Headers, 'get'> | null | undefined,
): number | null {
  if (status !== 429 && status !== 503) {
    return null;
  }
  return parseRetryAfter(readHeader(headers, 'Retry-After'));
}

/**
 * `Retry-After` (`RFC 9110` §10.2.3) as whole seconds: delta-seconds, or an HTTP-date measured
 * against `nowMs`. Clamped at 0 (a date in the past means "retry now"); `null` for a missing,
 * negative, fractional or otherwise unparsable value, because a garbage header must not be read
 * as a long wait. Only an HTTP-date — a day name followed by a clock time — is parsed as a
 * date: `Date.parse` also accepts `2026-10-24`, an ISO timestamp and junk such as `'1.5'`
 * (year 1), none of which is an HTTP-date.
 */
export function parseRetryAfter(
  value: string | null | undefined,
  nowMs: number = Date.now(),
): number | null {
  const raw = value?.trim();
  if (raw === undefined || raw === '') {
    return null;
  }
  if (DELTA_SECONDS.test(raw)) {
    const seconds = Number(raw);
    return Number.isSafeInteger(seconds) ? seconds : null;
  }
  if (!HTTP_DATE_START.test(raw) || !CLOCK_TIME.test(raw)) {
    return null;
  }
  const atMs = Date.parse(raw);
  if (Number.isNaN(atMs)) {
    return null;
  }
  return Math.max(0, Math.ceil((atMs - nowMs) / 1000));
}

/** True for an abort ({@link AbortSignal}); the cause is inspected, never copied into text. */
function isAbortCause(cause: unknown): boolean {
  return (
    typeof cause === 'object' &&
    cause !== null &&
    'name' in cause &&
    typeof cause.name === 'string' &&
    cause.name === 'AbortError'
  );
}

/**
 * A failure with no HTTP response (offline, DNS/TLS error, abort). Status is `0`, the kind is
 * `network` and a retry is safe only because no server saw the request — a side-effecting call
 * still needs its original `Idempotency-Key` (00 §9.2).
 */
export function mapNetworkError(cause: unknown): ApiError {
  return {
    kind: 'network',
    status: 0,
    code: CODE_NETWORK,
    message: isAbortCause(cause)
      ? 'The request was cancelled before the API answered.'
      : 'The request did not reach the API; no response was received.',
    requestId: null,
    retryable: true,
    details: NO_DETAILS,
    retryAfterSeconds: null,
  };
}

/**
 * Maps one failed response to an {@link ApiError}. `status` is the HTTP status (`0` when the
 * request never got one), `body` the SDK's raw `error` value, `headers` the response headers.
 *
 * A 2xx here is a programming error — success responses are read through the gateway, not
 * mapped — so it returns `unexpected_status` instead of an arbitrary kind.
 */
export function mapApiError(input: {
  status: number;
  body: unknown;
  headers?: Pick<Headers, 'get'> | null;
}): ApiError {
  const { status, body, headers } = input;

  if (status <= 0) {
    return mapNetworkError(null);
  }

  if (status >= 200 && status < 300) {
    return {
      kind: 'unexpected',
      status,
      code: CODE_UNEXPECTED_STATUS,
      message: 'A success status reached the error mapper; read it through the API gateway.',
      requestId: null,
      retryable: false,
      details: NO_DETAILS,
      retryAfterSeconds: null,
    };
  }

  if (isErrorEnvelope(body)) {
    return fromEnvelope(status, body.error, headers);
  }

  return {
    kind: 'unexpected',
    status,
    code: CODE_ENVELOPE_MISSING,
    message: `The API answered ${status} without the 00 §9.2 error envelope.`,
    requestId: null,
    retryable: retryableWithoutEnvelope(status),
    details: NO_DETAILS,
    retryAfterSeconds: retryAfterSecondsFor(status, headers),
  };
}

/** The envelope's own fields win; the status only picks the kind. */
function fromEnvelope(
  status: number,
  body: ErrorEnvelopeBody,
  headers: Pick<Headers, 'get'> | null | undefined,
): ApiError {
  return {
    kind: kindForStatus(status, body.code),
    status,
    code: body.code,
    message: body.message,
    requestId: body.request_id,
    retryable: body.retryable,
    details: body.details ?? NO_DETAILS,
    retryAfterSeconds: retryAfterSecondsFor(status, headers),
  };
}
