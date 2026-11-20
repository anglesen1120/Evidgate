// Primitives both request paths of the fetch adapter share: the call deadline, the abort and
// timeout shapes, the body-read race, the failure classification and the `Response` conversion.
// They live here so the text and binary paths cannot grow two copies of the same cancellation and
// header rules; neither path may add a private copy of these.

import { HttpErrorResponse, type HttpHeaders, type HttpResponseBase } from '@angular/common/http';
import {
  EmptyError,
  firstValueFrom,
  from,
  fromEvent,
  NEVER,
  type Observable,
  takeUntil,
} from 'rxjs';

import { BINARY_TRANSPORT_HEADER } from './binary-transport';

/** One call's deadline: a promise that never resolves and rejects once, plus its timer. */
export interface Deadline {
  /** Rejects with the deadline's `TimeoutError` when the deadline passes; never resolves. */
  readonly expiration: Promise<never>;
  /** Stops the timer, so a finished call leaves nothing scheduled behind. */
  readonly cancel: () => void;
}

/** Starts the call's deadline; `null` when the caller disabled it. */
export function createDeadline(timeoutMs: number): Deadline | null {
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) {
    return null;
  }
  // `Promise.withResolvers` is ES2024; this project compiles against the ES2022 lib, so the
  // executor form is the only way to get the reject handle out of the promise.
  let rejectDeadline: (reason: unknown) => void = () => undefined;
  const expiration = new Promise<never>((_resolve, reject) => {
    rejectDeadline = reject;
  });
  // The executor above runs synchronously, so the timer is armed with the reject handle already
  // stored.
  const timer = setTimeout(() => rejectDeadline(timeoutError()), timeoutMs);
  // The deadline is raced against two paths (the body read and the request); whichever path
  // loses the race leaves this promise unobserved, and its rejection must not surface as an
  // unhandled rejection. The consumers still see every rejection.
  void expiration.catch(() => undefined);
  return {
    expiration,
    cancel: () => clearTimeout(timer),
  };
}

// One abort reason for the three abort paths (already aborted, aborted while reading the body,
// aborted in flight): `fetch` rejects with a `DOMException` named `AbortError`.
export function abortError(): DOMException {
  return new DOMException('The request was aborted.', 'AbortError');
}

// The deadline's rejection, in the shape `AbortSignal.timeout` produces.
function timeoutError(): DOMException {
  return new DOMException('The request timed out.', 'TimeoutError');
}

// Duck-typed like `isAbortError` in the gateway: the class is per-realm and the value may be
// this realm's `DOMException` or a foreign one.
function isTimeoutError(value: unknown): boolean {
  return (
    typeof value === 'object' && value !== null && 'name' in value && value.name === 'TimeoutError'
  );
}

/**
 * Reads the request body under the call's deadline and the caller's `signal`. A body that never
 * finishes arriving (a stalled upload, a caller-owned stream) must not hold the call open, so
 * three outcomes race: the body, the deadline's `TimeoutError` and the caller's abort. An abort
 * wins over a deadline that fires in the same turn, and it is the only ending left when the
 * deadline is disabled. The listener never outlives the read.
 *
 * Losing the race leaves `read` running, so `cancel` releases whatever it is holding (a stream
 * reader, a socket) — otherwise a cancelled call keeps consuming the producer.
 */
export async function readUnderCall<T>(
  read: () => Promise<T>,
  signal: AbortSignal,
  deadline: Deadline | null,
  cancel?: () => void,
): Promise<T> {
  // A listener added to a signal that has already fired never runs, so an abort from before the
  // read is reported here; without it a stalled body would wait for the deadline or forever.
  if (signal.aborted) {
    throw abortError();
  }
  // `Promise.withResolvers` is ES2024, so the executor form is how the reject handle escapes.
  let rejectAbort: (reason: unknown) => void = () => undefined;
  const aborted = new Promise<never>((_resolve, reject) => {
    rejectAbort = reject;
  });
  const onAbort = () => rejectAbort(abortError());
  signal.addEventListener('abort', onAbort, { once: true });
  try {
    const body = read();
    return await (deadline === null
      ? Promise.race([body, aborted])
      : Promise.race([body, aborted, deadline.expiration]));
  } catch (error: unknown) {
    // A read that ends because of a cancellation can reject with the stream's own error, so the
    // signal is the authority on whether this was one - and an abort also wins over a deadline
    // that fired at the same moment.
    if (signal.aborted || isTimeoutError(error)) {
      cancel?.();
    }
    if (signal.aborted) {
      throw abortError();
    }
    throw error;
  } finally {
    signal.removeEventListener('abort', onAbort);
  }
}

/**
 * Awaits one `HttpClient` call under the caller's abort and the call's deadline. An abort
 * completes the source through `takeUntil`, which unsubscribes (and so cancels) the request and
 * leaves no value, so the promise rejects with `EmptyError` — the abort path the caller
 * translates. The deadline does the same by erroring the stream with its `TimeoutError`.
 */
export function settleCall<T>(
  source: Observable<T>,
  signal: AbortSignal,
  deadline: Deadline | null,
): Promise<T> {
  return firstValueFrom(
    source.pipe(
      takeUntil(fromEvent(signal, 'abort')),
      takeUntil(deadline === null ? NEVER : from(deadline.expiration)),
    ),
  );
}

/**
 * Maps one rejected `HttpClient` call onto the adapter's contract. An abort or the deadline is
 * re-thrown as its `DOMException` (an abort wins when both arrive in the same turn); a transport
 * failure and a 2xx reported as an error become the `TypeError` shape `fetch` uses, so the
 * gateway maps them to `network` instead of an error envelope or a success value. Any remaining
 * failure status is returned for the caller to turn into a `Response`.
 */
export function errorResponseOrThrow(error: unknown, signal: AbortSignal): HttpErrorResponse {
  if (signal.aborted && (error instanceof EmptyError || isTimeoutError(error))) {
    throw abortError();
  }
  if (isTimeoutError(error)) {
    throw error;
  }
  if (error instanceof EmptyError) {
    // The event stream completed without a response and nobody aborted: nothing reached the
    // caller, which is the transport-failure shape the gateway maps to `network`.
    throw new TypeError('The HTTP client completed without a response', { cause: error });
  }
  if (!(error instanceof HttpErrorResponse)) {
    throw error;
  }
  if (error.status === 0) {
    // `fetch` rejects a request that never reached the server with a `TypeError`; the SDK
    // depends on that to tell a transport failure from an HTTP error envelope.
    throw new TypeError('Network request failed', { cause: error });
  }
  if (error.status >= 200 && error.status <= 299) {
    // `HttpClient` reports a 2xx as an error only when it could not read the successful body
    // (a parse failure). That is not an error response the SDK may parse — handing it back
    // would turn a failure into a success value — so it takes the transport-failure shape.
    throw new TypeError('The HTTP client reported a success status as a failure', { cause: error });
  }
  return error;
}

/**
 * The headers `HttpClient` receives. The transport marker is dropped here and nowhere else, so
 * interceptors, the XSRF interceptor and the wire never see the caller's opt-in switch.
 */
export function toRequestHeaders(headers: Headers): Record<string, string> {
  const result: Record<string, string> = {};
  headers.forEach((value, name) => {
    if (name.toLowerCase() !== TRANSPORT_HEADER_LOWER) {
      result[name] = value;
    }
  });
  return result;
}

// Statuses whose fetch `Response` must not carry a body (its constructor rejects one). Every
// other status keeps the body. A status below 200 cannot become a `Response` at all — not even
// a bodyless one — so it is rejected before conversion instead of being listed here.
const NULL_BODY_STATUSES: ReadonlySet<number> = new Set([204, 205, 304]);

// `Headers` names are case-insensitive, so the marker is matched in one spelling.
const TRANSPORT_HEADER_LOWER = BINARY_TRANSPORT_HEADER.toLowerCase();

// Mime types whose characters the text path can forward truthfully: a JSON body (what the
// generated SDK sends and receives, `application/json`) and textual bodies.
// `application/x-www-form-urlencoded` is textual even though it does not start with `text/`.
const JSON_MIME_TYPE = 'application/json';
const FORM_MIME_TYPE = 'application/x-www-form-urlencoded';
const TEXT_MIME_TYPE_PREFIX = 'text/';

// Whether the characters `HttpClient` handed over are the bytes on the wire. `null` (no
// `Content-Type`) is accepted: a bodyless response has none, and a body a server sends without
// one is still plain text.
export function isTextualContentType(contentType: string | null): boolean {
  if (contentType === null) {
    return true;
  }
  // A `Content-Type` carries parameters (`application/json; charset=utf-8`); only the mime type
  // itself decides whether the bytes are text.
  const [mimeType] = contentType.split(';');
  const normalised = mimeType?.trim().toLowerCase() ?? '';
  return (
    normalised === JSON_MIME_TYPE ||
    normalised === FORM_MIME_TYPE ||
    normalised.startsWith(TEXT_MIME_TYPE_PREFIX)
  );
}

/** What a fetch `Response` is built from: text, a buffer, a view or a Blob. */
export type FetchBody = string | ArrayBuffer | ArrayBufferView<ArrayBuffer> | Blob;

/**
 * Turns one `HttpClient` result into the fetch `Response` the SDK reads. `binaryBody` marks a
 * call whose caller opted into the byte path: only then may a 2xx carry bytes, and only then is
 * a non-text success body legitimate. The status, header-copying and bodyless-status rules are
 * the same for both paths.
 */
export function toFetchResponse(
  status: number,
  statusText: string,
  headers: HttpHeaders,
  body: FetchBody | null,
  source: HttpResponseBase,
  binaryBody: boolean,
): Response {
  if (status < 200 || status > 599) {
    // `Response` accepts 200-599 only; anything else (a 101 upgrade acknowledgement, say) would
    // make its constructor throw a `RangeError` the SDK does not know, and `fetch` has no way to
    // hand a caller a 1xx status. Reject like `fetch` does for a response it cannot deliver and
    // keep the adapter's source object as `cause`.
    throw new TypeError('The HTTP client returned a status a fetch response cannot carry', {
      cause: source,
    });
  }
  if (
    !binaryBody &&
    status >= 200 &&
    status <= 299 &&
    !isTextualContentType(headers.get('Content-Type'))
  ) {
    // The text path reads the body with `responseType: 'text'`, so a binary success body has
    // already been decoded — lossily — by `HttpClient`. Handing it back would be a success value
    // with corrupted content (a downloaded archive, an image) and no way to notice. Refuse it in
    // the transport-failure shape instead: the gateway maps that `TypeError` to `network`.
    throw new TypeError('The HTTP client returned a non-text success response', { cause: source });
  }
  const responseBody = NULL_BODY_STATUSES.has(status) ? null : toFetchBody(body);
  return new Response(responseBody, {
    status,
    statusText,
    headers: toFetchResponseHeaders(headers),
  });
}

// `null` is a bodyless response and an empty body/length is no body at all, and both reach the
// SDK as no body; anything else is forwarded untouched.
function toFetchBody(body: FetchBody | null): FetchBody | null {
  if (body === null) {
    return null;
  }
  if (typeof body === 'string') {
    return body === '' ? null : body;
  }
  const length = body instanceof Blob ? body.size : body.byteLength;
  return length === 0 ? null : body;
}

function toFetchResponseHeaders(headers: HttpHeaders): Record<string, string> {
  const result: Record<string, string> = {};
  for (const name of headers.keys()) {
    // `HttpHeaders.get` returns only the first value of a repeated header, so it would silently
    // drop the rest (two `Vary` tokens, a repeated `Link`). `getAll` cannot, and joining its
    // values with ', ' is the wire form the platform's `Headers.get` reports for a repetition;
    // it returns null for an absent header, where `get` already covers the name.
    const values = headers.getAll(name);
    const value = values === null ? headers.get(name) : values.join(', ');
    if (value !== null && value !== '') {
      result[name] = value;
    }
  }
  return result;
}
