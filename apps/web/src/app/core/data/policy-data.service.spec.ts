// 09.7.1-T4 (plan 09.7.1 §2 AC1/AC5, §10 T4; S83 P2/P4): the three calls the Policy tab makes —
// one revision with its body, one cursor page of a project's revisions, and the append that
// publishes a body — against the real HTTP pipeline.
//
// Each case pins what only this layer decides: the URL and query the SDK operation builds, that the
// list's `ETag` reaches the caller as the head revision id (quotes and the weak marker stripped, an
// absent header as `null`), that a publish quotes that id in `If-Match` in the form the route's own
// `ETag` has (`PolicyIfMatch.TryParse` trims the quotes), that a `201` and a `200` are told apart by
// the status rather than the body, and that every refusal surfaces as the typed `ApiRequestError`
// instead of a value.
//
// The values here are declared inline rather than read from a fixture: this suite is about the
// request the service builds and the failure it maps, and the shapes it asserts are the SDK's own
// types, so a drift in `PolicyRevisionDto` fails to compile here instead of quietly matching a
// stale JSON file.
//
// The base URL is the absolute form of the same-origin API path because Node's `Request` (the one
// `ng test` runs on) refuses the relative `/v1` the browser resolves for us.

import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type {
  PolicyActivationStateDto,
  PolicyRevisionDto,
  PolicyRevisionSummaryDto,
} from '@evidgate/sdk';

import { isApiRequestError } from '../http/api-error';
import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { JSON_HEADERS, takeRequest } from '../../shared/testing/http-data.testing';
import { PolicyDataService } from './policy-data.service';

const API_BASE_URL = `${location.origin}${API_BASE_PATH}`;
const REVISION_ID = '018f0000-0000-7000-8000-0000000000a1';
const REVISION_URL = `${API_BASE_URL}/policies/${REVISION_ID}`;
const LIST_URL = `${API_BASE_URL}/policies/${REVISION_ID}/revisions`;
const PUBLISH_URL = `${API_BASE_URL}/projects/p-1/policies`;
const ACTIVATION_URL = `${API_BASE_URL}/projects/p-1/policy-activation`;
const HEAD_URL = `${API_BASE_URL}/projects/p-1/policies/head`;

/** A stored revision, as `GET /v1/policies/{id}` answers it: the body included. */
const REVISION: PolicyRevisionDto = {
  id: REVISION_ID,
  project_id: 'p-1',
  revision_no: 3,
  hash: 'sha256:4d5e6f',
  state: 'active',
  body: { flaky_budget: 2, critical_cases: ['checkout/card-declined'] },
  created_by: 'u-1',
  created_at: '2026-10-20T06:05:00Z',
  version: 1,
};

/** One list row: a revision without its body, as the list route serves them. */
const ROW: PolicyRevisionSummaryDto = {
  id: REVISION_ID,
  revision_no: 3,
  hash: REVISION.hash,
  state: 'active',
  created_by: REVISION.created_by,
  created_at: REVISION.created_at,
  version: 1,
};

/** The activation in force, as `GET /v1/projects/{p}/policy-activation` answers it. */
const ACTIVATION: PolicyActivationStateDto = {
  project_id: 'p-1',
  policy_revision_id: REVISION_ID,
  hash: REVISION.hash,
  mode: 'advisory',
  activated_by: 'u-1',
  activated_at: '2026-10-26T08:22:00Z',
};

/** The 412 the append route answers for a stale `If-Match` (PolicyErrors.Stale). */
const STALE = {
  error: {
    code: 'precondition_failed',
    message: "the project's policy head changed since it was read",
    request_id: '018f0000-0000-7000-8000-0000000000e5',
    retryable: false,
    details: [
      { path: 'If-Match', reason: 'stale' },
      { path: 'revision_id', reason: '018f0000-0000-7000-8000-0000000000b2' },
    ],
  },
};

/** Answers one request with a body, so the SDK's parser sees the served media type. */
function flushPage(request: TestRequest, body: unknown, status = 200, etag?: string): void {
  const headers: Record<string, string> = { ...JSON_HEADERS };
  if (etag !== undefined) {
    headers['ETag'] = etag;
  }
  request.flush(JSON.stringify(body), { status, statusText: '', headers });
}

/** The rejection one promise settles with, so a refusal can be inspected instead of thrown. */
async function rejected(promise: Promise<unknown>): Promise<unknown> {
  return promise.then(
    () => undefined,
    (error: unknown) => error,
  );
}

describe('PolicyDataService (09.7.1-T4)', () => {
  let httpMock: HttpTestingController;
  let service: PolicyDataService;

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
    service = TestBed.inject(PolicyDataService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('getRevision() reads the revision route with the id as its path parameter', async () => {
    const read = service.getRevision(REVISION_ID);
    const request = await takeRequest(httpMock, REVISION_URL);
    expect(request.request.method).toBe('GET');
    expect(request.request.urlWithParams).toBe(REVISION_URL);
    flushPage(request, REVISION);

    await expect(read).resolves.toEqual(REVISION);
  });

  it('getRevision() surfaces a 404 as the typed not_found error, never a revision', async () => {
    const read = rejected(service.getRevision(REVISION_ID));
    flushPage(
      await takeRequest(httpMock, REVISION_URL),
      {
        error: {
          code: 'not_found',
          message: 'The policy revision was not found.',
          request_id: '018f0000-0000-7000-8000-0000000000e6',
          retryable: false,
        },
      },
      404,
    );

    const cause = await read;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('not_found');
      expect(cause.error.message).toBe('The policy revision was not found.');
    }
  });

  it('listRevisions() reads one page at the contract’s maximum page size', async () => {
    const read = service.listRevisions(REVISION_ID);
    const request = await takeRequest(httpMock, LIST_URL);
    expect(request.request.method).toBe('GET');
    // One page, never a dump: the caller owns the cursor loop (00 §9.1, W7).
    expect(request.request.urlWithParams).toContain('limit=200');
    expect(request.request.urlWithParams).not.toContain('cursor=');
    flushPage(request, { items: [ROW], next_cursor: 'c-2' });

    await expect(read).resolves.toEqual({ items: [ROW], nextCursor: 'c-2', headRevisionId: null });
  });

  it('listRevisions() passes the cursor it is given and keeps the same page size', async () => {
    const read = service.listRevisions(REVISION_ID, 'c-2');
    const request = await takeRequest(httpMock, LIST_URL);
    expect(request.request.urlWithParams).toContain('cursor=c-2');
    expect(request.request.urlWithParams).toContain('limit=200');
    flushPage(request, { items: [], next_cursor: null });

    await expect(read).resolves.toEqual({ items: [], nextCursor: null, headRevisionId: null });
  });

  it('listRevisions() hands back the ETag as the head, unquoted', async () => {
    const head = '018f0000-0000-7000-8000-0000000000b2';
    const served = { items: [ROW], next_cursor: null };
    const read = service.listRevisions(REVISION_ID);
    // The route sets `ETag = "\"{head}\""` (PolicyEndpoints.ListPolicyRevisionsAsync).
    flushPage(await takeRequest(httpMock, LIST_URL), served, 200, `"${head}"`);

    await expect(read).resolves.toEqual({
      items: [ROW],
      nextCursor: null,
      headRevisionId: head,
    });
  });

  it('listRevisions() strips a weak marker and answers null without one', async () => {
    const head = '018f0000-0000-7000-8000-0000000000b3';
    const served = { items: [], next_cursor: null };
    const weak = service.listRevisions(REVISION_ID);
    flushPage(await takeRequest(httpMock, LIST_URL), served, 200, `W/"${head}"`);
    await expect(weak).resolves.toMatchObject({ headRevisionId: head });

    const absent = service.listRevisions(REVISION_ID);
    flushPage(await takeRequest(httpMock, LIST_URL), served);
    await expect(absent).resolves.toMatchObject({ headRevisionId: null });
  });

  it('publish() quotes the head it is given in If-Match and reads a 201 as created', async () => {
    const head = '018f0000-0000-7000-8000-0000000000b4';
    const publish = service.publish('p-1', { flaky_budget: 3 }, head);
    const request = await takeRequest(httpMock, PUBLISH_URL);
    expect(request.request.method).toBe('POST');
    // The adapter hands `HttpClient` the SDK's JSON text, not an object.
    expect(JSON.parse(request.request.body as string)).toEqual({ flaky_budget: 3 });
    // The form the route's own ETag has; `PolicyIfMatch.TryParse` trims the quotes.
    expect(request.request.headers.get('If-Match')).toBe(`"${head}"`);
    flushPage(request, { ...REVISION, revision_no: 4 }, 201);

    await expect(publish).resolves.toMatchObject({ created: true });
    await expect(publish).resolves.toMatchObject({ revision: { revision_no: 4 } });
  });

  it('publish() reads a 200 as the unchanged revision already on file', async () => {
    const publish = service.publish('p-1', REVISION.body, REVISION_ID);
    const request = await takeRequest(httpMock, PUBLISH_URL);
    flushPage(request, REVISION, 200);

    await expect(publish).resolves.toEqual({ revision: REVISION, created: false });
  });

  it('publish() sends no If-Match at all when there is no head to quote', async () => {
    const publish = service.publish('p-1', { flaky_budget: 1 }, null);
    const request = await takeRequest(httpMock, PUBLISH_URL);
    // Absent means "no precondition check" (00 §9.1); an empty header would be a malformed one.
    expect(request.request.headers.has('If-Match')).toBe(false);
    flushPage(request, REVISION, 200);

    await publish;
  });

  it('publish() surfaces a stale 412 with the server’s details', async () => {
    const publish = rejected(service.publish('p-1', { flaky_budget: 3 }, REVISION_ID));
    flushPage(await takeRequest(httpMock, PUBLISH_URL), STALE, 412);

    const cause = await publish;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('precondition_failed');
      expect(cause.error.status).toBe(412);
      expect(cause.error.details).toEqual(STALE.error.details);
    }
  });

  it('getActivation() reads the project activation route', async () => {
    const read = service.getActivation('p-1');
    const request = await takeRequest(httpMock, ACTIVATION_URL);
    expect(request.request.method).toBe('GET');
    expect(request.request.urlWithParams).toBe(ACTIVATION_URL);
    flushPage(request, ACTIVATION);

    await expect(read).resolves.toEqual(ACTIVATION);
  });

  it('getActivation() surfaces a 404 as the typed not_found error, never an activation', async () => {
    const read = rejected(service.getActivation('p-1'));
    flushPage(
      await takeRequest(httpMock, ACTIVATION_URL),
      {
        error: {
          code: 'not_found',
          message: 'No activation.',
          request_id: 'r-1',
          retryable: false,
        },
      },
      404,
    );

    const cause = await read;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('not_found');
    }
  });

  it('getActivation() surfaces a 403 and a 429 as their own typed kinds', async () => {
    for (const [status, kind] of [
      [403, 'forbidden'],
      [429, 'rate_limited'],
    ] as const) {
      const read = rejected(service.getActivation('p-1'));
      flushPage(
        await takeRequest(httpMock, ACTIVATION_URL),
        { error: { code: kind, message: 'Refused.', request_id: 'r-1', retryable: false } },
        status,
      );
      const cause = await read;
      expect(isApiRequestError(cause)).toBe(true);
      if (isApiRequestError(cause)) {
        expect(cause.error.kind).toBe(kind);
        expect(cause.error.status).toBe(status);
      }
    }
  });

  it('getHead() reads the head route and keeps its ETag as the head revision id', async () => {
    const head = '018f0000-0000-7000-8000-00000000a003';
    const read = service.getHead('p-1');
    const request = await takeRequest(httpMock, HEAD_URL);
    expect(request.request.method).toBe('GET');
    expect(request.request.urlWithParams).toBe(HEAD_URL);
    flushPage(request, ROW, 200, `"${head}"`);

    await expect(read).resolves.toEqual({ revision: ROW, headRevisionId: head });
  });

  it('getHead() answers a null head when the route names no ETag', async () => {
    const read = service.getHead('p-1');
    flushPage(await takeRequest(httpMock, HEAD_URL), ROW);

    await expect(read).resolves.toEqual({ revision: ROW, headRevisionId: null });
  });

  it('getHead() surfaces a 404 as the typed not_found error, never a revision', async () => {
    const read = rejected(service.getHead('p-1'));
    flushPage(
      await takeRequest(httpMock, HEAD_URL),
      {
        error: { code: 'not_found', message: 'No revisions.', request_id: 'r-2', retryable: false },
      },
      404,
    );

    const cause = await read;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('not_found');
    }
  });
});
