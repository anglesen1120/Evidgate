import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpClient, HttpHeaders, HttpResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { EXACT_MATCH_PROFILE, takeRequestWith } from '../../shared/testing/http-data.testing';
import {
  BINARY_BODY_LIMIT_BYTES,
  BINARY_REQUEST,
  BINARY_TRANSPORT_HEADER,
  isCappedBinaryCall,
} from './binary-transport';
import { createHttpClientFetch, type HttpClientFetch } from './http-client-fetch';
import { toBinaryResponse } from './http-client-fetch-binary';

const URL = 'http://localhost/v1/projects/project/authoring/import';
const takeRequest = takeRequestWith(EXACT_MATCH_PROFILE);

function requestOnly(): Request {
  return new Request(URL, {
    method: 'POST',
    body: new Uint8Array([1]),
    headers: { [BINARY_TRANSPORT_HEADER]: BINARY_REQUEST },
  });
}

describe('binary transport text boundary compatibility', () => {
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

  it.each([200, 422])(
    'accepts a request-only %i text reply exactly at the UTF-8 cap',
    async (status) => {
      const text = 'é'.repeat(BINARY_BODY_LIMIT_BYTES / 2);
      const pending = adapter(requestOnly());
      (await takeRequest(httpMock, URL)).flush(text, {
        status,
        statusText: 'Reply',
        headers: { 'Content-Type': 'text/plain' },
      });
      const response = await pending;
      expect(response.status).toBe(status);
      await expect(response.text()).resolves.toBe(text);
    },
  );

  it.each([200, 422])(
    'refuses a request-only %i text reply one UTF-8 byte over the cap',
    async (status) => {
      const text = `${'é'.repeat(BINARY_BODY_LIMIT_BYTES / 2)}a`;
      const pending = adapter(requestOnly()).catch((error: unknown) => error);
      (await takeRequest(httpMock, URL)).flush(text, {
        status,
        statusText: 'Reply',
        headers: { 'Content-Type': 'text/plain' },
      });
      expect(await pending).toBeInstanceOf(TypeError);
      expect(await pending).toHaveProperty('message', expect.stringContaining('exceeds limit'));
    },
  );

  it.each([200, 422])('keeps a non-opted-in %i text reply uncapped', async (status) => {
    const text = `${'é'.repeat(BINARY_BODY_LIMIT_BYTES / 2)}a`;
    const pending = adapter(new Request(URL));
    const test = await takeRequest(httpMock, URL);
    expect(test.request.responseType).toBe('text');
    expect(test.request.reportProgress).toBe(false);
    expect(isCappedBinaryCall(test.request)).toBe(false);
    test.flush(text, {
      status,
      statusText: 'Reply',
      headers: { 'Content-Type': 'text/plain' },
    });
    const response = await pending;
    expect(response.status).toBe(status);
    await expect(response.text()).resolves.toBe(text);
  });
});

describe('binary response body adaptation', () => {
  const backing = new Uint8Array([9, 0xff, 0, 0x80, 8]);
  const headers = new HttpHeaders({ 'Content-Type': 'application/octet-stream' });
  const source = new HttpResponse<unknown>({ status: 200, body: null });

  it.each([
    ['a sliced typed array', backing.subarray(1, 4)],
    ['a sliced DataView', new DataView(backing.buffer, 1, 3)],
  ])('preserves only the payload bytes of %s', async (_name, body) => {
    const response = toBinaryResponse(200, 'OK', headers, body, source);
    expect(new Uint8Array(await response.arrayBuffer())).toEqual(new Uint8Array([0xff, 0, 0x80]));
  });

  it.each([['a view', new Uint8Array(0)]])(
    'keeps an empty body from %s bodyless',
    (_name, body) => {
      expect(toBinaryResponse(200, 'OK', headers, body, source).body).toBeNull();
    },
  );
});
