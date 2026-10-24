// 09.1.1-T3.2 done-when (`api-client.spec.ts` proves headers added and envelope parsed): the
// whole API stack is exercised the way the app runs it — `provideApi()` (the SDK client on the
// Angular adapter, the request-id and idempotency interceptors, the XSRF wiring), `HttpClient`
// under `provideHttpClientTesting()`, and `ApiGateway` turning SDK results into typed bodies or
// thrown `ApiRequestError`s. Nothing here stubs the mapper or the interceptors: a regression in
// any of them fails these tests.
//
// Requests are matched on the absolute form of the same-origin API URL: the SDK builds
// `new Request(baseUrl + path)` and Node's `Request` (the one `ng test` runs on) refuses a
// relative URL, so the base below is `location.origin + API_BASE_PATH`. That is the same
// origin the app calls; only the spelling differs.

import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpClient, type HttpRequest } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  createRun,
  listRuns,
  type Client,
  type ErrorEnvelope,
  type ErrorEnvelopeDetail,
  type RunPage,
} from '@evidgate/sdk';
import { firstValueFrom } from 'rxjs';

import { isApiRequestError, type ApiError } from './api-error';
import { provideApi } from './api-client';
import { ApiGateway, type SdkResult } from './api-gateway';
import { API_BASE_PATH } from './api-origin';
import { createIntent, intentHeaders, type Intent } from './intent';

const API_BASE_URL = `${location.origin}${API_BASE_PATH}`;
const RUNS_URL = `${API_BASE_URL}/projects/p-1/runs`;
const REQUEST_ID = '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4';
const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const TRACEPARENT = /^00-[0-9a-f]{32}-[0-9a-f]{16}-01$/;
const RUN_PAGE: RunPage = { items: [], next_cursor: null };
const RUN_CREATED = { run_id: 'run-1', state: 'queued', unverified_ref: false };

/** The `00 §9.2` body the API sends; `details` only when a case names one. */
function errorEnvelope(code: string, details?: readonly ErrorEnvelopeDetail[]): ErrorEnvelope {
  return {
    error: {
      code,
      message: `The API refused the call (${code}).`,
      request_id: REQUEST_ID,
      retryable: false,
      ...(details === undefined ? {} : { details: [...details] }),
    },
  };
}

interface FlushOptions {
  readonly status?: number;
  readonly headers?: Record<string, string>;
}

/**
 * Answer a pending request with a JSON body. The adapter issues `responseType: 'blob'`, so the
 * body is a `Blob` (never a plain object) and the content type is what the SDK parses by.
 */
function flushJson(request: TestRequest, body: unknown, options: FlushOptions = {}): void {
  request.flush(new Blob([JSON.stringify(body)], { type: 'application/json' }), {
    status: options.status ?? 200,
    // `flush` requires an explicit status text once a custom status is set.
    statusText: '',
    headers: { 'Content-Type': 'application/json', ...options.headers },
  });
}

/** The SDK `createRun` call a page makes: the caller's intent supplies `Idempotency-Key`. */
function createRunWithIntent(client: Client, intent: Intent) {
  return createRun({
    client,
    path: { p: 'p-1' },
    body: { manifest_id: 'mf-1' },
    headers: intentHeaders(intent),
  });
}

/**
 * The SDK -> adapter -> `HttpClient` pipeline is asynchronous, so a request is registered a few
 * microtasks after the call starts. `expectOne` throws when nothing matches yet, so this drains
 * the queue first and then matches — the request is still `verify()`-checked like any other.
 */
async function takeRequest(
  httpMock: HttpTestingController,
  match: string | ((request: HttpRequest<unknown>) => boolean),
): Promise<TestRequest> {
  for (let attempt = 0; attempt < 20; attempt += 1) {
    const [request] = httpMock.match(match);
    if (request !== undefined) {
      return request;
    }
    await Promise.resolve();
  }
  throw new Error('no HTTP request reached HttpTestingController');
}

/** Resolves with whatever a call that must not succeed rejected with. */
async function captureRejection(promise: Promise<unknown>): Promise<unknown> {
  try {
    await promise;
  } catch (cause) {
    return cause;
  }
  throw new Error('expected the call to fail, but it resolved');
}

/** Resolves with the typed failure of a call that must fail with an `ApiRequestError`. */
async function captureApiError(promise: Promise<unknown>): Promise<ApiError> {
  const cause = await captureRejection(promise);
  if (isApiRequestError(cause)) {
    return cause.error;
  }
  throw cause;
}

describe('API client stack (09.1.1-T3.2)', () => {
  let httpMock: HttpTestingController;
  let gateway: ApiGateway;

  beforeEach(() => {
    document.cookie = 'XSRF-TOKEN=csrf-cookie-1; path=/';
    TestBed.configureTestingModule({
      providers: [
        provideApi(API_BASE_URL),
        provideHttpClientTesting(),
        provideZonelessChangeDetection(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    gateway = TestBed.inject(ApiGateway);
  });

  afterEach(() => {
    httpMock.verify();
    document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
  });

  it('adds X-Request-Id and traceparent to an API request', async () => {
    const page = gateway.call((sdk) => listRuns({ client: sdk, path: { p: 'p-1' } }));
    const request = await takeRequest(httpMock, RUNS_URL);
    expect(request.request.method).toBe('GET');
    expect(request.request.headers.get('X-Request-Id')).toMatch(UUID_V4);
    expect(request.request.headers.get('traceparent')).toMatch(TRACEPARENT);
    flushJson(request, RUN_PAGE);
    await expect(page).resolves.toEqual(RUN_PAGE);
  });

  it('sends neither Idempotency-Key nor X-XSRF-TOKEN on a GET', async () => {
    const page = gateway.call((sdk) => listRuns({ client: sdk, path: { p: 'p-1' } }));
    const request = await takeRequest(httpMock, RUNS_URL);
    expect(request.request.headers.has('Idempotency-Key')).toBe(false);
    expect(request.request.headers.has('X-XSRF-TOKEN')).toBe(false);
    flushJson(request, RUN_PAGE);
    await page;
  });

  it('sends the intent key on createRun and reuses it only within one intent', async () => {
    const intent = createIntent();
    const keys: string[] = [];

    for (const attempt of [1, 2]) {
      const created = gateway.call((sdk) => createRunWithIntent(sdk, intent));
      const request = await takeRequest(httpMock, RUNS_URL);
      expect(request.request.method).toBe('POST');
      keys.push(request.request.headers.get('Idempotency-Key') ?? '');
      flushJson(request, { ...RUN_CREATED, run_id: `run-${attempt}` }, { status: 201 });
      await created;
    }

    const otherIntent = createIntent();
    const created = gateway.call((sdk) => createRunWithIntent(sdk, otherIntent));
    const request = await takeRequest(httpMock, RUNS_URL);
    keys.push(request.request.headers.get('Idempotency-Key') ?? '');
    flushJson(request, RUN_CREATED, { status: 201 });
    await created;

    expect(keys[0]).toBe(intent.idempotencyKey);
    expect(keys[1]).toBe(intent.idempotencyKey);
    expect(keys[2]).toBe(otherIntent.idempotencyKey);
    expect(keys[2]).not.toBe(keys[0]);
  });

  it('sends the cookie XSRF token on a same-origin mutation', async () => {
    const created = gateway.call((sdk) => createRunWithIntent(sdk, createIntent()));
    const request = await takeRequest(httpMock, RUNS_URL);
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('csrf-cookie-1');
    flushJson(request, RUN_CREATED, { status: 201 });
    await created;
  });

  it('throws not_found for a 404 envelope instead of resolving a value', async () => {
    const page = gateway.call((sdk) => listRuns({ client: sdk, path: { p: 'other-tenant' } }));
    const url = `${API_BASE_URL}/projects/other-tenant/runs`;
    flushJson(await takeRequest(httpMock, url), errorEnvelope('not_found'), { status: 404 });
    const error = await captureApiError(page);
    expect(error.kind).toBe('not_found');
    expect(error.status).toBe(404);
    expect(error.code).toBe('not_found');
    expect(error.requestId).toBe(REQUEST_ID);
  });

  it('throws csrf_failed for a 403 csrf_failed envelope', async () => {
    const created = gateway.call((sdk) => createRunWithIntent(sdk, createIntent()));
    flushJson(await takeRequest(httpMock, RUNS_URL), errorEnvelope('csrf_failed'), { status: 403 });
    const error = await captureApiError(created);
    expect(error.kind).toBe('csrf_failed');
    expect(error.status).toBe(403);
    expect(error.code).toBe('csrf_failed');
  });

  it('throws idempotency_conflict for a 409 idempotency_conflict envelope', async () => {
    const created = gateway.call((sdk) => createRunWithIntent(sdk, createIntent()));
    flushJson(await takeRequest(httpMock, RUNS_URL), errorEnvelope('idempotency_conflict'), {
      status: 409,
    });
    const error = await captureApiError(created);
    expect(error.kind).toBe('idempotency_conflict');
    expect(error.status).toBe(409);
  });

  it('throws precondition_failed with the envelope details for a 412', async () => {
    const created = gateway.call((sdk) => createRunWithIntent(sdk, createIntent()));
    flushJson(
      await takeRequest(httpMock, RUNS_URL),
      errorEnvelope('precondition_failed', [
        { index: 3, path: '/events/3/sequence', reason: 'duplicate_different_payload' },
      ]),
      { status: 412 },
    );
    const error = await captureApiError(created);
    expect(error.kind).toBe('precondition_failed');
    expect(error.details).toHaveLength(1);
    expect(error.details[0]?.path).toBe('/events/3/sequence');
    expect(error.details[0]?.reason).toBe('duplicate_different_payload');
  });

  it('throws validation for a 422 with per-item details', async () => {
    const page = gateway.call((sdk) => listRuns({ client: sdk, path: { p: 'p-1' } }));
    flushJson(
      await takeRequest(httpMock, RUNS_URL),
      errorEnvelope('request_invalid', [{ path: 'ref_name', reason: 'invalid_value' }]),
      { status: 422 },
    );
    const error = await captureApiError(page);
    expect(error.kind).toBe('validation');
    expect(error.details[0]?.reason).toBe('invalid_value');
  });

  it('throws rate_limited and keeps Retry-After in seconds for a 429', async () => {
    const page = gateway.call((sdk) => listRuns({ client: sdk, path: { p: 'p-1' } }));
    flushJson(await takeRequest(httpMock, RUNS_URL), errorEnvelope('rate_limited'), {
      status: 429,
      headers: { 'Retry-After': '120' },
    });
    const error = await captureApiError(page);
    expect(error.kind).toBe('rate_limited');
    expect(error.retryAfterSeconds).toBe(120);
  });

  it('throws unavailable for a 503', async () => {
    const page = gateway.call((sdk) => listRuns({ client: sdk, path: { p: 'p-1' } }));
    flushJson(await takeRequest(httpMock, RUNS_URL), errorEnvelope('unavailable'), {
      status: 503,
      headers: { 'Retry-After': '30' },
    });
    const error = await captureApiError(page);
    expect(error.kind).toBe('unavailable');
    expect(error.retryAfterSeconds).toBe(30);
  });

  it('reports an HTML 502 without an envelope as unavailable/envelope_missing', async () => {
    const page = gateway.call((sdk) => listRuns({ client: sdk, path: { p: 'p-1' } }));
    const html = new Blob(['<html><body>Bad Gateway</body></html>'], { type: 'text/html' });
    (await takeRequest(httpMock, RUNS_URL)).flush(html, {
      status: 502,
      statusText: '',
      headers: { 'Content-Type': 'text/html' },
    });
    const error = await captureApiError(page);
    expect(error.kind).toBe('unavailable');
    expect(error.code).toBe('envelope_missing');
    expect(error.status).toBe(502);
  });

  it('reports a transport failure as network with status 0 and keeps the cause', async () => {
    const page = gateway.call((sdk) => listRuns({ client: sdk, path: { p: 'p-1' } }));
    (await takeRequest(httpMock, RUNS_URL)).error(new ProgressEvent('error'));
    const rejection = await captureRejection(page);
    if (!isApiRequestError(rejection)) {
      throw rejection;
    }
    expect(rejection.error.kind).toBe('network');
    expect(rejection.error.status).toBe(0);
    expect(rejection.error.retryable).toBe(true);
    expect(rejection.cause).toBeInstanceOf(TypeError);
  });

  it('rethrows the AbortError of an aborted call instead of a network failure', async () => {
    const controller = new AbortController();
    const page = gateway.call((sdk) =>
      listRuns({ client: sdk, path: { p: 'p-1' }, signal: controller.signal }),
    );
    await takeRequest(httpMock, RUNS_URL);
    controller.abort();
    const rejection = await captureRejection(page);
    expect(rejection).toBeInstanceOf(DOMException);
    expect(isApiRequestError(rejection)).toBe(false);
    expect(rejection).toHaveProperty('name', 'AbortError');
  });

  it('treats a non-Error abort reason as a cancellation too', async () => {
    const controller = new AbortController();
    const page = gateway.call((sdk) =>
      listRuns({ client: sdk, path: { p: 'p-1' }, signal: controller.signal }),
    );
    await takeRequest(httpMock, RUNS_URL);
    // A plain string reason cannot become a typed API failure: an abort is a cancellation
    // whatever its reason, so the call still rejects as an `AbortError`, never as `network`.
    controller.abort('timeout');
    const rejection = await captureRejection(page);
    expect(rejection).toBeInstanceOf(DOMException);
    expect(isApiRequestError(rejection)).toBe(false);
    expect(rejection).toHaveProperty('name', 'AbortError');
  });

  // The two cases below reject through the operation itself: the SDK catches every rejection of
  // its `fetch`, so only an operation that fails before `fetch` (or a differently configured
  // client) reaches this branch of `ApiGateway.call`.
  it('maps a TypeError rejected by the operation to network, keeping it as the cause', async () => {
    const transportFailure = new TypeError('fetch failed');
    const rejection = await captureRejection(
      gateway.call<SdkResult>(() => Promise.reject(transportFailure)),
    );
    if (!isApiRequestError(rejection)) {
      throw rejection;
    }
    expect(rejection.error.kind).toBe('network');
    expect(rejection.error.status).toBe(0);
    expect(rejection.cause).toBe(transportFailure);
  });

  it('rethrows any other operation rejection unchanged', async () => {
    const bug = new RangeError('the operation misused the SDK');
    const rejection = await captureRejection(gateway.call<SdkResult>(() => Promise.reject(bug)));
    expect(rejection).toBe(bug);
  });

  it('leaves a foreign-origin request untouched by all three API headers', async () => {
    const http = TestBed.inject(HttpClient);
    const foreign = firstValueFrom(
      http.get('https://foreign.example/collect', { responseType: 'text' }),
    );
    const request = await takeRequest(httpMock, 'https://foreign.example/collect');
    expect(request.request.headers.has('X-Request-Id')).toBe(false);
    expect(request.request.headers.has('traceparent')).toBe(false);
    expect(request.request.headers.has('Idempotency-Key')).toBe(false);
    request.flush('ok');
    await expect(foreign).resolves.toBe('ok');
  });
});
