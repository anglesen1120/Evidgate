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

// Reads one stream to its end, refusing as soon as the cumulative size passes the cap. The
// refusal cancels the stream first: the caller that produced the bytes must stop, and the bytes
// already read are dropped rather than uploaded.
async function readAllBytes(
  reader: ReadableStreamDefaultReader<Uint8Array>,
  cancel: () => void,
): Promise<ArrayBuffer> {
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) {
      break;
    }
    total += value.byteLength;
    if (total > BINARY_BODY_LIMIT_BYTES) {
      cancel();
      throw limitError('request');
    }
    chunks.push(value);
  }
  const buffer = new ArrayBuffer(total);
  const bytes = new Uint8Array(buffer);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return buffer;
}

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
export function enforceBinarySizeCap(
  events: Observable<HttpEvent<ArrayBuffer>>,
): Observable<HttpEvent<ArrayBuffer>> {
  return new Observable<HttpEvent<ArrayBuffer>>((subscriber) => {
    const subscription = events.subscribe({
      next: (event) => {
        const size = advertisedSize(event);
        if (size !== null && size > BINARY_BODY_LIMIT_BYTES) {
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

/** Whether an event of a binary call is the final response the adapter reads its bytes from. */
export function isFinalResponse(event: HttpEvent<ArrayBuffer>): event is HttpResponse<ArrayBuffer> {
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

// A `Content-Length` the server declared, or the bytes downloaded so far. Anything else (the
// `Sent` event, a textual response) says nothing about the size.
function advertisedSize(event: HttpEvent<ArrayBuffer>): number | null {
  if (event.type === HttpEventType.ResponseHeader) {
    const declared = event.headers.get('Content-Length');
    if (declared === null) {
      return null;
    }
    const byteLength = Number(declared);
    return Number.isFinite(byteLength) ? byteLength : null;
  }
  if (event.type === HttpEventType.DownloadProgress) {
    return event.loaded;
  }
  return null;
}

// The single place the cap is worded, so both directions and every refusal site report the same
// failure the gateway maps to `network`.
function limitError(direction: 'request' | 'response', cause?: unknown): TypeError {
  const message = `Binary ${direction} body exceeds limit of ${BINARY_BODY_LIMIT_BYTES} bytes`;
  return cause === undefined ? new TypeError(message) : new TypeError(message, { cause });
}
