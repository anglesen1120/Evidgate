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

// Statuses whose fetch `Response` must not carry a body (the constructor rejects one). 101 is
// listed because the HTTP client surfaces it as a bodyless upgrade acknowledgement.
const NULL_BODY_STATUSES: ReadonlySet<number> = new Set([101, 204, 205, 304]);

/**
 * Builds the `fetch` the generated SDK client is configured with:
 * `createClient(createConfig({ baseUrl: '/v1',
 * fetch: createHttpClientFetch(inject(HttpClient)) }))`.
 *
 * The SDK calls it with a `Request` built from `baseUrl + path`; a string/URL plus `init` is
 * normalised through `new Request(input, init)` the way `fetch` does. Every request goes
 * through `HttpClient` (interceptors, XSRF, test backend). 2xx becomes a `Response` carrying
 * the body, status and headers; any `HttpErrorResponse` with an HTTP status becomes a
 * `Response` carrying the error body, so the SDK parses the 00 §9.2 envelope itself; status
 * `0` (a transport failure) is rethrown as `TypeError`, as `fetch` does. An aborted
 * `request.signal` cancels the request and rejects with a `DOMException` named `AbortError`.
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
      return await toResponse(error.status, error.statusText, error.headers, error.error);
    }
    return toResponse(response.status, response.statusText, response.headers, response.body);
  };
}

async function toResponse(
  status: number,
  statusText: string,
  headers: HttpHeaders,
  body: unknown,
): Promise<Response> {
  const responseBody = NULL_BODY_STATUSES.has(status) ? null : await toFetchBody(body);
  return new Response(responseBody, {
    status,
    statusText,
    headers: toFetchResponseHeaders(headers),
  });
}

// One abort reason for the three abort paths (already aborted, aborted while reading the body,
// aborted in flight): `fetch` rejects with a `DOMException` named `AbortError`.
function abortError(): DOMException {
  return new DOMException('The request was aborted.', 'AbortError');
}

function toFetchResponseHeaders(headers: HttpHeaders): Record<string, string> {
  const result: Record<string, string> = {};
  for (const name of headers.keys()) {
    const value = headers.get(name);
    if (value !== null) {
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
