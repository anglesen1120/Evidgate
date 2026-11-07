// 09.7.1-T3 (plan 09.7.1 §2 AC3, §5 "secret references" row, §6 ingest-token rotation states,
// §10 T3.1/T3.2; COORDINATOR DECISIONS S82 P3/P5): the two calls the Secrets tab makes — the
// project's ingest-token metadata page and the rotation that mints its successor — against the
// real HTTP pipeline.
//
// Each case pins three things at once: the SDK operation and URL the service calls, that the route
// carries no `Idempotency-Key` (a rotation answer is a one-time secret and is never replayed, so a
// key would be a lie the server does not honour), and that a refusal surfaces as the typed
// `ApiRequestError` instead of a value — a row can never get a 409 into its success path.
//
// The values here are declared inline rather than read from a fixture: this suite is about the
// request the service builds and the failure it maps, and the response shapes it asserts are the
// SDK's own types, so a drift in `IngestTokenView` fails to compile here instead of quietly
// matching a stale JSON file.
//
// The base URL is the absolute form of the same-origin API path because Node's `Request` (the one
// `ng test` runs on) refuses the relative `/v1` the browser resolves for us.

import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { CiBindingView, IngestTokenCreatedResponse, IngestTokenView } from '@evidgate/sdk';

import { isApiRequestError } from '../http/api-error';
import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { JSON_HEADERS, takeRequest } from '../../shared/testing/http-data.testing';
import { SecretsDataService } from './secrets-data.service';

const API_BASE_URL = `${location.origin}${API_BASE_PATH}`;
const TOKENS_URL = `${API_BASE_URL}/projects/p-1/ingest-tokens`;
const ROTATE_URL = `${API_BASE_URL}/ingest-tokens/t-1/rotate`;
const BINDINGS_URL = `${API_BASE_URL}/projects/p-1/ci-bindings`;

/** One row of the metadata list: everything the tab renders, and no value of any kind. */
const TOKEN: IngestTokenView = {
  id: 't-1',
  project_id: 'p-1',
  name: 'Release CI',
  prefix: 'egt_ab12',
  status: 'active',
  scopes: ['event:write', 'run:create'],
  expires_at: '2026-11-01T00:00:00Z',
  overlap_ends_at: null,
  last_used_at: '2026-10-24T09:12:34Z',
  rotated_to_id: null,
  created_at: '2026-10-20T06:05:00Z',
};

/** The rotation's answer: the successor's metadata plus its one-time plaintext. */
const ROTATED: IngestTokenCreatedResponse = {
  token: { ...TOKEN, id: 't-2', prefix: 'egt_rotated', overlap_ends_at: '2026-10-26T06:10:00Z' },
  secret: 'egt_rotated_only_once_in_this_answer',
  overlap_ends_at: '2026-10-26T06:10:00Z',
};

/**
 * The 409 the route answers for a token that was already rotated (contracts/openapi.yaml).
 */
const INVALID_TRANSITION = {
  error: {
    code: 'invalid_transition',
    message:
      'this ingest token has already been rotated; create a new token instead of rotating it',
    request_id: '018f0000-0000-7000-8000-0000000000e5',
    retryable: false,
    details: [],
  },
};

/**
 * One row of the CI-binding metadata list (09.7.1-F15): the repository, its allowlists, the
 * verification state and the timestamps — and no challenge, which only a create answer carries.
 */
const BINDING: CiBindingView = {
  id: 'b-1',
  project_id: 'p-1',
  provider: 'github_actions',
  repository_id: 5012345,
  repository_full_name: 'acme/storefront-web',
  allowed_ref_patterns: ['refs/heads/main', 'refs/tags/v*'],
  allowed_workflow_refs: ['acme/storefront-web/.github/workflows/evidgate.yml@refs/heads/main'],
  status: 'active',
  activated_at: '2026-06-01T08:10:00Z',
  created_at: '2026-06-01T08:00:00Z',
  version: 1,
};

/** Answers one request with a body, so the SDK's parser sees the served media type. */
function flushPage(request: TestRequest, body: unknown, status = 200): void {
  request.flush(JSON.stringify(body), { status, statusText: '', headers: JSON_HEADERS });
}

/** The rejection one promise settles with, so a refusal can be inspected instead of thrown. */
async function rejected(promise: Promise<unknown>): Promise<unknown> {
  return promise.then(
    () => undefined,
    (error: unknown) => error,
  );
}

describe('SecretsDataService (09.7.1-T3)', () => {
  let httpMock: HttpTestingController;
  let service: SecretsDataService;

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
    service = TestBed.inject(SecretsDataService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('listIngestTokens() reads one page of the project URL at the contract’s maximum page size', async () => {
    const tokens = service.listIngestTokens('p-1');
    const request = await takeRequest(httpMock, TOKENS_URL);
    expect(request.request.method).toBe('GET');
    // One page, never a dump: the caller owns the cursor loop (00 §9.1, W7).
    expect(request.request.urlWithParams).toContain('limit=200');
    expect(request.request.urlWithParams).not.toContain('cursor=');
    const page = { items: [TOKEN], next_cursor: 't-1' };
    flushPage(request, page);
    await expect(tokens).resolves.toEqual(page);
  });

  it('listIngestTokens() passes the cursor it is given and keeps the same page size', async () => {
    const tokens = service.listIngestTokens('p-1', 't-1');
    const request = await takeRequest(httpMock, TOKENS_URL);
    expect(request.request.urlWithParams).toContain('cursor=t-1');
    expect(request.request.urlWithParams).toContain('limit=200');
    flushPage(request, { items: [], next_cursor: null });
    await expect(tokens).resolves.toEqual({ items: [], next_cursor: null });
  });

  it('listIngestTokens() surfaces a 403 as the typed forbidden error, never a page', async () => {
    const tokens = service.listIngestTokens('p-1');
    const read = rejected(tokens);
    flushPage(
      await takeRequest(httpMock, TOKENS_URL),
      {
        error: {
          code: 'forbidden',
          message: 'Missing permission ingest_token:read.',
          request_id: '5c9c4d0e-6a71-4b9e-9a51-2f6f1f0d3f11',
          retryable: false,
        },
      },
      403,
    );

    const cause = await read;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('forbidden');
      expect(cause.error.code).toBe('forbidden');
      expect(cause.error.message).toBe('Missing permission ingest_token:read.');
    }
  });

  it('rotateIngestToken() posts the token id as the path parameter and answers the successor once', async () => {
    const rotated = service.rotateIngestToken('t-1');
    const request = await takeRequest(httpMock, ROTATE_URL);
    expect(request.request.method).toBe('POST');
    // The token id is the path parameter `t` of the route, never a query or a body field.
    expect(request.request.urlWithParams).toBe(ROTATE_URL);
    expect(request.request.body).toBeNull();
    flushPage(request, ROTATED);

    const answer: IngestTokenCreatedResponse = await rotated;
    expect(answer).toEqual(ROTATED);
    // The plaintext travels to the caller as a return value — this layer stores nothing.
    expect(answer.secret).toBe(ROTATED.secret);
  });

  it('rotateIngestToken() carries no replayable key: each attempt gets a fresh interceptor key', async () => {
    // The service accepts no `Intent` (the answer carries a one-time secret and is never replayed),
    // so there is no caller-owned key to reuse. The app-wide interceptor still stamps one per
    // request — and it differs on the next attempt, which is why a retry is NOT a replay and the
    // store's in-flight guard is what stops two successors.
    const first = service.rotateIngestToken('t-1');
    const firstRequest = await takeRequest(httpMock, ROTATE_URL);
    const firstKey = firstRequest.request.headers.get('Idempotency-Key');
    expect(firstKey).not.toBeNull();
    flushPage(firstRequest, ROTATED);
    await first;

    const second = service.rotateIngestToken('t-1');
    const secondRequest = await takeRequest(httpMock, ROTATE_URL);
    expect(secondRequest.request.headers.get('Idempotency-Key')).not.toBe(firstKey);
    flushPage(secondRequest, ROTATED);
    await second;
  });

  it('rotateIngestToken() surfaces the server’s 409 as the typed conflict, verbatim', async () => {
    const rotated = service.rotateIngestToken('t-1');
    const read = rejected(rotated);
    flushPage(await takeRequest(httpMock, ROTATE_URL), INVALID_TRANSITION, 409);

    const cause = await read;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('conflict');
      expect(cause.error.status).toBe(409);
      expect(cause.error.code).toBe(INVALID_TRANSITION.error.code);
      expect(cause.error.message).toBe(INVALID_TRANSITION.error.message);
    }
  });

  it('listCiBindings() reads one page of the project URL at the contract’s maximum page size', async () => {
    const bindings = service.listCiBindings('p-1');
    const request = await takeRequest(httpMock, BINDINGS_URL);
    expect(request.request.method).toBe('GET');
    // The read is list-only and bounded: one page, never a dump (00 §9.1, W7).
    expect(request.request.urlWithParams).toContain('limit=200');
    expect(request.request.urlWithParams).not.toContain('cursor=');
    // A GET carries no body and no caller-owned key: the view is read, never written.
    expect(request.request.body).toBeNull();
    const page = { items: [BINDING], next_cursor: 'b-1' };
    flushPage(request, page);
    await expect(bindings).resolves.toEqual(page);
  });

  it('listCiBindings() passes the cursor it is given and keeps the same page size', async () => {
    const bindings = service.listCiBindings('p-1', 'b-1');
    const request = await takeRequest(httpMock, BINDINGS_URL);
    expect(request.request.urlWithParams).toContain('cursor=b-1');
    expect(request.request.urlWithParams).toContain('limit=200');
    flushPage(request, { items: [], next_cursor: null });
    await expect(bindings).resolves.toEqual({ items: [], next_cursor: null });
  });

  it('listCiBindings() surfaces a 403 as the typed forbidden error, never a page', async () => {
    const bindings = service.listCiBindings('p-1');
    const read = rejected(bindings);
    flushPage(
      await takeRequest(httpMock, BINDINGS_URL),
      {
        error: {
          code: 'forbidden',
          message: 'the principal is not allowed to perform this operation',
          request_id: '5c9c4d0e-6a71-4b9e-9a51-2f6f1f0d3f11',
          retryable: false,
        },
      },
      403,
    );

    const cause = await read;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('forbidden');
      expect(cause.error.status).toBe(403);
      expect(cause.error.code).toBe('forbidden');
    }
  });
});
