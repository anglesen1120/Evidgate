// 09.2.1-T1.1/T1.2/T2 (plan 09.2.1 §5, §10 T1.1/T1.2/T2; FR-09-10; AC2): the wizard's data
// service against the real HTTP stack. Each case pins the SDK operation and the URL the service
// reaches, the query the derivation depends on (the oldest project, the oldest run), the
// `Idempotency-Key` the caller's intent contributes on the create that takes one, the three
// secret-bearing creates of step 2 (no caller intent, and the org header the binding contract
// asks for), and that a refusal surfaces as the typed `ApiRequestError` instead of a value — the
// store maps that to a fixed pattern, so a refusal that arrived as a value would publish a wrong
// step, and step 2 would show a panel for a binding that was never created.
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
  CiBindingCreatedResponse,
  CiBindingListResponse,
  CiBindingView,
  CreateCiBindingRequest,
  CreateIngestTokenRequest,
  IngestTokenCreatedResponse,
  IngestTokenListResponse,
  IngestTokenView,
  PolicyActivationDto,
  PolicyActivationStateDto,
  PolicyRevisionDto,
  PolicyRevisionV1,
  ProjectDto,
  ProjectListResponse,
  RunPage,
  RunStateDto,
} from '@evidgate/sdk';

import { isApiRequestError } from '../http/api-error';
import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { createIntent } from '../http/intent';
import { TenantStore } from '../session/tenant.store';
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

/** The row a create answers: metadata, then the one-time value beside it. */
const BINDING_VIEW: CiBindingView = {
  id: 'b-1',
  project_id: 'p-1',
  provider: 'github_actions',
  repository_id: 42,
  repository_full_name: 'acme/storefront-web',
  allowed_ref_patterns: ['refs/heads/main'],
  allowed_workflow_refs: ['acme/storefront-web/.github/workflows/ci.yml@refs/heads/main'],
  status: 'pending_verification',
  created_at: '2026-10-20T10:00:00Z',
  version: 1,
};

const BINDING_CREATED: CiBindingCreatedResponse = {
  binding: BINDING_VIEW,
  binding_challenge: 'egc_9f2c1d7b4a6e8f0a',
};

const TOKEN_VIEW: IngestTokenView = {
  id: 't-1',
  project_id: 'p-1',
  name: 'ci ingest',
  prefix: 'egt_ab12',
  status: 'active',
  scopes: ['run:create', 'event:write'],
  created_at: '2026-10-20T10:05:00Z',
};

const TOKEN_CREATED: IngestTokenCreatedResponse = {
  token: TOKEN_VIEW,
  secret: 'egt_ab12cd34ef56gh78',
  overlap_ends_at: null,
};

const BINDING_BODY: CreateCiBindingRequest = {
  provider: 'github_actions',
  repository_id: 42,
  repository_full_name: 'acme/storefront-web',
  allowed_ref_patterns: ['refs/heads/main'],
  allowed_workflow_refs: ['acme/storefront-web/.github/workflows/ci.yml@refs/heads/main'],
};

const TOKEN_BODY: CreateIngestTokenRequest = { name: 'ci ingest' };

/** 09.2.1-T5.1: the same closed body the step-5 page sends (07.1.1-T3, ruling T62). */
const POLICY_BODY: PolicyRevisionV1 = {
  required_suites: [],
  required_cases: [],
  late_window_minutes: 1440,
  gating_refs: { default_branch: false },
};

const POLICY_REVISION_ID = '018f0000-0000-7000-8000-00000000a004';

const POLICY_REVISION: PolicyRevisionDto = {
  id: POLICY_REVISION_ID,
  project_id: 'p-1',
  revision_no: 4,
  hash: 'sha256:f250bb2c0d1e9a3b5c7d1f2a4b6c8d0e2f4a6b8c0d2e4f6a8b0c2d4e6f8a0b1c',
  state: 'draft',
  body: POLICY_BODY,
  created_by: '01990000-0000-7000-8000-000000000001',
  created_at: '2026-10-26T08:20:00Z',
  version: 1,
};

const ACTIVATION: PolicyActivationDto = {
  project_id: 'p-1',
  policy_revision_id: POLICY_REVISION_ID,
  hash: POLICY_REVISION.hash,
  mode: 'advisory',
  activated_by: '01990000-0000-7000-8000-000000000001',
  activated_at: '2026-10-26T08:22:00Z',
  previous_policy_revision_id: null,
  changed: true,
};

/** 09.2.1-T5.2: the read's answer — the stored pointer, without the POST's two extra members. */
const ACTIVATION_STATE: PolicyActivationStateDto = {
  project_id: ACTIVATION.project_id,
  policy_revision_id: ACTIVATION.policy_revision_id,
  hash: ACTIVATION.hash,
  mode: ACTIVATION.mode,
  activated_by: ACTIVATION.activated_by,
  activated_at: ACTIVATION.activated_at,
};

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
  manifest_hash: 'sha256:018f000000000000000000000000000000000000000000000000000000000000',
  suite_revision_id: '018f0000-0000-7000-8000-00000000b001',
  policy_revision_id: '018f0000-0000-7000-8000-00000000a004',
  environment: {
    revision_id: '018f0000-0000-7000-8000-00000000e001',
    environment_id: '018f0000-0000-7000-8000-00000000e000',
    environment_key: 'staging',
    revision_no: 3,
    revision_hash: 'sha256:018f000100000000000000000000000000000000000000000000000000000000',
  },
  shard_count: 2,
  declared_producers: ['shard-1', 'shard-2'],
  releases: [],
  after_cancel_events: 0,
  state_history: null,
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
    // The tenant the session publishes after sign-in; the tenant interceptor reads it for the
    // routes that name no org on the path.
    TestBed.inject(TenantStore).setOrg('o-1');
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

  it('createCiBinding() posts the binding with the tenant header and answers the challenge once', async () => {
    const created = service.createCiBinding('p-1', BINDING_BODY);
    const request = await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/ci-bindings`);
    expect(request.request.method).toBe('POST');
    // The route is tenant-scoped and the service builds no header itself: the value on the wire is
    // the interceptor's, read from the tenant the session published.
    expect(request.request.headers.get('X-Evidgate-Org')).toBe('o-1');
    expect(request.request.body).toBe(JSON.stringify(BINDING_BODY));
    request.flush(JSON.stringify(BINDING_CREATED), {
      status: 201,
      statusText: '',
      headers: JSON_HEADERS,
    });
    const answer: CiBindingCreatedResponse = await created;
    expect(answer).toEqual(BINDING_CREATED);
    expect(answer.binding_challenge).toBe(BINDING_CREATED.binding_challenge);
  });

  it('createCiBinding() contributes no idempotency key of its own (the route takes none)', async () => {
    // The method has no `Intent` parameter, and the wire proves it: the header that reaches the
    // server is the interceptor's, minted per request. A caller-owned key would repeat on a
    // second submit of the same body — and the response carrying the one-time challenge is never
    // replayed, so a repeated key would only turn the retry into a conflict.
    const first = service.createCiBinding('p-1', BINDING_BODY);
    const firstRequest = await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/ci-bindings`);
    const firstKey = firstRequest.request.headers.get('Idempotency-Key');
    firstRequest.flush(JSON.stringify(BINDING_CREATED), {
      status: 201,
      statusText: '',
      headers: JSON_HEADERS,
    });
    await first;

    const second = service.createCiBinding('p-1', BINDING_BODY);
    const secondRequest = await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/ci-bindings`);
    const secondKey = secondRequest.request.headers.get('Idempotency-Key');
    secondRequest.flush(JSON.stringify(BINDING_CREATED), {
      status: 201,
      statusText: '',
      headers: JSON_HEADERS,
    });
    await second;

    expect(firstKey).not.toBeNull();
    expect(secondKey).not.toBe(firstKey);
  });

  it('createCiBinding() surfaces a 409 envelope as a typed conflict, not a created binding', async () => {
    const created = service.createCiBinding('p-1', BINDING_BODY);
    const envelope = {
      error: {
        code: 'binding_exists',
        message: 'This repository is already bound to the project.',
        request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
        retryable: false,
      },
    };
    (await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/ci-bindings`)).flush(
      JSON.stringify(envelope),
      { status: 409, statusText: '', headers: JSON_HEADERS },
    );

    const cause = await rejectionOf(created);
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('conflict');
      expect(cause.error.code).toBe('binding_exists');
    }
  });

  it('createIngestToken() posts the name and answers the plaintext once', async () => {
    const created = service.createIngestToken('p-1', TOKEN_BODY);
    const request = await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/ingest-tokens`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toBe(JSON.stringify(TOKEN_BODY));
    request.flush(JSON.stringify(TOKEN_CREATED), {
      status: 201,
      statusText: '',
      headers: JSON_HEADERS,
    });
    const answer: IngestTokenCreatedResponse = await created;
    expect(answer).toEqual(TOKEN_CREATED);
    expect(answer.secret).toBe(TOKEN_CREATED.secret);
  });

  it('createIngestToken() surfaces a 403 envelope as a typed forbidden error', async () => {
    const created = service.createIngestToken('p-1', TOKEN_BODY);
    const envelope = {
      error: {
        code: 'forbidden',
        message: 'Missing permission ingest_token:write.',
        request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
        retryable: false,
      },
    };
    (await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/ingest-tokens`)).flush(
      JSON.stringify(envelope),
      { status: 403, statusText: '', headers: JSON_HEADERS },
    );

    const cause = await rejectionOf(created);
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('forbidden');
    }
  });

  it('rotateIngestToken() posts the rotation and answers the successor', async () => {
    const rotated = service.rotateIngestToken('t-1');
    const request = await takeRequest(httpMock, `${API_BASE_URL}/ingest-tokens/t-1/rotate`);
    expect(request.request.method).toBe('POST');
    // The token id is the path parameter `t` of the route, never a query or a body field.
    expect(request.request.urlWithParams).toBe(`${API_BASE_URL}/ingest-tokens/t-1/rotate`);
    request.flush(JSON.stringify(TOKEN_CREATED), {
      status: 200,
      statusText: '',
      headers: JSON_HEADERS,
    });
    const answer: IngestTokenCreatedResponse = await rotated;
    expect(answer.token.id).toBe(TOKEN_VIEW.id);
    expect(answer.secret).toBe(TOKEN_CREATED.secret);
  });

  it('rotateIngestToken() surfaces a 409 envelope as a typed conflict', async () => {
    const rotated = service.rotateIngestToken('t-revoked');
    const envelope = {
      error: {
        code: 'token_revoked',
        message: 'A revoked token cannot be rotated.',
        request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
        retryable: false,
      },
    };
    (await takeRequest(httpMock, `${API_BASE_URL}/ingest-tokens/t-revoked/rotate`)).flush(
      JSON.stringify(envelope),
      { status: 409, statusText: '', headers: JSON_HEADERS },
    );

    const cause = await rejectionOf(rotated);
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('conflict');
      expect(cause.error.code).toBe('token_revoked');
    }
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

  it('readPolicyActivation() reads the stored activation by project id', async () => {
    const read = service.readPolicyActivation('p-1');
    const request = await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/policy-activation`);
    expect(request.request.method).toBe('GET');
    // A read takes no Idempotency-Key and no XSRF header: the contract asks for neither.
    expect(request.request.headers.has('Idempotency-Key')).toBe(false);
    request.flush(JSON.stringify(ACTIVATION_STATE), { headers: JSON_HEADERS });

    await expect(read).resolves.toEqual(ACTIVATION_STATE);
  });

  it('readPolicyActivation() answers null for the contract 404 and only for it', async () => {
    const read = service.readPolicyActivation('p-1');
    (await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/policy-activation`)).flush(
      JSON.stringify({
        error: {
          code: 'not_found',
          message: 'the requested resource does not exist',
          request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
          retryable: false,
          details: [],
        },
      }),
      { status: 404, statusText: '', headers: JSON_HEADERS },
    );

    // One undifferentiated 404 (00 §9.2): never activated, unknown, soft-deleted and foreign all
    // read here, so the service reports "no activation" rather than a not-found error.
    await expect(read).resolves.toBeNull();
  });

  it('readPolicyActivation() re-throws a 403 unchanged, never as an absent activation', async () => {
    const read = service.readPolicyActivation('p-1');
    (await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/policy-activation`)).flush(
      JSON.stringify({
        error: {
          code: 'forbidden',
          message: 'Missing permission policy:read.',
          request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
          retryable: false,
          details: [],
        },
      }),
      { status: 403, statusText: '', headers: JSON_HEADERS },
    );

    const cause = await rejectionOf(read);
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('forbidden');
      expect(cause.error.status).toBe(403);
    }
  });

  it('readPolicyActivation() re-throws a 500 unchanged, so no UI state is invented', async () => {
    const read = service.readPolicyActivation('p-1');
    (await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/policy-activation`)).flush(
      JSON.stringify({
        error: {
          code: 'internal_error',
          message: 'the request could not be completed',
          request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
          retryable: true,
          details: [],
        },
      }),
      { status: 500, statusText: '', headers: JSON_HEADERS },
    );

    const cause = await rejectionOf(read);
    // 500 is the mapper's `unexpected` arm: the status's meaning depends on the body, and the
    // service passes the gateway's error through instead of reading it as "no activation".
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.status).toBe(500);
      expect(cause.error.code).toBe('internal_error');
    }
  });

  it('createPolicyRevision() posts the closed body and reads the stored revision', async () => {
    const created = service.createPolicyRevision('p-1', POLICY_BODY);
    const request = await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/policies`);
    expect(request.request.method).toBe('POST');
    // The SDK serialises the body, so the assertion compares the same text the wire carries.
    expect(request.request.body).toBe(JSON.stringify(POLICY_BODY));
    // Nothing is asserted about `Idempotency-Key` here: the service passes none (this route's
    // idempotency is the content hash), and the header on the wire is the platform interceptor's
    // fallback for untyped mutations (`core/http/idempotency.interceptor.ts`), which owns that
    // behaviour. The activation case below is the one that pins a caller-supplied key.
    request.flush(JSON.stringify(POLICY_REVISION), {
      status: 201,
      statusText: '',
      headers: JSON_HEADERS,
    });

    const answer: PolicyRevisionDto = await created;
    expect(answer.id).toBe(POLICY_REVISION_ID);
    expect(answer.revision_no).toBe(4);
  });

  it('activatePolicy() sends the caller intent key and answers the advisory pointer', async () => {
    const intent = createIntent();
    const activated = service.activatePolicy('p-1', POLICY_REVISION_ID, intent);
    const request = await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/policy-activation`);
    expect(request.request.method).toBe('POST');
    // The mode is the service's own decision: the wizard never offers `enforcing` (07.7.1).
    expect(request.request.body).toBe(
      JSON.stringify({ policy_revision_id: POLICY_REVISION_ID, mode: 'advisory' }),
    );
    expect(request.request.headers.get('Idempotency-Key')).toBe(intent.idempotencyKey);
    request.flush(JSON.stringify(ACTIVATION), { headers: JSON_HEADERS });

    const answer: PolicyActivationDto = await activated;
    expect(answer.mode).toBe('advisory');
    expect(answer.changed).toBe(true);
    expect(answer.policy_revision_id).toBe(POLICY_REVISION_ID);
  });

  it('activatePolicy() surfaces a 409 envelope as a typed conflict, never a value', async () => {
    const activated = service.activatePolicy('p-1', POLICY_REVISION_ID, createIntent());
    const envelope = {
      error: {
        code: 'invalid_transition',
        message: 'A retired policy revision can never be activated again.',
        request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
        retryable: false,
      },
    };
    (await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/policy-activation`)).flush(
      JSON.stringify(envelope),
      { status: 409, statusText: '', headers: JSON_HEADERS },
    );

    const cause = await rejectionOf(activated);
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('conflict');
      expect(cause.error.code).toBe('invalid_transition');
    }
  });
});
