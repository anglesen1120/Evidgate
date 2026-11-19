import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { createHttpClientFetch, type HttpClientFetch } from './http-client-fetch';

const URL = 'http://localhost/v1/projects/project/authoring/imports';

describe('HttpClient fetch request-body abort ordering', () => {
  let adapter: HttpClientFetch;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    adapter = createHttpClientFetch(TestBed.inject(HttpClient));
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    vi.restoreAllMocks();
  });

  it('reports a cancel that lands after the text body read as AbortError, not a body-type refusal', async () => {
    const controller = new AbortController();
    const request = new Request(URL, {
      method: 'POST',
      body: new Uint8Array([0x50, 0x4b, 0x03, 0x04]),
      signal: controller.signal,
      headers: {
        'Content-Type': 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
      },
    });
    const textRead = vi.spyOn(request, 'text');
    textRead.mockImplementation(() => {
      controller.abort();
      return Promise.resolve('PK\u0003\u0004');
    });

    const error = await adapter(request).catch((failure: unknown) => failure);

    expect(textRead).toHaveBeenCalledOnce();
    expect(error).toBeInstanceOf(DOMException);
    expect((error as DOMException).name).toBe('AbortError');
    httpMock.expectNone(URL);
  });
});
