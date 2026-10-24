// 09.1.1-T3.2 (ADR-0008 decision row a, rule 3): the `fetch` implementation handed to the
// generated `@hey-api/client-fetch` SDK. It turns the SDK's `Request` into an Angular
// `HttpClient` request, so the functional interceptors (`X-Request-Id`/`traceparent`,
// `Idempotency-Key`, Angular's XSRF interceptor) and the HttpClient test backend see every
// call, and it turns `HttpResponse`/`HttpErrorResponse` back into a fetch `Response`, so the
// SDK keeps its own 00 §9.2 error path (raw body, status, `Retry-After`).
//
// Interceptors see text bodies and absolute URLs (ADR-0008 rule 3): the body is the request
// text and the query string stays inside `request.url`. The SDK's relative `baseUrl` `/v1`
// resolves against the page origin in a browser only; Node's `Request` needs an absolute URL,
// so specs always build URLs from an absolute origin.
//
// The body is forwarded as text, so this adapter supports JSON and text payloads only: it reads
// `request.text()`, which decodes the bytes as UTF-8 and loses a binary body. A `Blob`,
// `FormData`/multipart or other non-text body is therefore rejected before the request leaves
// the app (a `TypeError`, the shape `fetch` uses for a request it cannot perform) instead of
// arriving truncated. Uploads do not travel through this adapter: 06.1.2 hands the client
// presigned URLs and the browser PUTs the bytes itself.
//
// This module and `api-client.ts` are the only places that touch `HttpClient`; the global
// `fetch` is never read or replaced.

import {
  HttpErrorResponse,
  type HttpClient,
  type HttpHeaders,
  type HttpResponse,
} from '@angular/common/http';
import { EmptyError, firstValueFrom, fromEvent, takeUntil } from 'rxjs';

/** The `fetch` shape the generated client resolves and calls. */
export type HttpClientFetch = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

// Statuses whose fetch `Response` must not carry a body (its constructor rejects one). Every
// other status keeps the body. A status below 200 cannot become a `Response` at all — not even
// a bodyless one — so it is rejected before conversion instead of being listed here.
const NULL_BODY_STATUSES: ReadonlySet<number> = new Set([204, 205, 304]);

// Mime types the adapter forwards byte-for-byte through `request.text()`: a JSON body (what the
// generated SDK sends, `application/json` from its default headers) and textual bodies.
// `application/x-www-form-urlencoded` is textual even though it does not start with `text/`.
const JSON_MIME_TYPE = 'application/json';
const FORM_MIME_TYPE = 'application/x-www-form-urlencoded';
const TEXT_MIME_TYPE_PREFIX = 'text/';

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
 * Three failures reject instead, always in `fetch`'s network-error shape (a `TypeError` with the
 * original as `cause`): status `0` (a transport failure), a response status the `Response`
 * constructor cannot represent — a 101 upgrade acknowledgement, say, or anything outside
 * 200-599 — and an `HttpErrorResponse` carrying a 2xx status, which `HttpClient` only produces
 * when it could not read a successful body and which must never become a success value. An
 * aborted `request.signal` cancels the request and rejects with a `DOMException` named
 * `AbortError`. A request body that is neither JSON nor text is refused with a `TypeError`
 * before it is sent, because the adapter can only forward the body as text; see the module
 * comment.
 */
export function createHttpClientFetch(http: HttpClient): HttpClientFetch {
  return async (input, init) => {
    const request =
      init === undefined && input instanceof Request ? input : new Request(input, init);

    if (request.signal.aborted) {
      throw abortError();
    }

    const method = request.method.toUpperCase();
    const text = method === 'GET' || method === 'HEAD' ? '' : await request.text();
    // Reading the body awaits, so the signal may have been aborted since the check above; a
    // listener added to an already-fired signal never runs, so the abort must be caught here.
    if (request.signal.aborted) {
      throw abortError();
    }
    assertTextRequestBody(request.headers.get('Content-Type'), text);
    const pending = http.request(method, request.url, {
      body: text.length === 0 ? null : text,
      headers: Object.fromEntries(request.headers),
      observe: 'response',
      responseType: 'blob',
      withCredentials: request.credentials === 'include',
    });

    let response: HttpResponse<Blob>;
    try {
      // `observe: 'response'` emits exactly once. An abort completes the stream through
      // `takeUntil`, which unsubscribes (and so cancels) the request and leaves no value, so
      // `firstValueFrom` rejects with `EmptyError` — that is the abort path below.
      response = await firstValueFrom(pending.pipe(takeUntil(fromEvent(request.signal, 'abort'))));
    } catch (error: unknown) {
      if (request.signal.aborted && error instanceof EmptyError) {
        throw abortError();
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
        // `HttpClient` reports a 2xx as an error only when it could not read the successful
        // body (a parse failure). That is not an error response the SDK may parse — handing it
        // back would turn a failure into a success value — so it takes the transport-failure
        // shape and the gateway maps it to `network`.
        throw new TypeError('The HTTP client reported a success status as a failure', {
          cause: error,
        });
      }
      return await toResponse(error.status, error.statusText, error.headers, error.error, error);
    }
    return toResponse(
      response.status,
      response.statusText,
      response.headers,
      response.body,
      response,
    );
  };
}

async function toResponse(
  status: number,
  statusText: string,
  headers: HttpHeaders,
  body: unknown,
  source: HttpResponse<Blob> | HttpErrorResponse,
): Promise<Response> {
  if (status < 200 || status > 599) {
    // `Response` accepts 200-599 only; anything else (a 101 upgrade acknowledgement, say) would
    // make its constructor throw a `RangeError` the SDK does not know, and `fetch` has no way to
    // hand a caller a 1xx status. Reject like `fetch` does for a response it cannot deliver and
    // keep the adapter's source object as `cause`.
    throw new TypeError('The HTTP client returned a status a fetch response cannot carry', {
      cause: source,
    });
  }
  const responseBody = NULL_BODY_STATUSES.has(status) ? null : await toFetchBody(body);
  return new Response(responseBody, {
    status,
    statusText,
    headers: toFetchResponseHeaders(headers),
  });
}

// The adapter forwards request bodies as text (see the module comment), so a non-text
// `Content-Type` means the body `request.text()` produced is a lossy decode of what the caller
// meant to send. Refuse it loudly instead of sending corrupted bytes. An absent `Content-Type`
// is allowed: the SDK removes it for a bodyless request and `Request` defaults a string body to
// `text/plain`.
function assertTextRequestBody(contentType: string | null, text: string): void {
  if (text.length === 0 || isTextualContentType(contentType)) {
    return;
  }
  throw new TypeError('Unsupported request body type for the HttpClient fetch adapter');
}

function isTextualContentType(contentType: string | null): boolean {
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

// One abort reason for the three abort paths (already aborted, aborted while reading the body,
// aborted in flight): `fetch` rejects with a `DOMException` named `AbortError`.
function abortError(): DOMException {
  return new DOMException('The request was aborted.', 'AbortError');
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

async function toFetchBody(body: unknown): Promise<BodyInit | null> {
  if (body === null || body === undefined) {
    return null;
  }
  if (isBlobLike(body)) {
    // A `Blob` from a realm without `stream()` (jsdom in the unit tests, older polyfills) is
    // stringified by the `Response` constructor; hand its bytes over instead (ADR-0008
    // adapter gotcha). A same-realm `Blob` is passed through without a copy.
    return typeof body.stream === 'function' ? body : await body.arrayBuffer();
  }
  if (body instanceof ArrayBuffer) {
    return body;
  }
  if (typeof body === 'string') {
    return body;
  }
  // An error body Angular already parsed (JSON response type) is re-encoded as JSON text.
  return JSON.stringify(body);
}

// Realm-agnostic `Blob` detection: `instanceof Blob` fails across realms (Node's `Response`
// receives a jsdom `Blob` in specs), while a native browser `Blob` is passed through.
function isBlobLike(value: unknown): value is Blob {
  if (typeof value !== 'object' || value === null) {
    return false;
  }
  const candidate = value as Partial<Blob>;
  return (
    typeof candidate.arrayBuffer === 'function' &&
    typeof candidate.size === 'number' &&
    typeof candidate.type === 'string'
  );
}
