// The binary path of the `HttpClient` fetch adapter. A caller opts one request in with the
// transport marker; this module decodes that marker, reads the request body as bytes, bounds both
// directions at `BINARY_BODY_LIMIT_BYTES` and hands the exact response bytes back. Nothing here
// looks at a `Content-Type`: the mode is always the caller's explicit choice, never a guess.

import {
  HttpEventType,
  type HttpErrorResponse,
  type HttpEvent,
  type HttpHeaders,
  HttpResponse,
  type HttpResponseBase,
} from '@angular/common/http';
import { Observable } from 'rxjs';

import {
  BINARY_BODY_LIMIT_BYTES,
  BINARY_REQUEST,
  BINARY_RESPONSE,
  BINARY_TRANSPORT_HEADER,
} from './binary-transport';
import { type Deadline, readUnderCall, toFetchResponse } from './http-client-fetch-transport';

/** Which directions of one call the caller opted into the byte path. */
export interface BinaryModes {
  /** Send the request body as its exact bytes instead of its UTF-8 decode. */
  readonly request: boolean;
  /** Hand the 2xx response body back as its exact bytes instead of text. */
  readonly response: boolean;
}

/**
 * Decodes the transport marker. `null` means the caller did not opt in and the text path runs
 * unchanged. A token this adapter does not know is refused here, before anything is sent, because
 * a silently ignored marker would send bytes down the text path and corrupt them.
 */
export function parseBinaryModes(value: string | null): BinaryModes | null {
  if (value === null) {
    return null;
  }
  const modes = { request: false, response: false };
  for (const raw of value.split(',')) {
    const token = raw.trim().toLowerCase();
    if (token === BINARY_REQUEST) {
      modes.request = true;
    } else if (token === BINARY_RESPONSE) {
      modes.response = true;
    } else {
      throw new TypeError(`Unknown ${BINARY_TRANSPORT_HEADER} token`);
    }
  }
  return modes;
}

/**
 * Reads a request body as bytes under the call's deadline and the caller's signal. An empty body
 * is sent as no body, and a body over the cap is refused before the request leaves the app, so an
 * oversized upload is a local failure instead of a 413 after the bytes travelled.
 *
 * The bytes are read incrementally rather than through `arrayBuffer()`, so the cap is enforced
 * while the body arrives: a caller-owned stream that never ends would otherwise be buffered in
 * full before the size could be checked. Losing the race (abort or deadline) cancels the reader,
 * so the producer stops instead of feeding a call nobody awaits.
 */
export async function readBinaryRequestBody(
  request: Request,
  sendsBody: boolean,
  deadline: Deadline | null,
): Promise<ArrayBuffer | null> {
  if (!sendsBody) {
    return null;
  }
  const body = request.body;
  if (body === null) {
    return null;
  }
  const reader = body.getReader();
  const cancel = () => {
    void reader.cancel().catch(() => undefined);
  };
  try {
    const bytes = await readUnderCall(
      () => readAllBytes(reader, cancel),
      request.signal,
      deadline,
      cancel,
    );
    return bytes.byteLength === 0 ? null : bytes;
  } finally {
    reader.releaseLock();
  }
}

const INITIAL_REQUEST_BUFFER_BYTES = 64 * 1024;

// Reads one stream to its end, refusing as soon as the cumulative size passes the cap. The
// refusal cancels the stream first: the caller that produced the bytes must stop, and the bytes
// already read are dropped rather than uploaded.
async function readAllBytes(
  reader: ReadableStreamDefaultReader<Uint8Array>,
  cancel: () => void,
): Promise<ArrayBuffer> {
  // Bytes are copied into one growing buffer as they arrive, so a stream of many tiny chunks
  // never leaves a list of views behind. Memory is the accumulator's own storage: about twice its
  // capacity while it grows, starting at a 64 KiB floor.
  let bytes = new Uint8Array(Math.min(INITIAL_REQUEST_BUFFER_BYTES, BINARY_BODY_LIMIT_BYTES));
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) {
      break;
    }
    // A caller-owned stream is untyped at runtime: a chunk that is not a byte view would make the
    // running total `NaN` (every comparison false, so the cap is bypassed) or be copied wrongly.
    // The tag check is realm-proof, unlike `instanceof`.
    if (!isByteChunk(value)) {
      cancel();
      throw new TypeError('The request body stream produced a chunk that is not bytes');
    }
    // The intrinsic length, not the chunk's own property: a genuine `Uint8Array` can shadow
    // `byteLength` with a data property, and under-counting it would skip the cap while the copy
    // below still moves every byte.
    const chunkBytes = byteLengthOf.call(value);
    if (total + chunkBytes > BINARY_BODY_LIMIT_BYTES) {
      cancel();
      throw limitError('request');
    }
    if (total + chunkBytes > bytes.byteLength) {
      const grown = new Uint8Array(
        Math.min(BINARY_BODY_LIMIT_BYTES, Math.max(total + chunkBytes, bytes.byteLength * 2)),
      );
      grown.set(bytes.subarray(0, total));
      bytes = grown;
    }
    bytes.set(value, total);
    total += chunkBytes;
  }
  return total === bytes.byteLength ? bytes.buffer : bytes.slice(0, total).buffer;
}

// The `%TypedArray%` tag getter reads the internal slot, so unlike `Object.prototype.toString` a
// plain object cannot forge it by defining `Symbol.toStringTag`; it is also realm-proof, and a
// `DataView` or a wider typed array reports another tag.
const typedArrayTag = Reflect.get(
  Object.getOwnPropertyDescriptor(
    Object.getPrototypeOf(Uint8Array.prototype) as object,
    Symbol.toStringTag,
  ) ?? {},
  'get',
) as (this: unknown) => string | undefined;

function isByteChunk(value: unknown): value is Uint8Array {
  return typedArrayTag.call(value) === 'Uint8Array';
}

// The `%TypedArray%` `byteLength` getter reads the internal slot, so an own property on a genuine
// chunk cannot shadow it. That matters for accounting only: `set` always copies the real bytes,
// so an under-counted length would add nothing while the copy still moves everything.
const byteLengthOf = Reflect.get(
  Object.getOwnPropertyDescriptor(
    Object.getPrototypeOf(Uint8Array.prototype) as object,
    'byteLength',
  ) ?? {},
  'get',
) as (this: unknown) => number;

/**
 * An error status on a binary call still carries the 00 §9.2 envelope, so its bytes are decoded
 * as text for the SDK — non-fatally (a malformed body becomes replacement characters, never a
 * throw) and bounded, because the refusal must not depend on the response being a success.
 */
export function decodeBinaryErrorBody(error: HttpErrorResponse): string | null {
  const raw: unknown = error.error;
  if (raw === null || raw === undefined) {
    return null;
  }
  if (typeof raw === 'string') {
    assertTextWithinCap(raw, error);
    return raw === '' ? null : raw;
  }
  if (!(raw instanceof ArrayBuffer)) {
    // With `responseType: 'arraybuffer'` a real backend yields an `ArrayBuffer` or nothing; any
    // other shape cannot be forwarded truthfully, so it takes the transport failure shape instead
    // of being dropped.
    throw new TypeError('The HTTP client returned an unsupported binary error body', {
      cause: error,
    });
  }
  if (raw.byteLength > BINARY_BODY_LIMIT_BYTES) {
    throw limitError('response', error);
  }
  const text = new TextDecoder().decode(raw);
  return text === '' ? null : text;
}

/**
 * Fails a binary download as soon as its advertised or observed size passes the cap, so an
 * oversized reply is aborted mid-transfer instead of being buffered whole and then refused. The
 * adapter repeats the check on the decoded body: a server may omit `Content-Length` and report
 * no progress before the final chunk.
 */
export function enforceBinarySizeCap<T>(
  events: Observable<HttpEvent<T>>,
): Observable<HttpEvent<T>> {
  return new Observable<HttpEvent<T>>((subscriber) => {
    const subscription = events.subscribe({
      next: (event) => {
        if (exceedsBinaryCap(event)) {
          subscriber.error(limitError('response'));
          return;
        }
        subscriber.next(event);
      },
      error: (error: unknown) => subscriber.error(error),
      complete: () => subscriber.complete(),
    });
    // Holding the source subscription is what lets the teardown cancel the request: on an
    // over-cap event or a caller abort the `HttpClient` subscription is unsubscribed, which
    // aborts the in-flight XHR.
    return () => subscription.unsubscribe();
  });
}

/**
 * Whether one event of a binary call proves the reply is over the cap. This is the single
 * decision both the adapter and the telemetry interceptor (which sees the same events first)
 * use, so a refused reply is never recorded as a successful call.
 *
 * What each size means (Fetch Standard "handle content codings" decodes the transmitted bytes
 * before the body reaches the page; XMLHttpRequest `loaded` counts those received, decoded bytes):
 * a `DownloadProgress` `loaded` and the final `ArrayBuffer` length are decoded bytes, while
 * `Content-Length` is the wire size. Under a `Content-Encoding` the wire size is only an early
 * refusal; the decoded bytes decide.
 */
export function exceedsBinaryCap<T>(event: HttpEvent<T>): boolean {
  const size = advertisedSize(event);
  return size !== null && size > BINARY_BODY_LIMIT_BYTES;
}

/** Whether an event of a binary call is the final response the adapter reads its bytes from. */
export function isFinalResponse<T>(event: HttpEvent<T>): event is HttpResponse<T> {
  return event instanceof HttpResponse;
}

/**
 * Converts a 2xx binary call into the fetch `Response`, repeating the cap on the decoded body.
 * The streaming check in {@link enforceBinarySizeCap} can be dodged by a server that omits
 * `Content-Length` and reports no progress, so the bytes that actually arrived are the last word.
 */
export function toBinaryResponse(
  status: number,
  statusText: string,
  headers: HttpHeaders,
  body: ArrayBuffer | null,
  source: HttpResponseBase,
): Response {
  if (body !== null && body.byteLength > BINARY_BODY_LIMIT_BYTES) {
    throw limitError('response', source);
  }
  return toFetchResponse(status, statusText, headers, body, source, true);
}

// A `Content-Length` the server declared, the bytes downloaded so far, the final byte length or
// the UTF-8 size of a final text reply. Anything else (the `Sent` event) says nothing about the
// size.
function advertisedSize<T>(event: HttpEvent<T>): number | null {
  if (event.type === HttpEventType.ResponseHeader) {
    const declared = event.headers.get('Content-Length');
    return declared === null ? null : declaredLength(declared);
  }
  if (event.type === HttpEventType.DownloadProgress) {
    // A length that is not a finite number proves nothing about the size, and letting it through
    // would make every comparison false: a non-number or a `NaN` must be over the cap.
    const loaded: unknown = event.loaded;
    return typeof loaded === 'number' && Number.isFinite(loaded)
      ? loaded
      : Number.POSITIVE_INFINITY;
  }
  if (event.type === HttpEventType.Response) {
    if (event.body instanceof ArrayBuffer) {
      return event.body.byteLength;
    }
    // A request-only call gets its reply as text with no progress event; the cap is the same.
    if (typeof event.body === 'string' && textExceedsCap(event.body)) {
      return Number.POSITIVE_INFINITY;
    }
  }
  return null;
}

// Repeated `Content-Length` fields reach the page comma-joined (the Fetch header list combines
// them, as does `getResponseHeader`). RFC 9110 section 8.6 allows a list only when every member is
// the same length, and says any other value is invalid framing. Only a plain run of digits is a
// length (`Number()` would accept `0x10` or `1e9`); a value that is not one valid length is
// refused early, and the decoded bytes stay the authority for everything that passes.
function declaredLength(value: string): number {
  const members = value.split(',').map((member) => member.trim());
  const [first] = members;
  if (first === undefined || !/^\d+$/.test(first) || members.some((member) => member !== first)) {
    return Number.POSITIVE_INFINITY;
  }
  return Number(first);
}

/**
 * Repeats the cap on a text body of a call that opted into the binary transport in only one
 * direction. `HttpClient` has already decoded it, so the UTF-8 size is recomputed here; a string
 * can never be shorter in bytes than in UTF-16 units, which lets the common cases skip the encode.
 */
export function assertTextWithinCap(text: string | null, source: unknown): void {
  if (text !== null && textExceedsCap(text)) {
    throw limitError('response', source);
  }
}

function textExceedsCap(text: string): boolean {
  return (
    text.length * 3 > BINARY_BODY_LIMIT_BYTES &&
    (text.length > BINARY_BODY_LIMIT_BYTES ||
      new TextEncoder().encode(text).byteLength > BINARY_BODY_LIMIT_BYTES)
  );
}

// The single place the cap is worded, so both directions and every refusal site report the same
// failure the gateway maps to `network`.
function limitError(direction: 'request' | 'response', cause?: unknown): TypeError {
  const message = `Binary ${direction} body exceeds limit of ${BINARY_BODY_LIMIT_BYTES} bytes`;
  return cause === undefined ? new TypeError(message) : new TypeError(message, { cause });
}
