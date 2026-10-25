// 09.2.1-T1.1/T1.2 (plan 09.2.1 §5, §10 T1.1/T1.2; FR-09-10): the wizard's data service against
// the real HTTP stack. Each case pins the SDK operation and the URL the service reaches, the query
// the derivation depends on (the oldest project, the oldest run), the `Idempotency-Key` the
// caller's intent contributes on the one mutation, and that a refusal surfaces as the typed
// `ApiRequestError` instead of a value — the store maps that to a fixed pattern, so a refusal that
// arrived as a value would publish a wrong step.
//
// The base URL is the absolute form of the same-origin API path because Node's `Request` (the one
// `ng test` runs on) refuses the relative `/v1` the browser resolves for us.

import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import type { HttpRequest } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type {
  CiBindingListResponse,
  IngestTokenListResponse,
  ProjectDto,
  ProjectListResponse,
  RunPage,
  RunStateDto,
} from '@evidgate/sdk';

import { isApiRequestError } from '../http/api-error';
import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { createIntent } from '../http/intent';
import { OnboardingDataService } from './onboarding-data.service';

const API_BASE_URL = `${location.origin}${API_BASE_PATH}`;
const PROJECTS_URL = `${API_BASE_URL}/orgs/o-1/projects`;

const PROJECT: ProjectDto = {
  id: 'p-1',
  org_id: 'o-1',
  name: 'Storefront web',
  slug: 'storefront-web',
  status: 'active',
  default_environment_id: null,
  created_at: '2026-10-20T09:00:00Z',
  updated_at: '2026-10-20T09:00:00Z',
  version: 1,
  retention_days: null,
  monthly_run_quota: null,
  case_prefix: 'STOR',
};

const PROJECT_PAGE: ProjectListResponse = { items: [PROJECT], next_cursor: null };
const NO_BINDINGS: CiBindingListResponse = { items: [], next_cursor: null };
const NO_TOKENS: IngestTokenListResponse = { items: [], next_cursor: null };
const NO_RUNS: RunPage = { items: [], next_cursor: null };
const RUN_STATE: RunStateDto = {
  run_id: 'r-1',
  state: 'complete',
  current_evidence_revision_id: null,
  version: 2,
  purpose: 'regular',
  ref: {
    kind: null,
    name: null,
    pr_number: null,
    head_sha: null,
    base_sha: null,
    trigger_event: null,
    is_fork: false,
  },
  unverified_ref: false,
};

/**
 * The served media type: the adapter reads bodies as text (`responseType: 'text'`) and `flush`
 * adds no content type of its own, while the SDK picks its parser from the response
 * `Content-Type` (`parseAs: 'auto'`), so a flush without this header reaches the SDK as an unparsed
 * stream — exactly what a server that omitted the header would do.
 */
const JSON_HEADERS = { 'Content-Type': 'application/json' } as const;

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

/** The rejection a promise answered with, so a case can assert on it. */
async function rejectionOf(promise: Promise<unknown>): Promise<unknown> {
  return promise.then(
    () => undefined,
    (error: unknown) => error,
  );
}

describe('OnboardingDataService (09.2.1-T1.1/T1.2)', () => {
  let httpMock: HttpTestingController;
  let service: OnboardingDataService;

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
    service = TestBed.inject(OnboardingDataService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('listProjects() reads the org project page with the limit the derivation asks for', async () => {
    const page = service.listProjects('o-1', { limit: 1 });
    const request = await takeRequest(httpMock, (candidate) =>
      candidate.url.startsWith(PROJECTS_URL),
    );
    expect(request.request.method).toBe('GET');
    expect(request.request.urlWithParams).toBe(`${PROJECTS_URL}?limit=1`);
    // A read carries no Idempotency-Key: only a mutation is retried on a caller-owned intent.
    expect(request.request.headers.has('Idempotency-Key')).toBe(false);
    request.flush(JSON.stringify(PROJECT_PAGE), { headers: JSON_HEADERS });
    await expect(page).resolves.toEqual(PROJECT_PAGE);
  });

  it('getProject() reads one project by id', async () => {
    const project = service.getProject('p-1');
    const request = await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1`);
    expect(request.request.method).toBe('GET');
    expect(request.request.headers.has('Idempotency-Key')).toBe(false);
    request.flush(JSON.stringify(PROJECT), { headers: JSON_HEADERS });
    await expect(project).resolves.toEqual(PROJECT);
  });

  it('getProject() surfaces a 404 envelope as a typed not_found error', async () => {
    const project = service.getProject('p-foreign');
    const envelope = {
      error: {
        code: 'not_found',
        message: 'Unknown project.',
        request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
        retryable: false,
      },
    };
    (await takeRequest(httpMock, `${API_BASE_URL}/projects/p-foreign`)).flush(
      JSON.stringify(envelope),
      { status: 404, statusText: '', headers: JSON_HEADERS },
    );

    const cause = await rejectionOf(project);
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('not_found');
    }
  });

  it('createProject() posts the body with the intent key and answers the created project', async () => {
    const intent = createIntent();
    const body = { name: 'Storefront web', slug: 'storefront-web' };
    const created = service.createProject('o-1', body, intent);
    const request = await takeRequest(httpMock, PROJECTS_URL);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('Idempotency-Key')).toBe(intent.idempotencyKey);
    // The adapter hands the interceptors the request text, so the body is the serialized JSON.
    expect(request.request.body).toBe(
      JSON.stringify({ name: 'Storefront web', slug: 'storefront-web' }),
    );
    request.flush(JSON.stringify(PROJECT), {
      status: 201,
      statusText: '',
      headers: JSON_HEADERS,
    });
    await expect(created).resolves.toEqual(PROJECT);
  });

  it('createProject() replays the same key when the caller retries the same intent', async () => {
    const intent = createIntent();
    const body = { name: 'Storefront web', slug: 'storefront-web' };

    const first = service.createProject('o-1', body, intent);
    const firstRequest = await takeRequest(httpMock, PROJECTS_URL);
    firstRequest.flush(
      JSON.stringify({ error: { code: 'unavailable', message: 'busy', retryable: true } }),
      { status: 503, statusText: '', headers: JSON_HEADERS },
    );
    await rejectionOf(first);

    const retried = service.createProject('o-1', body, intent);
    const secondRequest = await takeRequest(httpMock, PROJECTS_URL);
    expect(secondRequest.request.headers.get('Idempotency-Key')).toBe(intent.idempotencyKey);
    secondRequest.flush(JSON.stringify(PROJECT), {
      status: 201,
      statusText: '',
      headers: JSON_HEADERS,
    });
    await expect(retried).resolves.toEqual(PROJECT);
  });

  it('createProject() surfaces a 403 envelope as a typed forbidden error', async () => {
    const created = service.createProject('o-1', { name: 'n', slug: 'n' }, createIntent());
    const envelope = {
      error: {
        code: 'forbidden',
        message: 'Missing permission project:create.',
        request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
        retryable: false,
      },
    };
    (await takeRequest(httpMock, PROJECTS_URL)).flush(JSON.stringify(envelope), {
      status: 403,
      statusText: '',
      headers: JSON_HEADERS,
    });

    const cause = await rejectionOf(created);
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('forbidden');
    }
  });

  it('listCiBindings() reads the project binding page', async () => {
    const bindings = service.listCiBindings('p-1', { limit: 1 });
    const request = await takeRequest(httpMock, (candidate) =>
      candidate.url.startsWith(`${API_BASE_URL}/projects/p-1/ci-bindings`),
    );
    expect(request.request.method).toBe('GET');
    expect(request.request.urlWithParams).toContain('limit=1');
    request.flush(JSON.stringify(NO_BINDINGS), { headers: JSON_HEADERS });
    await expect(bindings).resolves.toEqual(NO_BINDINGS);
  });

  it('listIngestTokens() reads the project token page', async () => {
    const tokens = service.listIngestTokens('p-1', { limit: 1 });
    const request = await takeRequest(httpMock, (candidate) =>
      candidate.url.startsWith(`${API_BASE_URL}/projects/p-1/ingest-tokens`),
    );
    expect(request.request.method).toBe('GET');
    expect(request.request.urlWithParams).toContain('limit=1');
    request.flush(JSON.stringify(NO_TOKENS), { headers: JSON_HEADERS });
    await expect(tokens).resolves.toEqual(NO_TOKENS);
  });

  it('listRuns() reads the project run page', async () => {
    const runs = service.listRuns('p-1', { limit: 1 });
    const request = await takeRequest(httpMock, (candidate) =>
      candidate.url.startsWith(`${API_BASE_URL}/projects/p-1/runs`),
    );
    expect(request.request.method).toBe('GET');
    expect(request.request.urlWithParams).toContain('limit=1');
    request.flush(JSON.stringify(NO_RUNS), { headers: JSON_HEADERS });
    await expect(runs).resolves.toEqual(NO_RUNS);
  });

  it('getRun() reads one run, which carries the evidence revision', async () => {
    const run = service.getRun('r-1');
    const request = await takeRequest(httpMock, `${API_BASE_URL}/runs/r-1`);
    expect(request.request.method).toBe('GET');
    request.flush(JSON.stringify(RUN_STATE), { headers: JSON_HEADERS });
    const state: RunStateDto = await run;
    expect(state.current_evidence_revision_id).toBeNull();
  });

  it('getRun() surfaces a 404 envelope as a typed not_found error', async () => {
    const run = service.getRun('r-unknown');
    const envelope = {
      error: {
        code: 'not_found',
        message: 'Unknown run.',
        request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
        retryable: false,
      },
    };
    (await takeRequest(httpMock, `${API_BASE_URL}/runs/r-unknown`)).flush(
      JSON.stringify(envelope),
      { status: 404, statusText: '', headers: JSON_HEADERS },
    );

    const cause = await rejectionOf(run);
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('not_found');
    }
  });
});
