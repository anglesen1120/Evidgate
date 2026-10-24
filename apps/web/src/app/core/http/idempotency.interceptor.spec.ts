// 09.1.1-T3.2 (00 §9.1; ADR-0008 rule 2): the Idempotency-Key fallback for untyped
// mutations — caller intent keys win, GET/HEAD/OPTIONS and foreign requests stay bare.
import { BrowserPlatformLocation, DOCUMENT, PlatformLocation } from '@angular/common';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { idempotencyInterceptor } from './idempotency.interceptor';
import { requestIdInterceptor } from './request-id.interceptor';

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

interface Harness {
  readonly http: HttpClient;
  readonly controller: HttpTestingController;
  readonly origin: string;
}

function createHarness(): Harness {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([requestIdInterceptor, idempotencyInterceptor])),
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

describe('idempotencyInterceptor', () => {
  let harness: Harness;

  beforeEach(() => {
    harness = createHarness();
  });

  afterEach(() => {
    harness.controller.verify();
  });

  it('adds a UUID Idempotency-Key to a POST without one', () => {
    const url = `${harness.origin}/v1/runs`;
    harness.http.post(url, {}).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.get('Idempotency-Key') ?? '').toMatch(UUID_V4);

    request.flush({});
  });

  it('adds a key to PUT, PATCH and DELETE mutations', () => {
    const url = `${harness.origin}/v1/runs/run-1`;
    for (const method of ['PUT', 'PATCH', 'DELETE'] as const) {
      harness.http.request(method, url, { body: {} }).subscribe();
      const request = harness.controller.expectOne(url);

      expect(request.request.method).toBe(method);
      expect(request.request.headers.get('Idempotency-Key') ?? '').toMatch(UUID_V4);

      request.flush({});
    }
  });

  it('never adds a key to GET, HEAD or OPTIONS', () => {
    const url = `${harness.origin}/v1/runs`;

    harness.http.get(url).subscribe();
    const get = harness.controller.expectOne(url);
    expect(get.request.method).toBe('GET');
    expect(get.request.headers.has('Idempotency-Key')).toBe(false);
    // The sibling request-id interceptor still applies, proving the chain is live.
    expect(get.request.headers.get('X-Request-Id') ?? '').toMatch(UUID_V4);
    get.flush({});

    harness.http.head(url).subscribe();
    const head = harness.controller.expectOne(url);
    expect(head.request.method).toBe('HEAD');
    expect(head.request.headers.has('Idempotency-Key')).toBe(false);
    head.flush(null);

    harness.http.request('OPTIONS', url).subscribe();
    const options = harness.controller.expectOne(url);
    expect(options.request.method).toBe('OPTIONS');
    expect(options.request.headers.has('Idempotency-Key')).toBe(false);
    options.flush(null);
  });

  it('keeps a caller-provided key untouched and does not duplicate it', () => {
    const url = `${harness.origin}/v1/runs`;
    harness.http.post(url, {}, { headers: { 'idempotency-key': 'intent-42' } }).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.get('Idempotency-Key')).toBe('intent-42');
    expect(request.request.headers.getAll('Idempotency-Key')).toEqual(['intent-42']);

    request.flush({});
  });

  it('gives two separate mutations two different keys', () => {
    const url = `${harness.origin}/v1/runs`;
    harness.http.post(url, {}).subscribe();
    harness.http.post(url, {}).subscribe();
    const requests = harness.controller.match(url);

    expect(requests).toHaveLength(2);
    expect(requests[0]?.request.headers.get('Idempotency-Key') ?? '').toMatch(UUID_V4);
    expect(requests[1]?.request.headers.get('Idempotency-Key') ?? '').toMatch(UUID_V4);
    expect(requests[0]?.request.headers.get('Idempotency-Key')).not.toBe(
      requests[1]?.request.headers.get('Idempotency-Key'),
    );

    for (const request of requests) {
      request.flush({});
    }
  });

  it('adds no key to a foreign-origin mutation', () => {
    const url = 'https://other.example/v1/runs';
    harness.http.post(url, {}).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.has('Idempotency-Key')).toBe(false);

    request.flush({});
  });

  it('adds no API header to a same-origin mutation outside /v1', () => {
    const url = `${harness.origin}/internal/operator/replay`;
    harness.http.post(url, {}).subscribe();
    const request = harness.controller.expectOne(url);

    expect(request.request.headers.has('Idempotency-Key')).toBe(false);
    expect(request.request.headers.has('X-Request-Id')).toBe(false);
    expect(request.request.headers.has('traceparent')).toBe(false);

    request.flush({});
  });
});
