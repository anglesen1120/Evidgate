// 09.1.1-T3.2 (00 §5.6 W5, §9.1; ADR-0008 rules 1-3): request ids, W3C trace context and
// the same-origin `/v1` predicate the interceptors gate on.
import { BrowserPlatformLocation, DOCUMENT, PlatformLocation } from '@angular/common';
import {
  HttpClient,
  provideHttpClient,
  withInterceptors,
  type HttpInterceptorFn,
  type HttpRequest,
} from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { isApiRequestUrl } from './api-origin';
import { idempotencyInterceptor } from './idempotency.interceptor';
import { createTraceparent, requestIdInterceptor } from './request-id.interceptor';

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const TRACEPARENT = /^00-([0-9a-f]{32})-([0-9a-f]{16})-01$/;

interface Harness {
  readonly http: HttpClient;
  readonly controller: HttpTestingController;
  readonly origin: string;
}

/** Fresh zoneless TestBed per harness; `before` runs ahead of the two interceptors under test. */
function createHarness(before: readonly HttpInterceptorFn[] = []): Harness {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(
        withInterceptors([...before, requestIdInterceptor, idempotencyInterceptor]),
      ),
      provideHttpClientTesting(),
      { provide: PlatformLocation, useClass: BrowserPlatformLocation },
    ],
  });
  return {
    http: TestBed.inject(HttpClient),
    controller: TestBed.inject(HttpTestingController),
    origin: TestBed.inject(DOCUMENT).location.origin,
  };
}

describe('requestIdInterceptor', () => {
  let harness: Harness;

  beforeEach(() => {
    harness = createHarness();
  });

  afterEach(() => {
    harness.controller.verify();
  });

  it('adds X-Request-Id and traceparent to a same-origin /v1 GET', () => {
    const url = `${harness.origin}/v1/runs`;
    harness.http.get(url).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.get('X-Request-Id') ?? '').toMatch(UUID_V4);

    const traceparent = request.request.headers.get('traceparent') ?? '';
    expect(traceparent).toMatch(TRACEPARENT);
    const parts = TRACEPARENT.exec(traceparent);
    expect(parts).not.toBeNull();
    expect(parts?.[1] ?? '').toMatch(/[1-9a-f]/);
    expect(parts?.[2] ?? '').toMatch(/[1-9a-f]/);

    request.flush({});
  });

  it('treats the SDK relative base URL /v1 as an API request', () => {
    harness.http.get('/v1/runs').subscribe();
    const request = harness.controller.expectOne('/v1/runs');

    expect(request.request.headers.get('X-Request-Id') ?? '').toMatch(UUID_V4);
    expect(request.request.headers.get('traceparent') ?? '').toMatch(TRACEPARENT);

    request.flush({});
  });

  it('adds both headers to a same-origin /v1 POST', () => {
    const url = `${harness.origin}/v1/runs`;
    harness.http.post(url, { suite_id: 'suite-1' }).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.get('X-Request-Id') ?? '').toMatch(UUID_V4);
    expect(request.request.headers.get('traceparent') ?? '').toMatch(TRACEPARENT);

    request.flush({});
  });

  it('uses a distinct X-Request-Id and traceparent for every request', () => {
    const url = `${harness.origin}/v1/runs`;
    harness.http.get(url).subscribe();
    harness.http.get(url).subscribe();
    const requests = harness.controller.match(url);

    expect(requests).toHaveLength(2);
    expect(requests[0]?.request.headers.get('X-Request-Id') ?? '').toMatch(UUID_V4);
    expect(requests[1]?.request.headers.get('X-Request-Id') ?? '').toMatch(UUID_V4);
    expect(requests[0]?.request.headers.get('X-Request-Id')).not.toBe(
      requests[1]?.request.headers.get('X-Request-Id'),
    );
    expect(requests[0]?.request.headers.get('traceparent')).not.toBe(
      requests[1]?.request.headers.get('traceparent'),
    );

    for (const request of requests) {
      request.flush({});
    }
  });

  it('never overwrites caller-provided X-Request-Id, traceparent or Idempotency-Key', () => {
    const url = `${harness.origin}/v1/runs`;
    const callerTraceparent = '00-11111111111111111111111111111111-2222222222222222-01';
    const headers = {
      'X-Request-Id': 'caller-request-id',
      traceparent: callerTraceparent,
      'Idempotency-Key': 'intent-42',
    };
    harness.http.post(url, {}, { headers }).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.get('X-Request-Id')).toBe('caller-request-id');
    expect(request.request.headers.get('traceparent')).toBe(callerTraceparent);
    expect(request.request.headers.get('Idempotency-Key')).toBe('intent-42');

    request.flush({});
  });

  it('adds none of the three headers to a foreign-origin request', () => {
    const url = 'https://other.example/v1/runs';
    harness.http.post(url, {}).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.has('X-Request-Id')).toBe(false);
    expect(request.request.headers.has('traceparent')).toBe(false);
    expect(request.request.headers.has('Idempotency-Key')).toBe(false);

    request.flush({});
  });

  it('adds no request-id header to a same-origin path outside /v1', () => {
    const url = `${harness.origin}/internal/health/live`;
    harness.http.get(url).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.has('X-Request-Id')).toBe(false);
    expect(request.request.headers.has('traceparent')).toBe(false);

    request.flush({});
  });

  it('mints both UUID headers without crypto.randomUUID (non-secure context)', () => {
    const realCrypto = globalThis.crypto;
    const cryptoDescriptor = Object.getOwnPropertyDescriptor(globalThis, 'crypto');
    // A plain-http origin outside localhost: getRandomValues exists, randomUUID does not.
    Object.defineProperty(globalThis, 'crypto', {
      configurable: true,
      enumerable: true,
      writable: true,
      value: {
        getRandomValues: (bytes: Uint8Array<ArrayBuffer>) => realCrypto.getRandomValues(bytes),
      },
    });
    try {
      const url = `${harness.origin}/v1/runs`;
      harness.http.post(url, {}).subscribe();
      const request = harness.controller.expectOne(url);

      expect(request.request.headers.get('X-Request-Id') ?? '').toMatch(UUID_V4);
      expect(request.request.headers.get('Idempotency-Key') ?? '').toMatch(UUID_V4);
      expect(request.request.headers.get('traceparent') ?? '').toMatch(TRACEPARENT);

      request.flush({});
    } finally {
      if (cryptoDescriptor === undefined) {
        Reflect.deleteProperty(globalThis, 'crypto');
      } else {
        Object.defineProperty(globalThis, 'crypto', cryptoDescriptor);
      }
    }
  });

  it('hands a non-API request to the next interceptor as the same HttpRequest instance', () => {
    const captured: HttpRequest<unknown>[] = [];
    const capture: HttpInterceptorFn = (request, next) => {
      captured.push(request);
      return next(request);
    };
    harness = createHarness([capture]);

    const url = 'https://other.example/v1/runs';
    harness.http.get(url).subscribe();
    const request = harness.controller.expectOne(url);

    expect(captured).toHaveLength(1);
    expect(captured[0]).toBe(request.request);

    request.flush({});
  });
});

describe('createTraceparent', () => {
  it('emits a W3C traceparent with non-zero ids on 50 consecutive calls', () => {
    const seen = new Set<string>();
    for (let index = 0; index < 50; index += 1) {
      const traceparent = createTraceparent();
      expect(traceparent).toMatch(TRACEPARENT);
      const parts = TRACEPARENT.exec(traceparent);
      expect(parts).not.toBeNull();
      expect(parts?.[1] ?? '').toMatch(/[1-9a-f]/);
      expect(parts?.[2] ?? '').toMatch(/[1-9a-f]/);
      seen.add(traceparent);
    }
    expect(seen.size).toBe(50);
  });
});

describe('isApiRequestUrl', () => {
  const page = 'https://app.evidgate.test/runs?x=1';

  it('accepts same-origin /v1 paths only', () => {
    expect(isApiRequestUrl('/v1', page)).toBe(true);
    expect(isApiRequestUrl('/v1/', page)).toBe(true);
    expect(isApiRequestUrl('/v1/runs', page)).toBe(true);
    expect(isApiRequestUrl('/v1/runs?limit=5', page)).toBe(true);
    expect(isApiRequestUrl('https://app.evidgate.test/v1/runs', page)).toBe(true);
  });

  it('rejects foreign origins, other paths and unparsable input', () => {
    expect(isApiRequestUrl('//evil.test/v1/x', page)).toBe(false);
    expect(isApiRequestUrl('https://other.example/v1/x', page)).toBe(false);
    expect(isApiRequestUrl('https://app.evidgate.test:8443/v1/runs', page)).toBe(false);
    expect(isApiRequestUrl('http://app.evidgate.test/v1/runs', page)).toBe(false);
    expect(isApiRequestUrl('/v2/x', page)).toBe(false);
    expect(isApiRequestUrl('/v1evil', page)).toBe(false);
    expect(isApiRequestUrl('/internal/x', page)).toBe(false);
    expect(isApiRequestUrl('/metrics', page)).toBe(false);
    expect(isApiRequestUrl('/v1/runs', 'not a url')).toBe(false);
    expect(isApiRequestUrl('/v1/runs', '')).toBe(false);
    expect(isApiRequestUrl('http://[', page)).toBe(false);
  });

  it('still accepts a plain-http page on the same origin', () => {
    const plainPage = 'http://localhost:4200/projects/p-1';
    expect(isApiRequestUrl('/v1/runs', plainPage)).toBe(true);
    expect(isApiRequestUrl('http://localhost:4200/v1/runs', plainPage)).toBe(true);
  });

  it('rejects an opaque-origin page even when both origins stringify to "null"', () => {
    const filePage = 'file:///Users/me/app/index.html';
    expect(isApiRequestUrl('/v1/runs', filePage)).toBe(false);
    expect(isApiRequestUrl('file:///v1/runs', filePage)).toBe(false);
    expect(isApiRequestUrl('/v1/runs', 'data:text/html,<p>app</p>')).toBe(false);
    expect(isApiRequestUrl('/v1/runs', 'about:blank')).toBe(false);
  });

  it('rejects a non-http(s) request URL', () => {
    // `blob:` inherits the creating origin, and the others are opaque: none of them carries
    // API traffic, so the scheme alone decides regardless of origin or path.
    expect(isApiRequestUrl('blob:https://app.evidgate.test/v1/runs', page)).toBe(false);
    expect(isApiRequestUrl('data:text/plain,/v1/runs', page)).toBe(false);
    expect(isApiRequestUrl('ftp://app.evidgate.test/v1/runs', page)).toBe(false);
  });
});
