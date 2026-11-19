// The `fetch` implementation handed to the generated `@hey-api/client-fetch` SDK. It turns the
// SDK's `Request` into an Angular `HttpClient` request, so the functional interceptors
// (`X-Request-Id`/`traceparent`, `Idempotency-Key`, Angular's XSRF interceptor) and the HttpClient
// test backend see every call, and it turns `HttpResponse`/`HttpErrorResponse` back into a fetch
// `Response`, so the SDK keeps its own 00 §9.2 error path (raw body, status, `Retry-After`).
//
// Two body paths, chosen per request by the caller's `X-Evidgate-Transport` marker and never by a
// `Content-Type` sniff. Without the marker the adapter forwards text exactly as before. With it the
// caller names the directions it needs — `binary-request` sends the body's exact bytes (an XLSX
// workbook), `binary-response` returns the reply's exact bytes — under one size cap and the same
// deadline, abort and interceptor rules, because a byte-for-byte body has no textual round trip it
// could fall back on.
//
// Every call runs under a deadline (`DEFAULT_REQUEST_TIMEOUT_MS`, configurable for a caller and for
// specs). On expiry the adapter unsubscribes the `HttpClient` request (which cancels it) and rejects
// with a `TimeoutError`; a caller abort rejects with `AbortError` and wins when both happen. This
// module and `api-client.ts` are the only places that touch `HttpClient`; the global `fetch` is
// never read or replaced.

import { type HttpClient, type HttpEvent, type HttpResponse } from '@angular/common/http';
import { filter, type Observable } from 'rxjs';

import { BINARY_TRANSPORT_HEADER, cappedBinaryContext } from './binary-transport';
import {
  assertTextWithinCap,
  decodeBinaryErrorBody,
  enforceBinarySizeCap,
  isFinalResponse,
  parseBinaryModes,
  readBinaryRequestBody,
  toBinaryResponse,
  type BinaryModes,
} from './http-client-fetch-binary';
import {
  abortError,
  createDeadline,
  errorResponseOrThrow,
  isTextualContentType,
  readUnderCall,
  settleCall,
  toFetchResponse,
  toRequestHeaders,
  type Deadline,
} from './http-client-fetch-transport';

/** The `fetch` shape the generated client resolves and calls. */
export type HttpClientFetch = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

/** Tunables of one adapter instance; every member has a production default. */
export interface HttpClientFetchOptions {
  /**
   * Deadline for one whole call in milliseconds: the request body read, the request itself and
   * the response. Defaults to {@link DEFAULT_REQUEST_TIMEOUT_MS}; a non-finite or non-positive
   * value disables the deadline (the browser's own timeout is then the only bound).
   */
  readonly timeoutMs?: number;
}

/**
 * The production deadline for one API call. A stalled connection must become a typed `timeout`
 * failure the UI can report and retry, not a spinner that never resolves; 30 s is longer than
 * every 09.1.1 read, and shorter than the browser's own HTTP timeout.
 */
export const DEFAULT_REQUEST_TIMEOUT_MS = 30_000;

/**
 * Builds the `fetch` the generated SDK client is configured with:
 * `createClient(createConfig({ baseUrl: '/v1',
 * fetch: createHttpClientFetch(inject(HttpClient)) }))`.
 *
 * The SDK calls it with a `Request` built from `baseUrl + path`; a string/URL plus `init` is
 * normalised through `new Request(input, init)` the way `fetch` does. Every request goes
 * through `HttpClient` (interceptors, XSRF, test backend). 2xx becomes a `Response` carrying
 * the body, status and headers; any `HttpErrorResponse` with an error status becomes a
 * `Response` carrying the error body, so the SDK parses the 00 §9.2 envelope itself.
 *
 * Requests marked with `X-Evidgate-Transport` take the binary path for the directions they name
 * (see {@link BinaryModes}): the body travels as bytes, the reply comes back as bytes and both
 * are bounded by `BINARY_BODY_LIMIT_BYTES`; an unknown marker token is refused before sending.
 *
 * Five failures reject, always in `fetch`'s network-error shape (a `TypeError` with the original
 * as `cause` unless it is the caller's own body): status `0` (a transport failure), a 2xx whose
 * `Content-Type` is not text on the text path, a body or reply over the binary cap, a response
 * status the `Response` constructor cannot represent — a 101 upgrade acknowledgement, say, or
 * anything outside 200-599 — and an `HttpErrorResponse` carrying a 2xx status, which `HttpClient`
 * only produces when it could not read a successful body and which must never become a success
 * value. An aborted `request.signal` cancels the request and rejects with a `DOMException` named
 * `AbortError`. A call that outlives `timeoutMs` (default {@link DEFAULT_REQUEST_TIMEOUT_MS}) is
 * cancelled and rejects with a `DOMException` named `TimeoutError`; an abort that arrives in the
 * same turn — or while a stalled request body is still being read — wins and stays an
 * `AbortError`. On the text path a request body that is neither JSON nor text is refused with a
 * `TypeError` before it is sent, because that path can only forward the body as text.
 */
export function createHttpClientFetch(
  http: HttpClient,
  options: HttpClientFetchOptions = {},
): HttpClientFetch {
  const timeoutMs = options.timeoutMs ?? DEFAULT_REQUEST_TIMEOUT_MS;

  return async (input, init) => {
    const request =
      init === undefined && input instanceof Request ? input : new Request(input, init);

    if (request.signal.aborted) {
      throw abortError();
    }

    const modes = parseBinaryModes(request.headers.get(BINARY_TRANSPORT_HEADER));
    const deadline = createDeadline(timeoutMs);
    try {
      return await sendCall(http, request, modes, deadline);
    } finally {
      deadline?.cancel();
    }
  };
}

async function sendCall(
  http: HttpClient,
  request: Request,
  modes: BinaryModes | null,
  deadline: Deadline | null,
): Promise<Response> {
  const method = request.method.toUpperCase();
  const sendsBody = method !== 'GET' && method !== 'HEAD';
  const body: string | ArrayBuffer | null =
    modes?.request === true
      ? await readBinaryRequestBody(request, sendsBody, deadline)
      : await readTextRequestBody(request, sendsBody, deadline);
  // The read races the signal, so a cancellation during it arrives as an `AbortError`. This
  // re-check covers the other order: the read settled in the same turn the signal was aborted, so
  // nothing is sent for a call the caller cancelled. It comes before the text body-type refusal,
  // which would otherwise report a cancelled call as a body the caller never asked to send.
  if (request.signal.aborted) {
    throw abortError();
  }
  if (typeof body === 'string') {
    assertTextRequestBody(request.headers.get('Content-Type'), body);
  }

  try {
    return await receiveResponse(http, method, request, body, modes, deadline);
  } catch (error: unknown) {
    const failure = errorResponseOrThrow(error, request.signal);
    // `HttpErrorResponse.error` is `any`: `decodeBinaryErrorBody` narrows it itself, and the text
    // cast is what keeps the adapter's `string | null` contract honest without an `any` reaching
    // `toFetchResponse`. Any call that opted into the binary transport gets its error body
    // capped, even when only the request was binary.
    const errorBody: string | null =
      modes === null ? (failure.error as string | null) : decodeBinaryErrorBody(failure);
    return toFetchResponse(
      failure.status,
      failure.statusText,
      failure.headers,
      errorBody,
      failure,
      false,
    );
  }
}

async function receiveResponse(
  http: HttpClient,
  method: string,
  request: Request,
  body: string | ArrayBuffer | null,
  modes: BinaryModes | null,
  deadline: Deadline | null,
): Promise<Response> {
  if (modes?.response === true) {
    const response = await settleCall(
      finalResponse(http, method, request, body),
      request.signal,
      deadline,
    );
    return toBinaryResponse(
      response.status,
      response.statusText,
      response.headers,
      response.body,
      response,
    );
  }
  const response = await settleCall(
    modes === null
      ? textResponse(http, method, request, body)
      : cappedTextResponse(http, method, request, body),
    request.signal,
    deadline,
  );
  if (modes !== null) {
    assertTextWithinCap(response.body, response);
  }
  return toFetchResponse(
    response.status,
    response.statusText,
    response.headers,
    response.body,
    response,
    false,
  );
}

// The text path: `responseType: 'text'` hands over the exact characters the server sent, which is
// what the SDK's JSON envelope needs. Binary replies are refused later, in `toFetchResponse`.
function textResponse(
  http: HttpClient,
  method: string,
  request: Request,
  body: string | ArrayBuffer | null,
): Observable<HttpResponse<string>> {
  return http.request(method, request.url, {
    body,
    headers: toRequestHeaders(request.headers),
    observe: 'response',
    responseType: 'text',
    withCredentials: request.credentials === 'include',
  });
}

// The binary path reads events, because download progress is how an oversized reply is aborted
// while it is still arriving instead of after `HttpClient` buffered all of it.
function finalResponse(
  http: HttpClient,
  method: string,
  request: Request,
  body: string | ArrayBuffer | null,
): Observable<HttpResponse<ArrayBuffer>> {
  const events: Observable<HttpEvent<ArrayBuffer>> = http.request(method, request.url, {
    body,
    headers: toRequestHeaders(request.headers),
    observe: 'events',
    reportProgress: true,
    context: cappedBinaryContext(),
    responseType: 'arraybuffer',
    withCredentials: request.credentials === 'include',
  });
  return events.pipe(enforceBinarySizeCap, filter(isFinalResponse));
}

// A call that opted into the binary transport for its request only still reads a text reply, but
// the reply is bounded like a binary one: progress events abort an oversized body mid-transfer.
function cappedTextResponse(
  http: HttpClient,
  method: string,
  request: Request,
  body: string | ArrayBuffer | null,
): Observable<HttpResponse<string>> {
  const events: Observable<HttpEvent<string>> = http.request(method, request.url, {
    body,
    headers: toRequestHeaders(request.headers),
    observe: 'events',
    reportProgress: true,
    context: cappedBinaryContext(),
    responseType: 'text',
    withCredentials: request.credentials === 'include',
  });
  return events.pipe(
    enforceBinarySizeCap,
    filter((event): event is HttpResponse<string> => isFinalResponse(event)),
  );
}

async function readTextRequestBody(
  request: Request,
  sendsBody: boolean,
  deadline: Deadline | null,
): Promise<string | null> {
  const text = sendsBody ? await readUnderCall(() => request.text(), request.signal, deadline) : '';
  // An absent body and an empty one are the same call: `HttpClient` must see `null`, so the
  // request carries no body at all and interceptors see a bodyless request.
  return text.length === 0 ? null : text;
}

// The body is forwarded as text, so a non-text `Content-Type` means `request.text()` produced a
// lossy decode of what the caller meant to send. Refuse it loudly instead of sending corrupted
// bytes; the binary path is the explicit opt-out. An absent `Content-Type` is allowed: the SDK
// removes it for a bodyless request and `Request` defaults a string body to `text/plain`.
function assertTextRequestBody(contentType: string | null, text: string): void {
  if (text.length === 0 || isTextualContentType(contentType)) {
    return;
  }
  throw new TypeError('Unsupported request body type for the HttpClient fetch adapter');
}
