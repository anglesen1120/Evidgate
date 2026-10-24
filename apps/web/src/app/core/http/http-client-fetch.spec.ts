// 09.1.1-T3.2 (ADR-0008 decision row a + adapter gotchas), extended by 09.1.1-T8: specs for the
// `fetch` adapter that puts the generated SDK behind Angular `HttpClient`. They pin what the
// interceptors see (method, absolute URL, forwarded headers, exact text body, withCredentials),
// what the SDK gets back (a fetch `Response` for 2xx and for 00 §9.2 error statuses, `TypeError`
// for status 0, for a non-text 2xx body, for a status a `Response` cannot carry, for a 2xx
// `HttpErrorResponse` and for a non-text request body), what the deadline does (a `TimeoutError`
// rejection with the request cancelled, while a caller abort keeps winning) and that responses
// are read as text, never as a `Blob`.

import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import {
  HttpClient,
  HttpErrorResponse,
  HttpResponse,
  provideHttpClient,
} from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ErrorEnvelope } from '@evidgate/sdk';

import {
  createHttpClientFetch,
  DEFAULT_REQUEST_TIMEOUT_MS,
  type HttpClientFetch,
} from './http-client-fetch';

const API_ORIGIN = 'http://localhost';

// Node's `Request` rejects a relative URL, whereas the SDK's `/v1` baseUrl resolves against the
// page origin in a browser; the specs therefore use the absolute form of the same URL.
function apiUrl(path: string): string {
  return `${API_ORIGIN}/v1${path}`;
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

    testRequest.flush(JSON.stringify({ items: [], next_cursor: null }), {
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

    testRequest.flush(JSON.stringify({ name: 'nightly' }), {
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

    testRequest.flush(JSON.stringify({ name: 'nightly' }), {
      status: 200,
      statusText: 'OK',
      headers: { 'Content-Type': 'application/json' },
    });

    await pending;
  });

  it('refuses a non-text request body before it reaches HttpClient', async () => {
    const bytes = new Uint8Array([0x89, 0x50, 0x4e, 0x47]);
    const pending = adapter(
      new Request(apiUrl('/artifacts'), {
        method: 'POST',
        body: bytes,
        headers: { 'Content-Type': 'application/octet-stream' },
      }),
    );

    const error: unknown = await pending.catch((reason: unknown) => reason);
    expect(error).toBeInstanceOf(TypeError);
    httpMock.expectNone(apiUrl('/artifacts'));
  });

  it('forwards an application/x-www-form-urlencoded body as text', async () => {
    const text = 'grant_type=client_credentials&scope=runs%3Aread';
    const pending = adapter(
      new Request(apiUrl('/oauth/token'), {
        method: 'POST',
        body: text,
        headers: { 'Content-Type': 'application/x-www-form-urlencoded; charset=utf-8' },
      }),
    );

    const testRequest = await nextRequest(httpMock, apiUrl('/oauth/token'));
    const body: unknown = testRequest.request.body;
    expect(body).toBe(text);

    testRequest.flush(JSON.stringify({ access_token: 'token-1' }), {
      status: 200,
      statusText: 'OK',
      headers: { 'Content-Type': 'application/json' },
    });

    const response = await pending;
    expect(response.status).toBe(200);
  });

  it('converts a 200 JSON text body into a Response with body, status, headers', async () => {
    const payload = { items: [{ id: 'run-1' }], next_cursor: null };
    const pending = adapter(new Request(apiUrl('/runs')));

    (await nextRequest(httpMock, apiUrl('/runs'))).flush(JSON.stringify(payload), {
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

    (await nextRequest(httpMock, apiUrl('/runs'))).flush(JSON.stringify(envelope), {
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

    (await nextRequest(httpMock, apiUrl('/runs'))).flush(JSON.stringify(envelope), {
      status: 429,
      statusText: 'Too Many Requests',
      headers: { 'Content-Type': 'application/json', 'Retry-After': '120' },
    });

    const response = await pending;
    expect(response.status).toBe(429);
    expect(response.headers.get('Retry-After')).toBe('120');
  });

  it('joins a repeated response header instead of dropping a value', async () => {
    const pending = adapter(new Request(apiUrl('/runs')));

    (await nextRequest(httpMock, apiUrl('/runs'))).flush(JSON.stringify({ items: [] }), {
      status: 200,
      statusText: 'OK',
      headers: { 'Content-Type': 'application/json', 'X-Trace-Id': ['span-1', 'span-2'] },
    });

    const response = await pending;
    expect(response.headers.get('X-Trace-Id')).toBe('span-1, span-2');
  });

  it('keeps a non-envelope text error body on a 502', async () => {
    const html = '<html><body>Bad Gateway</body></html>';
    const pending = adapter(new Request(apiUrl('/runs')));

    (await nextRequest(httpMock, apiUrl('/runs'))).flush(html, {
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

  it('rejects a 101 status instead of letting Response throw a RangeError', async () => {
    const pending = adapter(new Request(apiUrl('/runs')));

    // A non-2xx status reaches the adapter as an `HttpErrorResponse`; 101 cannot become a fetch
    // `Response` (its constructor only accepts 200-599).
    (await nextRequest(httpMock, apiUrl('/runs'))).flush('', {
      status: 101,
      statusText: 'Switching Protocols',
    });

    const error: unknown = await pending.catch((reason: unknown) => reason);
    expect(error).toBeInstanceOf(TypeError);
    expect(error).not.toBeInstanceOf(RangeError);
    const { cause } = error as TypeError;
    expect(cause).toBeInstanceOf(HttpErrorResponse);
    expect((cause as HttpErrorResponse).status).toBe(101);
  });

  it('rejects a response event whose status is below 200', async () => {
    const pending = adapter(new Request(apiUrl('/runs')));
    const testRequest = await nextRequest(httpMock, apiUrl('/runs'));

    // `event` is how a non-error `HttpResponse` reaches the conversion without flush's 2xx
    // filter; a 1xx there must not surface as the constructor's `RangeError`.
    testRequest.event(
      new HttpResponse({ body: null, status: 101, statusText: 'Switching Protocols' }),
    );

    const error: unknown = await pending.catch((reason: unknown) => reason);
    expect(error).toBeInstanceOf(TypeError);
    expect(error).not.toBeInstanceOf(RangeError);
    const { cause } = error as TypeError;
    expect(cause).toBeInstanceOf(HttpResponse);
    expect((cause as HttpResponse<unknown>).status).toBe(101);
  });

  it('rejects a 2xx HttpErrorResponse instead of returning it as a success', async () => {
    const pending = adapter(new Request(apiUrl('/runs')));

    (await nextRequest(httpMock, apiUrl('/runs'))).error(new ProgressEvent('error'), {
      status: 200,
    });

    const error: unknown = await pending.catch((reason: unknown) => reason);
    expect(error).toBeInstanceOf(TypeError);
    const { cause } = error as TypeError;
    expect(cause).toBeInstanceOf(HttpErrorResponse);
    expect((cause as HttpErrorResponse).status).toBe(200);
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

    testRequest.flush(JSON.stringify({ items: [], next_cursor: null }), {
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

    testRequest.flush(JSON.stringify({ name: 'nightly' }), {
      status: 200,
      statusText: 'OK',
      headers: { 'Content-Type': 'application/json' },
    });

    await pending;
  });

  // 09.1.1-T8 deadline specs. The clock is faked for `setTimeout`/`clearTimeout` only, so the
  // microtask pipeline (`request.text()`, rxjs and the testing backend) keeps running normally.
  // Requests are taken by draining microtasks instead of `vi.waitFor`, because `waitFor` owns
  // the timers that these specs advance.
  describe('09.1.1-T8 deadline', () => {
    async function takeRequest(url: string): Promise<TestRequest> {
      for (let attempt = 0; attempt < 50; attempt += 1) {
        const [request] = httpMock.match(url);
        if (request !== undefined) {
          return request;
        }
        await Promise.resolve();
      }
      throw new Error(`The adapter has not issued ${url} yet.`);
    }

    afterEach(() => {
      vi.useRealTimers();
    });

    it('rejects with a TimeoutError and cancels the request', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      adapter = createHttpClientFetch(TestBed.inject(HttpClient), { timeoutMs: 1_000 });

      const pending = adapter(new Request(apiUrl('/runs')));
      const settlement = pending.then(
        () => 'resolved',
        (reason: unknown) => reason,
      );
      const testRequest = await takeRequest(apiUrl('/runs'));

      await vi.advanceTimersByTimeAsync(1_000);

      const error = await settlement;
      expect(error).toBeInstanceOf(DOMException);
      expect((error as DOMException).name).toBe('TimeoutError');
      // The timeout unsubscribed the request, so a slow server cannot deliver a late success.
      expect(testRequest.cancelled).toBe(true);
    });

    it('leaves a response that arrives before the deadline untouched', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      adapter = createHttpClientFetch(TestBed.inject(HttpClient), { timeoutMs: 1_000 });

      const pending = adapter(new Request(apiUrl('/runs')));
      const testRequest = await takeRequest(apiUrl('/runs'));
      await vi.advanceTimersByTimeAsync(999);
      testRequest.flush(JSON.stringify({ items: [] }), {
        status: 200,
        statusText: 'OK',
        headers: { 'Content-Type': 'application/json' },
      });

      const response = await pending;
      expect(response.status).toBe(200);
      // The finished call leaves no deadline behind: advancing past it changes nothing.
      await vi.advanceTimersByTimeAsync(10_000);
      await expect(response.text()).resolves.toBe(JSON.stringify({ items: [] }));
    });

    it('keeps a caller abort as an AbortError when the deadline also passes', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      adapter = createHttpClientFetch(TestBed.inject(HttpClient), { timeoutMs: 1_000 });
      const controller = new AbortController();

      const pending = adapter(new Request(apiUrl('/runs'), { signal: controller.signal }));
      const settlement = pending.then(
        () => 'resolved',
        (reason: unknown) => reason,
      );
      const testRequest = await takeRequest(apiUrl('/runs'));

      // The abort fires before the deadline, in the same advance that reaches 1000 ms.
      setTimeout(() => controller.abort(), 500);
      await vi.advanceTimersByTimeAsync(1_000);

      const error = await settlement;
      expect(error).toBeInstanceOf(DOMException);
      expect((error as DOMException).name).toBe('AbortError');
      expect(testRequest.cancelled).toBe(true);
    });

    it('rejects with a TimeoutError while the request body is still being read', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      adapter = createHttpClientFetch(TestBed.inject(HttpClient), { timeoutMs: 1_000 });

      // A stalled upload: the platform body read never settles, so only the deadline can end
      // the call. `Request.text()` is the one read the adapter performs on a request body.
      const request = new Request(apiUrl('/runs'), {
        method: 'POST',
        body: '{"name":"nightly"}',
        headers: { 'Content-Type': 'application/json' },
      });
      vi.spyOn(request, 'text').mockReturnValue(new Promise<string>(() => undefined));

      const settlement = adapter(request).then(
        () => 'resolved',
        (reason: unknown) => reason,
      );

      await vi.advanceTimersByTimeAsync(1_000);

      const error = await settlement;
      expect(error).toBeInstanceOf(DOMException);
      expect((error as DOMException).name).toBe('TimeoutError');
      // The deadline ends the call before anything is sent, so no half-read body reaches the API.
      httpMock.expectNone(apiUrl('/runs'));
    });

    it('does not start a deadline when the caller disables it', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      adapter = createHttpClientFetch(TestBed.inject(HttpClient), { timeoutMs: 0 });

      const pending = adapter(new Request(apiUrl('/runs')));
      const testRequest = await takeRequest(apiUrl('/runs'));
      await vi.advanceTimersByTimeAsync(DEFAULT_REQUEST_TIMEOUT_MS * 2);

      testRequest.flush(JSON.stringify({ items: [] }), {
        status: 200,
        statusText: 'OK',
        headers: { 'Content-Type': 'application/json' },
      });
      await expect(pending).resolves.toHaveProperty('status', 200);
    });

    it('surfaces a caller abort during a stalled body read as an AbortError', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      adapter = createHttpClientFetch(TestBed.inject(HttpClient), { timeoutMs: 1_000 });
      const controller = new AbortController();
      const request = new Request(apiUrl('/runs'), {
        method: 'POST',
        body: '{"name":"nightly"}',
        headers: { 'Content-Type': 'application/json' },
        signal: controller.signal,
      });
      // The platform body read never settles, so only the signal can end the call.
      vi.spyOn(request, 'text').mockReturnValue(new Promise<string>(() => undefined));

      const settlement = adapter(request).then(
        () => 'resolved',
        (reason: unknown) => reason,
      );
      controller.abort();

      const error = await settlement;
      expect(error).toBeInstanceOf(DOMException);
      expect((error as DOMException).name).toBe('AbortError');
      // Nothing is sent for a half-read body, and the 1 s deadline never had to fire.
      httpMock.expectNone(apiUrl('/runs'));
    });

    it('surfaces that abort even when the deadline is disabled', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      adapter = createHttpClientFetch(TestBed.inject(HttpClient), { timeoutMs: 0 });
      const controller = new AbortController();
      const request = new Request(apiUrl('/runs'), {
        method: 'POST',
        body: '{"name":"nightly"}',
        headers: { 'Content-Type': 'application/json' },
        signal: controller.signal,
      });
      vi.spyOn(request, 'text').mockReturnValue(new Promise<string>(() => undefined));

      const settlement = adapter(request).then(
        () => 'resolved',
        (reason: unknown) => reason,
      );
      controller.abort();

      const error = await settlement;
      expect(error).toBeInstanceOf(DOMException);
      expect((error as DOMException).name).toBe('AbortError');
      httpMock.expectNone(apiUrl('/runs'));
    });

    it('leaves no deadline timer or abort listener behind once the call settles', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      adapter = createHttpClientFetch(TestBed.inject(HttpClient), { timeoutMs: 1_000 });
      const controller = new AbortController();
      const request = new Request(apiUrl('/runs'), {
        method: 'POST',
        body: '{"name":"nightly"}',
        headers: { 'Content-Type': 'application/json' },
        signal: controller.signal,
      });
      const removed = vi.spyOn(request.signal, 'removeEventListener');

      const pending = adapter(request);
      const testRequest = await takeRequest(apiUrl('/runs'));
      // The body read finished, so its abort listener was released before the request went out.
      expect(removed).toHaveBeenCalledWith('abort', expect.any(Function));

      testRequest.flush(JSON.stringify({ run_id: 'run-1' }), {
        status: 201,
        statusText: 'Created',
        headers: { 'Content-Type': 'application/json' },
      });
      await expect(pending).resolves.toHaveProperty('status', 201);
      // The deadline is the adapter's only timer, and a settled call leaves none behind.
      expect(vi.getTimerCount()).toBe(0);
    });
  });

  // 09.1.1-T8: responses travel as text. These rows are the round-trip proof for every body
  // shape the SDK parses, plus the refusal that keeps a binary 2xx from becoming a success.
  describe('09.1.1-T8 text responses', () => {
    it('keeps a 412 envelope with details parseable through the text body', async () => {
      const envelope: ErrorEnvelope = {
        error: {
          code: 'precondition_failed',
          message: 'The row changed since it was read.',
          request_id: 'req-412',
          retryable: false,
          details: [{ path: '/revision_id', reason: 'stale' }],
        },
      };
      const pending = adapter(new Request(apiUrl('/runs'), { method: 'POST' }));

      (await nextRequest(httpMock, apiUrl('/runs'))).flush(JSON.stringify(envelope), {
        status: 412,
        statusText: 'Precondition Failed',
        headers: { 'Content-Type': 'application/json' },
      });

      const response = await pending;
      expect(response.status).toBe(412);
      expect(response.ok).toBe(false);
      const body: unknown = await response.json();
      expect(body).toEqual(envelope);
    });

    it('passes a non-JSON text error body through unchanged', async () => {
      const text = 'upstream worker crashed';
      const pending = adapter(new Request(apiUrl('/runs')));

      (await nextRequest(httpMock, apiUrl('/runs'))).flush(text, {
        status: 500,
        statusText: 'Internal Server Error',
        headers: { 'Content-Type': 'text/plain; charset=utf-8' },
      });

      const response = await pending;
      expect(response.status).toBe(500);
      await expect(response.text()).resolves.toBe(text);
    });

    it('keeps a non-text error body as text, because the SDK reports it verbatim', async () => {
      const text = '\u0000\u0001 worker trace';
      const pending = adapter(new Request(apiUrl('/runs')));

      (await nextRequest(httpMock, apiUrl('/runs'))).flush(text, {
        status: 500,
        statusText: 'Internal Server Error',
        headers: { 'Content-Type': 'application/octet-stream' },
      });

      const response = await pending;
      expect(response.status).toBe(500);
      await expect(response.text()).resolves.toBe(text);
    });

    it('returns an empty body for an empty 200 without inventing content', async () => {
      const pending = adapter(new Request(apiUrl('/runs')));

      (await nextRequest(httpMock, apiUrl('/runs'))).flush('', {
        status: 200,
        statusText: 'OK',
        headers: { 'Content-Type': 'application/json' },
      });

      const response = await pending;
      expect(response.status).toBe(200);
      await expect(response.text()).resolves.toBe('');
      await expect(response.json()).rejects.toBeInstanceOf(Error);
    });

    it.each(['image/png', 'application/octet-stream'])(
      'refuses a 2xx whose Content-Type is %s instead of a corrupted success',
      async (contentType) => {
        const binary = '\u0089PNG\r\n\u001a\n';
        const pending = adapter(new Request(apiUrl('/artifacts/a-1/download')));

        (await nextRequest(httpMock, apiUrl('/artifacts/a-1/download'))).flush(binary, {
          status: 200,
          statusText: 'OK',
          headers: { 'Content-Type': contentType },
        });

        const error: unknown = await pending.catch((reason: unknown) => reason);
        expect(error).toBeInstanceOf(TypeError);
        expect((error as TypeError).message).toContain('non-text');
      },
    );

    it('keeps a JSON 2xx parseable and a 204 bodyless', async () => {
      const created = adapter(new Request(apiUrl('/runs'), { method: 'POST' }));
      (await nextRequest(httpMock, apiUrl('/runs'))).flush(JSON.stringify({ run_id: 'run-1' }), {
        status: 201,
        statusText: 'Created',
        headers: { 'Content-Type': 'application/json' },
      });
      const createdResponse = await created;
      expect(createdResponse.status).toBe(201);
      const body: unknown = await createdResponse.json();
      expect(body).toEqual({ run_id: 'run-1' });

      const acked = adapter(new Request(apiUrl('/runs/run-1'), { method: 'DELETE' }));
      (await nextRequest(httpMock, apiUrl('/runs/run-1'))).flush(null, {
        status: 204,
        statusText: 'No Content',
      });
      const ackedResponse = await acked;
      expect(ackedResponse.status).toBe(204);
      expect(ackedResponse.body).toBeNull();
    });
  });
});
