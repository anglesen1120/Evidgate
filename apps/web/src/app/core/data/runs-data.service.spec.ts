// 09.1.1-T3.1 (00 §5.6 W5, FR-09-10; ADR-0008 rule 2): the reference data service against the
// real stack. Each case pins three things at once — the SDK operation and URL the service calls,
// the `Idempotency-Key` the caller's intent contributes, and that a refusal surfaces as the typed
// `ApiRequestError` instead of a value (a page cannot get a 404 into its `ready` state).
//
// The base URL is the absolute form of the same-origin API path because Node's `Request` (the
// one `ng test` runs on) refuses the relative `/v1` the browser resolves for us.

import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import type { HttpRequest } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { RunCreated, RunPage, RunStateDto } from '@evidgate/sdk';

import { isApiRequestError } from '../http/api-error';
import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { createIntent } from '../http/intent';
import { RunsDataService } from './runs-data.service';

const API_BASE_URL = `${location.origin}${API_BASE_PATH}`;
const RUNS_URL = `${API_BASE_URL}/projects/p-1/runs`;
const RUN_PAGE: RunPage = { items: [], next_cursor: null };
const RUN_STATE = {
  run_id: 'r-1',
  state: 'running',
  current_evidence_revision_id: null,
  version: 3,
  purpose: 'regular',
  ref: {
    ref_name: null,
    pr_number: null,
    head_sha: null,
    trigger_event: null,
    is_fork: null,
  },
  unverified_ref: false,
};

/**
 * A JSON body as the adapter delivers it: the SDK reads a `Response`, and `flush` converts a
 * plain object only for `responseType: 'json'`, while the adapter asks for a blob.
 */
function jsonBlob(value: unknown): Blob {
  return new Blob([JSON.stringify(value)], { type: 'application/json' });
}

/**
 * The served media type, which `flush` does not derive from the blob: the SDK picks its parser
 * from the response `Content-Type` (`parseAs: 'auto'`), so a flush without this header reaches
 * the SDK as an unparsed stream — exactly what a server that omitted the header would do.
 */
const JSON_HEADERS = { 'Content-Type': 'application/json' } as const;

const RUN_CREATED: RunCreated = { run_id: 'run-1', state: 'queued', unverified_ref: false };

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

describe('RunsDataService (09.1.1-T3.1)', () => {
  let httpMock: HttpTestingController;
  let service: RunsDataService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideApi(API_BASE_URL),
        provideHttpClientTesting(),
        provideZonelessChangeDetection(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    service = TestBed.inject(RunsDataService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('list() calls GET on the project runs URL with the cursor query', async () => {
    const page = service.list('p-1', { cursor: 'c1', limit: 20 });
    const request = await takeRequest(httpMock, (candidate) => candidate.url.startsWith(RUNS_URL));
    expect(request.request.method).toBe('GET');
    expect(request.request.urlWithParams).toContain('cursor=c1');
    expect(request.request.urlWithParams).toContain('limit=20');
    request.flush(jsonBlob(RUN_PAGE), { headers: JSON_HEADERS });
    await expect(page).resolves.toEqual(RUN_PAGE);
  });

  it('list() sends no Idempotency-Key, being a read', async () => {
    const page = service.list('p-1');
    const request = await takeRequest(httpMock, RUNS_URL);
    expect(request.request.headers.has('Idempotency-Key')).toBe(false);
    request.flush(jsonBlob(RUN_PAGE), { headers: JSON_HEADERS });
    await page;
  });

  it('get() calls GET on the run URL', async () => {
    const run = service.get('r-1');
    const request = await takeRequest(httpMock, `${API_BASE_URL}/runs/r-1`);
    expect(request.request.method).toBe('GET');
    request.flush(jsonBlob(RUN_STATE), { headers: JSON_HEADERS });
    const state: RunStateDto = await run;
    expect(state.version).toBe(3);
  });

  it('create() posts the body with the intent key', async () => {
    const intent = createIntent();
    const created = service.create('p-1', { manifest_id: 'mf-1' }, intent);
    const request = await takeRequest(httpMock, RUNS_URL);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('Idempotency-Key')).toBe(intent.idempotencyKey);
    // The adapter hands the interceptors the request text, so the body is the serialized JSON.
    expect(request.request.body).toBe(JSON.stringify({ manifest_id: 'mf-1' }));
    request.flush(jsonBlob(RUN_CREATED), {
      status: 201,
      statusText: '',
      headers: JSON_HEADERS,
    });
    const run: RunCreated = await created;
    expect(run.run_id).toBe('run-1');
  });

  it('create() surfaces a 404 envelope as a typed not_found error', async () => {
    const created = service.create('other-tenant', { manifest_id: 'mf-1' }, createIntent());
    const envelope = {
      error: {
        code: 'not_found',
        message: 'Unknown project.',
        request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
        retryable: false,
      },
    };
    (await takeRequest(httpMock, `${API_BASE_URL}/projects/other-tenant/runs`)).flush(
      jsonBlob(envelope),
      { status: 404, statusText: '', headers: { 'Content-Type': 'application/json' } },
    );

    const cause: unknown = await created.then(
      () => undefined,
      (error: unknown) => error,
    );
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('not_found');
      expect(cause.error.code).toBe('not_found');
    }
  });
});
