import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import {
  HttpClient,
  HttpEventType,
  HttpHeaderResponse,
  HttpHeaders,
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import {
  BINARY_BODY_LIMIT_BYTES,
  BINARY_REQUEST,
  BINARY_RESPONSE,
  BINARY_TRANSPORT_HEADER,
  binaryTransport,
} from './binary-transport';
import { createHttpClientFetch, type HttpClientFetch } from './http-client-fetch';

const URL = 'http://localhost/v1/projects/project/authoring/export';
const MIME = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
const BYTES = new Uint8Array([0xff, 0xfe, 0x00, 0x80]);

async function nextRequest(httpMock: HttpTestingController): Promise<TestRequest> {
  for (let attempt = 0; attempt < 100; attempt += 1) {
    const [request] = httpMock.match(URL);
    if (request !== undefined) {
      return request;
    }
    await Promise.resolve();
  }
  throw new Error('The adapter has not issued the binary request.');
}

function binaryRequest(init: RequestInit = {}): Request {
  const headers = new Headers(init.headers);
  if (!headers.has(BINARY_TRANSPORT_HEADER)) {
    headers.set(BINARY_TRANSPORT_HEADER, binaryTransport(BINARY_RESPONSE));
  }
  return new Request(URL, { ...init, headers });
}

function rejection(pending: Promise<Response>): Promise<unknown> {
  return pending.catch((error: unknown) => error);
}

function expectLimit(error: unknown): void {
  expect(error).toBeInstanceOf(TypeError);
  expect((error as TypeError).message).toContain('exceeds limit');
}

// The abort and deadline failures are `DOMException`s whose `name` lives on the prototype, so
// they are asserted by identity and name, never by an object shape.
function expectNamedError(error: unknown, name: 'AbortError' | 'TimeoutError'): void {
  expect(error).toBeInstanceOf(DOMException);
  expect((error as DOMException).name).toBe(name);
}

// The test environment's `TextEncoder` can belong to another realm, and its `buffer` then fails
// the backend's `instanceof ArrayBuffer` check; the bytes are copied into a buffer of this realm.
function utf8ArrayBuffer(text: string): ArrayBuffer {
  const bytes = new TextEncoder().encode(text);
  const buffer = new ArrayBuffer(bytes.byteLength);
  new Uint8Array(buffer).set(bytes);
  return buffer;
}

describe('HttpClient fetch binary transport', () => {
  let adapter: HttpClientFetch;
  let httpMock: HttpTestingController;
  let interceptorHeaders: HttpHeaders | undefined;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(
          withInterceptors([
            (request, next) => {
              interceptorHeaders = request.headers;
              return next(request);
            },
          ]),
        ),
        provideHttpClientTesting(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    adapter = createHttpClientFetch(TestBed.inject(HttpClient));
    httpMock = TestBed.inject(HttpTestingController);
    interceptorHeaders = undefined;
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('sends exact binary bytes without changing content type, credentials or intent headers', async () => {
    const request = binaryRequest({
      method: 'POST',
      body: BYTES,
      credentials: 'include',
      headers: {
        [BINARY_TRANSPORT_HEADER]: binaryTransport(BINARY_REQUEST),
        'Content-Type': MIME,
        'Idempotency-Key': 'import-intent',
      },
    });
    const textRead = vi.spyOn(request, 'text');
    const pending = adapter(request);
    const test = await nextRequest(httpMock);
    const body: unknown = test.request.body;
    expect(body).toBeInstanceOf(ArrayBuffer);
    expect(new Uint8Array(body as ArrayBuffer)).toEqual(BYTES);
    expect(textRead).not.toHaveBeenCalled();
    expect(test.request.headers.get('Content-Type')).toBe(MIME);
    expect(test.request.headers.get('Idempotency-Key')).toBe('import-intent');
    expect(test.request.withCredentials).toBe(true);
    expect(test.request.responseType).toBe('text');
    expect(test.request.headers.has(BINARY_TRANSPORT_HEADER)).toBe(false);
    expect(interceptorHeaders?.has(BINARY_TRANSPORT_HEADER)).toBe(false);
    test.flush('{"ok":true}', { headers: { 'Content-Type': 'application/json' } });
    await expect((await pending).json()).resolves.toEqual({ ok: true });
  });

  it('round trips invalid UTF-8 bytes with both case-insensitive comma-list modes', async () => {
    const pending = adapter(
      binaryRequest({
        method: 'POST',
        body: BYTES,
        headers: {
          [BINARY_TRANSPORT_HEADER.toLowerCase()]: ' BINARY-REQUEST, Binary-Response ',
          'Content-Type': MIME,
        },
      }),
    );
    const test = await nextRequest(httpMock);
    expect(test.request.responseType).toBe('arraybuffer');
    expect(test.request.reportProgress).toBe(true);
    expect(new Uint8Array(test.request.body as ArrayBuffer)).toEqual(BYTES);
    expect(test.request.headers.has(BINARY_TRANSPORT_HEADER)).toBe(false);
    test.flush(BYTES.buffer, {
      headers: {
        'Content-Type': MIME,
        'Content-Disposition': 'attachment; filename=authoring-export.xlsx',
        'Cache-Control': 'no-store',
      },
    });
    const response = await pending;
    expect(response.status).toBe(200);
    expect(response.headers.get('Content-Disposition')).toContain('authoring-export.xlsx');
    expect(response.headers.get('Cache-Control')).toBe('no-store');
    expect(new Uint8Array(await response.arrayBuffer())).toEqual(BYTES);
  });

  it('sends an empty binary request body as null', async () => {
    const pending = adapter(
      binaryRequest({
        method: 'POST',
        body: new ArrayBuffer(0),
        headers: { [BINARY_TRANSPORT_HEADER]: BINARY_REQUEST },
      }),
    );
    const test = await nextRequest(httpMock);
    expect(test.request.body).toBeNull();
    test.flush(null, { status: 204, statusText: 'No Content' });
    expect((await pending).status).toBe(204);
  });

  it.each(['unknown', 'binary-response,unknown', '', 'binary-request,'])(
    'refuses unknown marker %j before sending',
    async (marker) => {
      const error = await rejection(
        adapter(binaryRequest({ headers: { [BINARY_TRANSPORT_HEADER]: marker } })),
      );
      expect(error).toBeInstanceOf(TypeError);
      httpMock.expectNone(URL);
    },
  );

  it('refuses a binary request over the cap before sending', async () => {
    const error = await rejection(
      adapter(
        binaryRequest({
          method: 'POST',
          body: new ArrayBuffer(BINARY_BODY_LIMIT_BYTES + 1),
          headers: { [BINARY_TRANSPORT_HEADER]: BINARY_REQUEST, 'Content-Type': MIME },
        }),
      ),
    );
    expectLimit(error);
    httpMock.expectNone(URL);
  });

  it('allows a binary request exactly at the cap', async () => {
    const pending = adapter(
      binaryRequest({
        method: 'POST',
        body: new ArrayBuffer(BINARY_BODY_LIMIT_BYTES),
        headers: { [BINARY_TRANSPORT_HEADER]: BINARY_REQUEST, 'Content-Type': MIME },
      }),
    );
    const test = await nextRequest(httpMock);
    expect((test.request.body as ArrayBuffer).byteLength).toBe(BINARY_BODY_LIMIT_BYTES);
    test.flush(null, { status: 204, statusText: 'No Content' });
    expect((await pending).status).toBe(204);
  });

  it('allows response bytes, progress and Content-Length exactly at the cap', async () => {
    const pending = adapter(binaryRequest());
    const test = await nextRequest(httpMock);
    test.event(
      new HttpHeaderResponse({
        headers: new HttpHeaders({ 'Content-Length': String(BINARY_BODY_LIMIT_BYTES) }),
      }),
    );
    test.event({ type: HttpEventType.DownloadProgress, loaded: BINARY_BODY_LIMIT_BYTES });
    test.flush(new ArrayBuffer(BINARY_BODY_LIMIT_BYTES), { headers: { 'Content-Type': MIME } });
    expect((await (await pending).arrayBuffer()).byteLength).toBe(BINARY_BODY_LIMIT_BYTES);
  });

  it('refuses an oversized response even without progress events', async () => {
    const settled = rejection(adapter(binaryRequest()));
    (await nextRequest(httpMock)).flush(new ArrayBuffer(BINARY_BODY_LIMIT_BYTES + 1));
    expectLimit(await settled);
  });

  it('cancels when download progress crosses the cap before a complete body arrives', async () => {
    const settled = rejection(adapter(binaryRequest()));
    const test = await nextRequest(httpMock);
    test.event({ type: HttpEventType.DownloadProgress, loaded: BINARY_BODY_LIMIT_BYTES + 1 });
    expectLimit(await settled);
    expect(test.cancelled).toBe(true);
  });

  it('cancels when response headers advertise an oversized body', async () => {
    const settled = rejection(adapter(binaryRequest()));
    const test = await nextRequest(httpMock);
    test.event(
      new HttpHeaderResponse({
        headers: new HttpHeaders({ 'Content-Length': String(BINARY_BODY_LIMIT_BYTES + 1) }),
      }),
    );
    expectLimit(await settled);
    expect(test.cancelled).toBe(true);
  });

  it.each([422, 413])(
    'returns a binary %i JSON error envelope with response headers',
    async (status) => {
      const envelope = {
        error: {
          code: 'validation_failed',
          message: 'Invalid file',
          request_id: 'request',
          retryable: false,
          details: [{ path: '/file', reason: 'body_too_large' }],
        },
      };
      const pending = adapter(binaryRequest());
      (await nextRequest(httpMock)).flush(utf8ArrayBuffer(JSON.stringify(envelope)), {
        status,
        statusText: 'Rejected',
        headers: {
          'Content-Type': 'application/json',
          'Retry-After': '10',
          'X-Trace': ['a', 'b'],
        },
      });
      const response = await pending;
      expect(response.status).toBe(status);
      expect(response.statusText).toBe('Rejected');
      expect(response.headers.get('Retry-After')).toBe('10');
      expect(response.headers.get('X-Trace')).toBe('a, b');
      await expect(response.json()).resolves.toEqual(envelope);
    },
  );

  it('decodes an invalid UTF-8 error body non-fatally', async () => {
    const pending = adapter(binaryRequest());
    (await nextRequest(httpMock)).flush(BYTES.buffer, { status: 422, statusText: 'Rejected' });
    await expect((await pending).text()).resolves.toBe(new TextDecoder().decode(BYTES));
  });

  it('bounds binary error bodies before UTF-8 decoding', async () => {
    const settled = rejection(adapter(binaryRequest()));
    (await nextRequest(httpMock)).flush(new ArrayBuffer(BINARY_BODY_LIMIT_BYTES + 1), {
      status: 413,
      statusText: 'Too Large',
    });
    expectLimit(await settled);
  });

  it('accepts a binary error body exactly at the cap', async () => {
    const pending = adapter(binaryRequest());
    (await nextRequest(httpMock)).flush(new ArrayBuffer(BINARY_BODY_LIMIT_BYTES), {
      status: 413,
      statusText: 'Too Large',
    });
    expect((await (await pending).text()).length).toBe(BINARY_BODY_LIMIT_BYTES);
  });

  it('returns no body for binary 204 responses', async () => {
    const pending = adapter(binaryRequest());
    (await nextRequest(httpMock)).flush(null, { status: 204, statusText: 'No Content' });
    expect((await pending).body).toBeNull();
  });

  it('still refuses non-text requests when only the response is opted in', async () => {
    const settled = rejection(
      adapter(binaryRequest({ method: 'POST', body: BYTES, headers: { 'Content-Type': MIME } })),
    );
    expect(await settled).toBeInstanceOf(TypeError);
    httpMock.expectNone(URL);
  });

  it('cancels an in-flight binary download on caller abort', async () => {
    const controller = new AbortController();
    const settled = rejection(adapter(binaryRequest({ signal: controller.signal })));
    const test = await nextRequest(httpMock);
    controller.abort();
    expectNamedError(await settled, 'AbortError');
    expect(test.cancelled).toBe(true);
  });

  it.each([0, 1000])(
    'aborts while the binary request read stalls with timeout %i',
    async (timeoutMs) => {
      adapter = createHttpClientFetch(TestBed.inject(HttpClient), { timeoutMs });
      const controller = new AbortController();
      const request = binaryRequest({
        method: 'POST',
        body: BYTES,
        signal: controller.signal,
        headers: { [BINARY_TRANSPORT_HEADER]: BINARY_REQUEST },
      });
      vi.spyOn(request, 'arrayBuffer').mockReturnValue(new Promise<ArrayBuffer>(() => undefined));
      const settled = rejection(adapter(request));
      controller.abort();
      expectNamedError(await settled, 'AbortError');
      httpMock.expectNone(URL);
    },
  );

  it('times out and cancels an in-flight binary download', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    adapter = createHttpClientFetch(TestBed.inject(HttpClient), { timeoutMs: 1000 });
    const settled = rejection(adapter(binaryRequest()));
    const test = await nextRequest(httpMock);
    await vi.advanceTimersByTimeAsync(1000);
    expectNamedError(await settled, 'TimeoutError');
    expect(test.cancelled).toBe(true);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('times out while the binary request read stalls', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    adapter = createHttpClientFetch(TestBed.inject(HttpClient), { timeoutMs: 1000 });
    const request = binaryRequest({
      method: 'POST',
      body: BYTES,
      headers: { [BINARY_TRANSPORT_HEADER]: BINARY_REQUEST },
    });
    vi.spyOn(request, 'arrayBuffer').mockReturnValue(new Promise<ArrayBuffer>(() => undefined));
    const settled = rejection(adapter(request));
    await vi.advanceTimersByTimeAsync(1000);
    expectNamedError(await settled, 'TimeoutError');
    httpMock.expectNone(URL);
  });

  it('keeps a caller abort authoritative when the deadline also expires', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    adapter = createHttpClientFetch(TestBed.inject(HttpClient), { timeoutMs: 1000 });
    const controller = new AbortController();
    const settled = rejection(adapter(binaryRequest({ signal: controller.signal })));
    const test = await nextRequest(httpMock);
    controller.abort();
    await vi.advanceTimersByTimeAsync(1000);
    expectNamedError(await settled, 'AbortError');
    expect(test.cancelled).toBe(true);
  });
});
