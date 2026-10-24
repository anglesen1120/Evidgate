// 09.1.1-T3 boundary cases (QA of 09.1.1-T3, kept next to the mapper): the same-origin
// predicate's near-miss paths, the interceptor chain on a protocol-relative URL, and the error
// mapper's edge inputs — a status-only refusal, an envelope-shaped body that is not an envelope,
// and a Retry-After that is neither delta-seconds nor an HTTP-date. Each case states the contract
// it checks; a failing case is a defect. The per-unit cases of the same contracts live in
// api-origin's importers and `error-mapper.spec.ts`; nothing here repeats them verbatim.

import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { isApiRequestUrl } from './api-origin';
import { mapApiError, parseRetryAfter } from './error-mapper';
import { idempotencyInterceptor } from './idempotency.interceptor';
import { requestIdInterceptor } from './request-id.interceptor';

const PAGE = `${location.origin}/projects/p-1`;

describe('isApiRequestUrl — near-miss paths', () => {
  it.each(['/V1/runs', '/v10/runs', '/v1/../internal', '/v1/%2e%2e/internal', ''])(
    'rejects %j',
    (url) => {
      expect(isApiRequestUrl(url, PAGE)).toBe(false);
    },
  );

  it('accepts an absolute same-origin /v1 URL', () => {
    expect(isApiRequestUrl(`${location.origin}/v1/runs?x=1`, PAGE)).toBe(true);
  });
});

describe('interceptor chain on a protocol-relative URL', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(withInterceptors([requestIdInterceptor, idempotencyInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('adds no API header to a protocol-relative POST, which resolves to another origin', () => {
    http.post('//evil.test/v1/runs', {}).subscribe();
    const request = httpMock.expectOne('//evil.test/v1/runs');

    expect(request.request.headers.has('Idempotency-Key')).toBe(false);
    expect(request.request.headers.has('X-Request-Id')).toBe(false);
    expect(request.request.headers.has('traceparent')).toBe(false);
    request.flush({});
  });
});

describe('mapApiError — envelope-less status edges', () => {
  it('maps a 304 with no envelope to unexpected / envelope_missing, not retryable', () => {
    const error = mapApiError({ status: 304, body: null, headers: null });

    expect(error.kind).toBe('unexpected');
    expect(error.code).toBe('envelope_missing');
    expect(error.retryable).toBe(false);
  });

  it('maps a 404 with an HTML body to not_found while keeping the envelope_missing code', () => {
    const error = mapApiError({ status: 404, body: '<html>nope</html>', headers: null });

    expect(error.kind).toBe('not_found');
    expect(error.code).toBe('envelope_missing');
    expect(error.requestId).toBeNull();
    expect(error.message).not.toContain('nope');
  });

  it('does not read an envelope whose details is null as an envelope', () => {
    const error = mapApiError({
      status: 404,
      body: {
        error: {
          code: 'not_found',
          message: 'x',
          request_id: 'req-1',
          retryable: false,
          details: null,
        },
      },
      headers: null,
    });

    expect(error.kind).toBe('not_found');
    expect(error.code).toBe('envelope_missing');
    expect(error.requestId).toBeNull();
  });

  it('reads an HTTP-date Retry-After on a 429 envelope as whole seconds', () => {
    vi.useFakeTimers();
    try {
      vi.setSystemTime(new Date('2026-10-24T10:00:00Z'));
      const error = mapApiError({
        status: 429,
        body: {
          error: {
            code: 'rate_limited',
            message: 'slow down',
            request_id: 'req-2',
            retryable: true,
          },
        },
        headers: new Headers({ 'Retry-After': 'Sat, 24 Oct 2026 10:00:30 GMT' }),
      });

      expect(error.kind).toBe('rate_limited');
      expect(error.retryAfterSeconds).toBe(30);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('parseRetryAfter — unparsable date shapes', () => {
  it.each(['Foo 10:00:00', 'Bar, 99 Oct 2026 25:61:61 GMT', 'Mon, 12:00:00 GMT junk'])(
    'returns null for %j',
    (value) => {
      expect(parseRetryAfter(value, Date.parse('2026-10-24T10:00:00Z'))).toBeNull();
    },
  );
});
