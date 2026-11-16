// 09.1.1-T3.1 (00 §9.2, ADR-0008 decision (a) and rule 2): reduce a failed SDK call to one
// typed `ApiError`. With a 00 §9.2 envelope the table is by HTTP status with the two documented
// code splits — 403 `csrf_failed` and 409 `idempotency_conflict` — so every code 00 §9.2 lists is
// covered by its status and none falls through to a catch-all.
//
// A body that is not a 00 §9.2 envelope is never read for meaning, but the status alone can still
// be unambiguous, and then it decides the kind (401 `unauthenticated`, 403 `forbidden`, 404
// `not_found`, 413 `payload_too_large`, 429 `rate_limited`, 502/503/504 `unavailable`) with the
// code kept as `envelope_missing`. A status whose meaning depends on the body stays `unexpected`
// — 304, 500 and any other 4xx or 5xx — because guessing there would invent a UI state for
// something no Evidgate handler produced.
//
// Server messages and details remain unchanged data, never interpolated into markup or logged.
// Client-owned fallback messages are localized; no request data is copied into either path.

import { isErrorEnvelope } from '@evidgate/sdk';
import type { ErrorEnvelopeBody, ErrorEnvelopeDetail } from '@evidgate/sdk';

import type { ApiError, ApiErrorKind } from './api-error';

/** Shared empty `details`, so a mapping allocates no array. */
const NO_DETAILS: readonly ErrorEnvelopeDetail[] = [];

/** Mapper-local codes: emitted only when the server sent no usable `error.code`. */
const CODE_ENVELOPE_MISSING = 'envelope_missing';
const CODE_UNEXPECTED_STATUS = 'unexpected_status';
const CODE_NETWORK = 'network';
const CODE_TIMEOUT = 'request_timeout';

/** `delta-seconds` of RFC 9110 §10.2.3: digits only, no sign, no fraction. */
const DELTA_SECONDS = /^\d+$/;

/** Every HTTP-date form accepted below starts with a day name … */
const HTTP_DATE_START = /^[A-Za-z]{3}/;

/** … and carries a clock time (`HH:MM:SS`); an ES5 date may carry neither. */
const CLOCK_TIME = /\d{1,2}:\d{2}:\d{2}/;

/**
 * RFC 9110 §5.6.7 `GMT`: an IMF-fixdate (or an RFC 850 date, whose timezone is `GMT` too) ends
 * with it. A date with no zone, or an asctime date (`Sun Nov  6 08:49:37 1994`), is not one, so
 * `Date.parse` is only asked about a value that carries the zone explicitly.
 */
const HTTP_DATE_GMT = / GMT$/;

/**
 * Upper bound on `Retry-After`: one day. The header is untrusted server input, and a caller that
 * schedules its retry from it must never be parked for weeks by a typo (`99999999`) or by a
 * far-future date; such a value asks for "much later", which one day satisfies.
 */
const MAX_RETRY_AFTER_SECONDS = 86_400;

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

/**
 * Status → kind when the response carried no 00 §9.2 envelope. Only a status that means one
 * thing on its own is read here; 403 is `forbidden` because `csrf_failed` needs the envelope's
 * `code`, and everything else — 304, 500, any undocumented 4xx — stays `unexpected`, since a
 * status the body would have qualified is not a UI state by itself.
 */
function kindWithoutEnvelope(status: number): ApiErrorKind {
  switch (status) {
    case 401:
      return 'unauthenticated';
    case 403:
      return 'forbidden';
    case 404:
      return 'not_found';
    case 413:
      return 'payload_too_large';
    case 429:
      return 'rate_limited';
    case 502:
    case 503:
    case 504:
      return 'unavailable';
    default:
      return 'unexpected';
  }
}

/**
 * `Retry-After` in whole seconds for the two statuses 00 §9.2 documents it on (429, 503);
 * `null` everywhere else, so a stray header never extends a backoff. A real `Headers` matches
 * the name case-insensitively on its own, which is the only implementation the gateway hands in.
 */
function retryAfterSecondsFor(
  status: number,
  headers: Pick<Headers, 'get'> | null | undefined,
): number | null {
  if (status !== 429 && status !== 503) {
    return null;
  }
  return parseRetryAfter(headers?.get('retry-after') ?? null);
}

/**
 * `Retry-After` (`RFC 9110` §10.2.3) as whole seconds: delta-seconds, or an HTTP-date (IMF-fixdate
 * or RFC 850, both of which end in `GMT`) measured against `nowMs`. The result is clamped to
 * `[0, MAX_RETRY_AFTER_SECONDS]`: a date already in the past means "retry now", and anything
 * above the ceiling — a huge delta-seconds, a digit string that overflows `Number`, a
 * far-future date — asks for "much later", which one day satisfies. `null` for a missing,
 * negative, fractional or otherwise unparsable value, because a garbage header must not be read
 * as a long wait. Only a value that ends in `GMT` is parsed as a date: `Date.parse` also accepts
 * `2026-10-24`, an ISO timestamp, a zone-less or asctime date and junk such as `'1.5'` (year 1),
 * none of which is an HTTP-date.
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
    return Number.isFinite(seconds)
      ? Math.min(seconds, MAX_RETRY_AFTER_SECONDS)
      : MAX_RETRY_AFTER_SECONDS;
  }
  if (!HTTP_DATE_START.test(raw) || !CLOCK_TIME.test(raw) || !HTTP_DATE_GMT.test(raw)) {
    return null;
  }
  const atMs = Date.parse(raw);
  if (Number.isNaN(atMs)) {
    return null;
  }
  return Math.min(MAX_RETRY_AFTER_SECONDS, Math.max(0, Math.ceil((atMs - nowMs) / 1000)));
}

/**
 * A failure with no HTTP response (offline, DNS/TLS error). Status is `0`, the kind is `network`
 * and a retry is safe only because no server saw the request — a side-effecting call still needs
 * its original `Idempotency-Key` (00 §9.2). A cancellation never arrives here: the gateway
 * re-throws the `AbortError` untouched, because the caller asked for it.
 */
export function mapNetworkError(): ApiError {
  return {
    kind: 'network',
    status: 0,
    code: CODE_NETWORK,
    message: $localize`:@@core.http.error.network:The request did not reach the API; no response was received.`,
    requestId: null,
    retryable: true,
    details: NO_DETAILS,
    retryAfterSeconds: null,
  };
}

/**
 * A call the adapter's deadline stopped (09.1.1-T8) before any HTTP response arrived. Status is
 * `0` like {@link mapNetworkError}, but the two are not the same fact: a timeout says nothing
 * about whether the server saw the request, so the copy claims no outcome and a retry is only
 * safe with the original `Idempotency-Key` (00 §9.2). No interval is invented — there is no
 * `Retry-After` to echo — and a cancellation never arrives here: the gateway re-throws the
 * `AbortError` untouched, because the caller asked for it.
 */
export function mapTimeoutError(): ApiError {
  return {
    kind: 'timeout',
    status: 0,
    code: CODE_TIMEOUT,
    message: $localize`:@@core.http.error.timeout:The API did not answer within the request deadline; the request may or may not have reached the server.`,
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
    return mapNetworkError();
  }

  if (status >= 200 && status < 300) {
    return {
      kind: 'unexpected',
      status,
      code: CODE_UNEXPECTED_STATUS,
      message: $localize`:@@core.http.error.unexpected-status:A success status reached the error mapper; read it through the API gateway.`,
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
    kind: kindWithoutEnvelope(status),
    status,
    code: CODE_ENVELOPE_MISSING,
    message: $localize`:@@core.http.error.envelope-missing:The API answered ${status}:STATUS: without the 00 §9.2 error envelope.`,
    requestId: null,
    // The two 00 §9.2 backoff statuses and the transient gateway pair are worth the same-intent
    // retry even when the body carries no envelope.
    retryable: status === 429 || status === 502 || status === 503 || status === 504,
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
