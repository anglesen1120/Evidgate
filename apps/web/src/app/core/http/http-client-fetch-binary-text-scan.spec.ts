// A reply of a request-only binary call is measured once, by the streaming cap, not again by the
// adapter. The scan reads each UTF-16 unit with `charCodeAt`, so its calls count the scans.
import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpClient, provideHttpClient } from '@angular/common/http';
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
  BINARY_TRANSPORT_HEADER,
} from './binary-transport';
import { createHttpClientFetch, type HttpClientFetch } from './http-client-fetch';

const URL = 'http://localhost/v1/projects/project/authoring/import';

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

function requestOnly(): Request {
  return new Request(URL, {
    method: 'POST',
    body: new Uint8Array([1]),
    headers: { [BINARY_TRANSPORT_HEADER]: BINARY_REQUEST },
  });
}

// A counting wrapper, not a spy: a spy would record millions of calls.
async function countScannedUnits(run: () => Promise<unknown>): Promise<number> {
  const descriptor = Object.getOwnPropertyDescriptor(String.prototype, 'charCodeAt') ?? {};
  const original = Reflect.get(descriptor, 'value') as (this: string, index: number) => number;
  let units = 0;
  String.prototype.charCodeAt = function (this: string, index: number): number {
    // Only the large reply: framework code reads short strings with it too.
    if (this.length > 1_000_000) {
      units += 1;
    }
    return original.call(this, index);
  };
  try {
    await run();
  } finally {
    Object.defineProperty(String.prototype, 'charCodeAt', descriptor);
  }
  return units;
}

describe('request-only binary call text reply scan', () => {
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

  afterEach(() => httpMock.verify());

  it('scans a reply that needs counting exactly once', async () => {
    // More units than a third of the cap, so the length shortcut cannot settle it.
    const text = 'x'.repeat(BINARY_BODY_LIMIT_BYTES / 2);
    const units = await countScannedUnits(async () => {
      const pending = adapter(requestOnly());
      (await nextRequest(httpMock)).flush(text, { headers: { 'Content-Type': 'text/plain' } });
      await pending;
    });
    expect(units).toBe(text.length);
  });

  it('refuses an over-cap multi-byte reply after one scan', async () => {
    const text = 'é'.repeat(BINARY_BODY_LIMIT_BYTES / 2 + 1);
    const units = await countScannedUnits(async () => {
      const pending = adapter(requestOnly()).catch((error: unknown) => error);
      (await nextRequest(httpMock)).flush(text);
      expect(await pending).toBeInstanceOf(TypeError);
    });
    expect(units).toBeLessThanOrEqual(text.length);
  });
});
