// 09.1.1-T3.2 (ADR-0008 decision row a + adapter gotchas): specs for the `fetch` adapter that
// puts the generated SDK behind Angular `HttpClient`. They pin what the interceptors see
// (method, absolute URL, forwarded headers, exact text body, withCredentials) and what the SDK
// gets back (a fetch `Response` for 2xx and for 00 §9.2 error statuses, `TypeError` for
// status 0, `AbortError` on abort).

import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpClient, HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ErrorEnvelope } from '@evidgate/sdk';

import { createHttpClientFetch, type HttpClientFetch } from './http-client-fetch';

const API_ORIGIN = 'http://localhost';

// Node's `Request` rejects a relative URL, whereas the SDK's `/v1` baseUrl resolves against the
// page origin in a browser; the specs therefore use the absolute form of the same URL.
function apiUrl(path: string): string {
  return `${API_ORIGIN}/v1${path}`;
}

function jsonBlob(value: unknown): Blob {
  return new Blob([JSON.stringify(value)], { type: 'application/json' });
}

// The adapter awaits `request.text()` before subscribing, so the request reaches the testing
// backend a microtask after `adapter(...)` returns; `match` removes it from the open list, which
// also keeps `verify()` green.
async function nextRequest(httpMock: HttpTestingController, url: string): Promise<TestRequest> {
  return await vi.waitFor(
    () => {
      const [request] = httpMock.match(url);
      if (request === undefined) {
        throw new Error(`The adapter has not issued ${url} yet.`);
      }
      return request;
    },
    { interval: 1 },
  );
}

describe('createHttpClientFetch (09.1.1-T3.2)', () => {
  let adapter: HttpClientFetch;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        // Angular's XSRF interceptor needs `PlatformLocation`; a silently missing one would
        // hide XSRF failures from later specs that build on this provider set (ADR-0008 rule 1).
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    adapter = createHttpClientFetch(TestBed.inject(HttpClient));
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('forwards the method, URL and headers of a GET without a body', async () => {
    const pending = adapter(new Request(apiUrl('/runs'), { headers: { 'X-Request-Id': 'req-1' } }));

    const testRequest = await nextRequest(httpMock, apiUrl('/runs'));
    expect(testRequest.request.method).toBe('GET');
    const body: unknown = testRequest.request.body;
    expect(body).toBeNull();
    expect(testRequest.request.headers.get('X-Request-Id')).toBe('req-1');

    testRequest.flush(jsonBlob({ items: [], next_cursor: null }), {
      status: 200,
      statusText: 'OK',
      headers: { 'Content-Type': 'application/json' },
    });

    expect((await pending).status).toBe(200);
  });

  it('normalises a string URL plus init through Request', async () => {
    const text = JSON.stringify({ name: 'nightly' });
    const pending = adapter(apiUrl('/runs'), {
      method: 'POST',
      body: text,
      headers: { 'Content-Type': 'application/json' },
    });

    const testRequest = await nextRequest(httpMock, apiUrl('/runs'));
    expect(testRequest.request.method).toBe('POST');
    const body: unknown = testRequest.request.body;
    expect(body).toBe(text);

    testRequest.flush(jsonBlob({ name: 'nightly' }), {
      status: 201,
      statusText: 'Created',
      headers: { 'Content-Type': 'application/json' },
    });

    await pending;
  });

  it('sends the exact text body and Content-Type of a POST', async () => {
    const text = JSON.stringify({ name: 'nightly', environment: 'ci' });
    const pending = adapter(
      new Request(apiUrl('/runs'), {
        method: 'POST',
        body: text,
        headers: { 'Content-Type': 'application/json' },
      }),
    );

    const testRequest = await nextRequest(httpMock, apiUrl('/runs'));
    const body: unknown = testRequest.request.body;
    expect(body).toBe(text);
    expect(testRequest.request.headers.get('Content-Type')).toBe('application/json');

    testRequest.flush(jsonBlob({ name: 'nightly' }), {
      status: 200,
      statusText: 'OK',
      headers: { 'Content-Type': 'application/json' },
    });

    await pending;
  });

  it('converts a 200 JSON blob into a Response with the payload, status and headers', async () => {
    const payload = { items: [{ id: 'run-1' }], next_cursor: null };
    const pending = adapter(new Request(apiUrl('/runs')));

    (await nextRequest(httpMock, apiUrl('/runs'))).flush(jsonBlob(payload), {
      status: 200,
      statusText: 'OK',
      headers: { 'Content-Type': 'application/json', 'X-Request-Id': 'rid-1' },
    });

    const response = await pending;
    expect(response.status).toBe(200);
    expect(response.statusText).toBe('OK');
    expect(response.ok).toBe(true);
    expect(response.headers.get('X-Request-Id')).toBe('rid-1');
    const body: unknown = await response.json();
    expect(body).toEqual(payload);
  });

  it('returns a null body for a 204 without throwing', async () => {
    const pending = adapter(new Request(apiUrl('/runs/run-1/ack'), { method: 'DELETE' }));

    (await nextRequest(httpMock, apiUrl('/runs/run-1/ack'))).flush(null, {
      status: 204,
      statusText: 'No Content',
    });

    const response = await pending;
    expect(response.status).toBe(204);
    await expect(response.text()).resolves.toBe('');
  });

  it('turns a 403 envelope error into a Response the SDK can parse', async () => {
    const envelope: ErrorEnvelope = {
      error: {
        code: 'csrf_failed',
        message: 'The antiforgery token is missing.',
        request_id: 'req-403',
        retryable: false,
      },
    };
    const pending = adapter(new Request(apiUrl('/runs'), { method: 'POST' }));

    (await nextRequest(httpMock, apiUrl('/runs'))).flush(jsonBlob(envelope), {
      status: 403,
      statusText: 'Forbidden',
      headers: { 'Content-Type': 'application/json' },
    });

    const response = await pending;
    expect(response.status).toBe(403);
    expect(response.ok).toBe(false);
    const body: unknown = await response.json();
    expect(body).toEqual(envelope);
  });

  it('preserves the Retry-After header on a 429', async () => {
    const envelope: ErrorEnvelope = {
      error: {
        code: 'rate_limited',
        message: 'Too many requests.',
        request_id: 'req-429',
        retryable: true,
      },
    };
    const pending = adapter(new Request(apiUrl('/runs')));

    (await nextRequest(httpMock, apiUrl('/runs'))).flush(jsonBlob(envelope), {
      status: 429,
      statusText: 'Too Many Requests',
      headers: { 'Content-Type': 'application/json', 'Retry-After': '120' },
    });

    const response = await pending;
    expect(response.status).toBe(429);
    expect(response.headers.get('Retry-After')).toBe('120');
  });

  it('keeps a non-envelope text error body on a 502', async () => {
    const html = '<html><body>Bad Gateway</body></html>';
    const pending = adapter(new Request(apiUrl('/runs')));

    (await nextRequest(httpMock, apiUrl('/runs'))).flush(new Blob([html], { type: 'text/html' }), {
      status: 502,
      statusText: 'Bad Gateway',
      headers: { 'Content-Type': 'text/html' },
    });

    const response = await pending;
    expect(response.status).toBe(502);
    await expect(response.text()).resolves.toBe(html);
  });

  it('rejects a status 0 failure with a TypeError carrying the HttpErrorResponse', async () => {
    const pending = adapter(new Request(apiUrl('/runs')));

    (await nextRequest(httpMock, apiUrl('/runs'))).error(new ProgressEvent('error'));

    const error: unknown = await pending.catch((reason: unknown) => reason);
    expect(error).toBeInstanceOf(TypeError);
    const { cause } = error as TypeError;
    expect(cause).toBeInstanceOf(HttpErrorResponse);
  });

  it('rejects with an AbortError before sending an already-aborted request', async () => {
    const controller = new AbortController();
    controller.abort();

    const pending = adapter(new Request(apiUrl('/runs'), { signal: controller.signal }));

    const error: unknown = await pending.catch((reason: unknown) => reason);
    expect(error).toBeInstanceOf(DOMException);
    expect((error as DOMException).name).toBe('AbortError');
    httpMock.expectNone(apiUrl('/runs'));
  });

  it('cancels an in-flight request and rejects with an AbortError', async () => {
    const controller = new AbortController();
    const pending = adapter(new Request(apiUrl('/runs'), { signal: controller.signal }));
    const testRequest = await nextRequest(httpMock, apiUrl('/runs'));

    controller.abort();

    const error: unknown = await pending.catch((reason: unknown) => reason);
    expect(error).toBeInstanceOf(DOMException);
    expect((error as DOMException).name).toBe('AbortError');
    expect(testRequest.cancelled).toBe(true);
  });

  it("maps credentials 'include' to withCredentials true", async () => {
    const pending = adapter(new Request(apiUrl('/runs'), { credentials: 'include' }));
    const testRequest = await nextRequest(httpMock, apiUrl('/runs'));
    expect(testRequest.request.withCredentials).toBe(true);

    testRequest.flush(jsonBlob({ items: [], next_cursor: null }), {
      status: 200,
      statusText: 'OK',
      headers: { 'Content-Type': 'application/json' },
    });

    await pending;
  });

  it('keeps withCredentials false for the default credentials mode', async () => {
    const pending = adapter(new Request(apiUrl('/runs')));
    const testRequest = await nextRequest(httpMock, apiUrl('/runs'));
    expect(testRequest.request.withCredentials).toBe(false);

    testRequest.flush(null, { status: 204, statusText: 'No Content' });

    await pending;
  });

  it('forwards a caller-provided Idempotency-Key verbatim', async () => {
    const pending = adapter(
      new Request(apiUrl('/runs'), {
        method: 'POST',
        body: JSON.stringify({ name: 'nightly' }),
        headers: { 'Content-Type': 'application/json', 'Idempotency-Key': 'intent-42' },
      }),
    );

    const testRequest = await nextRequest(httpMock, apiUrl('/runs'));
    expect(testRequest.request.headers.get('Idempotency-Key')).toBe('intent-42');

    testRequest.flush(jsonBlob({ name: 'nightly' }), {
      status: 200,
      statusText: 'OK',
      headers: { 'Content-Type': 'application/json' },
    });

    await pending;
  });
});
