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
// Request bodies: the body is forwarded as text, so this adapter supports JSON and text
// payloads only. It reads `request.text()`, which decodes the bytes as UTF-8 and loses a binary
// body. A `Blob`, `FormData`/multipart or other non-text body is therefore rejected before the
// request leaves the app (a `TypeError`, the shape `fetch` uses for a request it cannot
// perform) instead of arriving truncated. Uploads do not travel through this adapter: 06.1.2
// hands the client presigned URLs and the browser PUTs the bytes itself.
//
// Response bodies: every response is read as text (`responseType: 'text'`), so a JSON or
// textual body reaches the SDK as the exact characters the server sent — no `Blob`, no parse
// step, no binary. An error status keeps its body whatever its `Content-Type`, because the SDK
// parses the 00 §9.2 envelope (or reports the raw text) from those characters. A 2xx with a
// non-textual `Content-Type` (`application/octet-stream`, `image/png`, …) cannot be handed to
// the caller truthfully as text: it is refused with a `TypeError`, the transport-failure shape
// the gateway maps to `network`, rather than returned as a corrupted success body.
//
// 09.1.1-T8: every call runs under a deadline (`DEFAULT_REQUEST_TIMEOUT_MS`, 30 s, configurable
// for a caller and for specs). On expiry the adapter unsubscribes the `HttpClient` request
// (which cancels it) and rejects with `new DOMException('The request timed out.',
// 'TimeoutError')`, the shape `AbortSignal.timeout` produces; a caller abort still rejects with
// `AbortError` and wins when both happen.
//
// This module and `api-client.ts` are the only places that touch `HttpClient`; the global
// `fetch` is never read or replaced.

import {
  HttpErrorResponse,
  type HttpClient,
  type HttpHeaders,
  type HttpResponse,
} from '@angular/common/http';
import { EmptyError, firstValueFrom, from, fromEvent, NEVER, takeUntil } from 'rxjs';

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

// Statuses whose fetch `Response` must not carry a body (its constructor rejects one). Every
// other status keeps the body. A status below 200 cannot become a `Response` at all — not even
// a bodyless one — so it is rejected before conversion instead of being listed here.
const NULL_BODY_STATUSES: ReadonlySet<number> = new Set([204, 205, 304]);

// Mime types whose characters the adapter can forward truthfully: a JSON body (what the
// generated SDK sends and receives, `application/json`) and textual bodies.
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
 * Four failures reject instead, always in `fetch`'s network-error shape (a `TypeError` with the
 * original as `cause`): status `0` (a transport failure), a 2xx whose `Content-Type` is not
 * text, a response status the `Response` constructor cannot represent — a 101 upgrade
 * acknowledgement, say, or anything outside 200-599 — and an `HttpErrorResponse` carrying a 2xx
 * status, which `HttpClient` only produces when it could not read a successful body and which
 * must never become a success value. An aborted `request.signal` cancels the request and
 * rejects with a `DOMException` named `AbortError`. A call that outlives `timeoutMs` (default
 * {@link DEFAULT_REQUEST_TIMEOUT_MS}) is cancelled and rejects with a `DOMException` named
 * `TimeoutError`; an abort that arrives in the same turn — or while a stalled request body is
 * still being read — wins and stays an `AbortError`. A request body that is neither JSON nor
 * text is refused with a `TypeError` before it is sent, because the adapter can only forward
 * the body as text; see the module comment.
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

    const deadline = createDeadline(timeoutMs);
    try {
      const text = await readRequestBody(request, deadline);
      // `readRequestBody` races the signal, so a cancellation during the read arrives as an
      // `AbortError`. This re-check covers the other order: the body read settled in the same
      // turn the signal was aborted, so nothing is sent for a call the caller cancelled.
      if (request.signal.aborted) {
        throw abortError();
      }
      assertTextRequestBody(request.headers.get('Content-Type'), text);
      const pending = http.request(request.method.toUpperCase(), request.url, {
        body: text.length === 0 ? null : text,
        headers: Object.fromEntries(request.headers),
        observe: 'response',
        responseType: 'text',
        withCredentials: request.credentials === 'include',
      });

      let response: HttpResponse<string>;
      try {
        // `observe: 'response'` emits exactly once. An abort completes the stream through
        // `takeUntil`, which unsubscribes (and so cancels) the request and leaves no value, so
        // `firstValueFrom` rejects with `EmptyError` — that is the abort path below. The
        // deadline does the same by erroring the stream with its `TimeoutError`, which the
        // catch re-throws once the abort check has had its say.
        response = await firstValueFrom(
          pending.pipe(
            takeUntil(fromEvent(request.signal, 'abort')),
            takeUntil(deadline === null ? NEVER : from(deadline.expiration)),
          ),
        );
      } catch (error: unknown) {
        // A caller abort wins over the deadline when both arrive in the same turn: it stays an
        // `AbortError` and never becomes a typed `timeout` failure.
        if (request.signal.aborted && (error instanceof EmptyError || isTimeoutError(error))) {
          throw abortError();
        }
        if (isTimeoutError(error)) {
          throw error;
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
          // body (a parse failure). That is not an error response the SDK may parse — handing
          // it back would turn a failure into a success value — so it takes the
          // transport-failure shape and the gateway maps it to `network`.
          throw new TypeError('The HTTP client reported a success status as a failure', {
            cause: error,
          });
        }
        // `HttpErrorResponse.error` is `any`: with `responseType: 'text'` Angular hands over the
        // response text (or `null` for a bodyless error), and the assertion is what keeps the
        // adapter's `string | null` contract honest without an `any` reaching `toResponse`.
        const body: string | null = error.error as string | null;
        return toResponse(error.status, error.statusText, error.headers, body, error);
      }
      return toResponse(
        response.status,
        response.statusText,
        response.headers,
        response.body,
        response,
      );
    } finally {
      deadline?.cancel();
    }
  };
}

/** One call's deadline: a promise that never resolves and rejects once, plus its timer. */
interface Deadline {
  /** Rejects with the deadline's `TimeoutError` when the deadline passes; never resolves. */
  readonly expiration: Promise<never>;
  /** Stops the timer, so a finished call leaves nothing scheduled behind. */
  readonly cancel: () => void;
}

function createDeadline(timeoutMs: number): Deadline | null {
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

/**
 * Reads the request body under the call's deadline and the caller's `signal`. A body that never
 * finishes arriving (a stalled upload, a caller-owned stream) must not hold the call open, so
 * three outcomes race: the body text, the deadline's `TimeoutError` and the caller's abort. An
 * abort wins over a deadline that fires in the same turn, and it is the only ending left when the
 * deadline is disabled (`timeoutMs <= 0`). The listener never outlives the read.
 */
async function readRequestBody(request: Request, deadline: Deadline | null): Promise<string> {
  const method = request.method.toUpperCase();
  if (method === 'GET' || method === 'HEAD') {
    return '';
  }
  const { signal } = request;
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
    const body = request.text();
    return await (deadline === null
      ? Promise.race([body, aborted])
      : Promise.race([body, aborted, deadline.expiration]));
  } catch (error: unknown) {
    // A read that ends because of a cancellation can reject with the stream's own error, so the
    // signal is the authority on whether this was one - and an abort also wins over a deadline
    // that fired at the same moment.
    if (signal.aborted) {
      throw abortError();
    }
    throw error;
  } finally {
    signal.removeEventListener('abort', onAbort);
  }
}

function toResponse(
  status: number,
  statusText: string,
  headers: HttpHeaders,
  body: string | null,
  source: HttpResponse<string> | HttpErrorResponse,
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
  if (status >= 200 && status <= 299 && !isTextualContentType(headers.get('Content-Type'))) {
    // Responses are read as text, so a binary success body has already been decoded — lossily —
    // by `HttpClient`. Handing it back would be a success value with corrupted content (a
    // downloaded archive, an image) and no way to notice. Refuse it in the transport-failure
    // shape instead: the gateway maps that `TypeError` to `network`, never to a success.
    throw new TypeError('The HTTP client returned a non-text success response', { cause: source });
  }
  const responseBody = NULL_BODY_STATUSES.has(status) ? null : toFetchBody(body);
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

// Whether the characters `HttpClient` handed over are the bytes on the wire. `null` (no
// `Content-Type`) is accepted: a bodyless response has none, and a body a server sends without
// one is still plain text.
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

// `responseType: 'text'` hands the decoded body over as a string; `null` is a bodyless
// response and `''` an empty body, and both reach the SDK as no body at all.
function toFetchBody(body: string | null): string | null {
  if (body === null || body === '') {
    return null;
  }
  return body;
}
