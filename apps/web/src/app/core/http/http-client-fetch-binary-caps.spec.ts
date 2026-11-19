import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import {
  HttpClient,
  HttpEventType,
  HttpHeaderResponse,
  HttpHeaders,
  provideHttpClient,
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
} from './binary-transport';
import { createHttpClientFetch, type HttpClientFetch } from './http-client-fetch';

const URL = 'http://localhost/v1/projects/project/authoring/import';
const OVER = BINARY_BODY_LIMIT_BYTES + 1;

async function nextRequest(httpMock: HttpTestingController): Promise<TestRequest> {
  for (let attempt = 0; attempt < 100; attempt += 1) {
    const [request] = httpMock.match(URL);
    if (request !== undefined) {
      return request;
    }
    await Promise.resolve();
  }
  throw new Error('The adapter has not issued the request.');
}

// `transport` is the marker of the call: request-only is what the XLSX import sends.
function call(transport: typeof BINARY_REQUEST | typeof BINARY_RESPONSE): Request {
  return new Request(URL, {
    method: 'POST',
    body: new Uint8Array([1, 2, 3]),
    headers: { [BINARY_TRANSPORT_HEADER]: transport },
  });
}

function rejection(pending: Promise<Response>): Promise<unknown> {
  return pending.catch((error: unknown) => error);
}

function expectLimit(error: unknown): void {
  expect(error).toBeInstanceOf(TypeError);
  expect((error as TypeError).message).toContain('exceeds limit');
}

function headerEvent(contentLength: string): HttpHeaderResponse {
  return new HttpHeaderResponse({ headers: new HttpHeaders({ 'Content-Length': contentLength }) });
}

describe('HttpClient fetch binary transport size caps', () => {
  let adapter: HttpClientFetch;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    adapter = createHttpClientFetch(TestBed.inject(HttpClient));
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    vi.restoreAllMocks();
  });

  describe('request stream chunks', () => {
    it.each([
      ['a string', 'text'],
      ['a number', 7],
      ['a plain object', { byteLength: 4 }],
      ['a typed array of another width', new Uint16Array(2)],
    ])('refuses %s chunk, cancels the reader and sends nothing', async (_name, chunk) => {
      const cancel = vi.fn(() => new Promise<void>(() => undefined));
      const stream = new ReadableStream<unknown>({
        start(source) {
          source.enqueue(chunk);
        },
        cancel,
      });
      const request = call(BINARY_REQUEST);
      vi.spyOn(request, 'body', 'get').mockReturnValue(
        stream as ReadableStream<Uint8Array<ArrayBuffer>>,
      );
      const error = await rejection(adapter(request));
      expect(error).toBeInstanceOf(TypeError);
      expect((error as TypeError).message).toContain('not bytes');
      expect(cancel).toHaveBeenCalledOnce();
      expect(stream.locked).toBe(false);
      httpMock.expectNone(URL);
    });
  });

  describe('request-only binary call reads a text reply', () => {
    it('refuses a reply over the cap that sent no progress', async () => {
      const settled = rejection(adapter(call(BINARY_REQUEST)));
      (await nextRequest(httpMock)).flush('x'.repeat(OVER));
      expectLimit(await settled);
    });

    it('cancels the transfer when progress crosses the cap', async () => {
      const settled = rejection(adapter(call(BINARY_REQUEST)));
      const test = await nextRequest(httpMock);
      test.event({ type: HttpEventType.DownloadProgress, loaded: OVER });
      expectLimit(await settled);
      expect(test.cancelled).toBe(true);
    });

    it('counts UTF-8 bytes, not characters, of a reply', async () => {
      const settled = rejection(adapter(call(BINARY_REQUEST)));
      // Fewer characters than the cap, more bytes than the cap.
      (await nextRequest(httpMock)).flush('é'.repeat(BINARY_BODY_LIMIT_BYTES / 2 + 1));
      expectLimit(await settled);
    });

    it('refuses an error body over the cap', async () => {
      const settled = rejection(adapter(call(BINARY_REQUEST)));
      (await nextRequest(httpMock)).flush('x'.repeat(OVER), {
        status: 413,
        statusText: 'Too Large',
      });
      expectLimit(await settled);
    });

    it('still forwards a normal reply and a normal error body', async () => {
      const ok = adapter(call(BINARY_REQUEST));
      (await nextRequest(httpMock)).flush('{"ok":true}', {
        headers: { 'Content-Type': 'application/json' },
      });
      await expect((await ok).text()).resolves.toBe('{"ok":true}');

      const refused = adapter(call(BINARY_REQUEST));
      (await nextRequest(httpMock)).flush('{"error":{"code":"bad"}}', {
        status: 422,
        statusText: 'Rejected',
      });
      const response = await refused;
      expect(response.status).toBe(422);
      await expect(response.text()).resolves.toBe('{"error":{"code":"bad"}}');
    });
  });

  describe('a Content-Length that disagrees with the body', () => {
    it('refuses a body over the cap when Content-Length is smaller and no progress is reported', async () => {
      const settled = rejection(adapter(call(BINARY_RESPONSE)));
      const test = await nextRequest(httpMock);
      test.event(headerEvent('10'));
      test.flush(new ArrayBuffer(OVER));
      expectLimit(await settled);
    });

    it('cancels on progress over the cap although Content-Length is smaller', async () => {
      const settled = rejection(adapter(call(BINARY_RESPONSE)));
      const test = await nextRequest(httpMock);
      test.event(headerEvent('10'));
      test.event({ type: HttpEventType.DownloadProgress, loaded: OVER });
      expectLimit(await settled);
      expect(test.cancelled).toBe(true);
    });

    it.each(['abc', '', '0x10', '1e9', '-5'])(
      'ignores the non-numeric Content-Length %j and lets the real size decide',
      async (declared) => {
        const accepted = adapter(call(BINARY_RESPONSE));
        const first = await nextRequest(httpMock);
        first.event(headerEvent(declared));
        first.flush(new ArrayBuffer(4));
        expect((await (await accepted).arrayBuffer()).byteLength).toBe(4);

        const refused = rejection(adapter(call(BINARY_RESPONSE)));
        const second = await nextRequest(httpMock);
        second.event(headerEvent(declared));
        second.flush(new ArrayBuffer(OVER));
        expectLimit(await refused);
      },
    );
  });
});
