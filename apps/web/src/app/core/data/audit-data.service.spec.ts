// 09.7.1-T6 (plan 09.7.1 §5 audit row, §8 input-limits row, §10 T6.1/T6.2; 02.7.1 §5): the audit
// page read and the query it puts on the wire, against the real stack.
//
// The CSV export walks cursors through this one operation (AC7 caps that walk at 10,000 rows), so
// the two facts the walk depends on are pinned here: the cursor reaches the request, and a filter
// the user left alone is absent from the URL instead of travelling as an empty `action=`, which
// the server would read as a filter matching nothing.
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
import type { AuditEntryDto } from '@evidgate/sdk';

import { isApiRequestError } from '../http/api-error';
import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { AuditDataService } from './audit-data.service';

const API_BASE_URL = `${location.origin}${API_BASE_PATH}`;
const AUDIT_URL = `${API_BASE_URL}/orgs/o-1/audit`;

/** The served media type: the adapter reads bodies as text and `flush` does not derive it. */
const JSON_HEADERS = { 'Content-Type': 'application/json' } as const;

function auditEntry(seq: number, actorType: AuditEntryDto['actor_type']): AuditEntryDto {
  return {
    id: `018f0000-0000-7000-8000-00000000${String(seq)}`,
    seq,
    actor_type: actorType,
    actor_id: '018f0000-0000-7000-8000-0000000000e1',
    action: 'role.grant',
    object_type: 'membership',
    object_id: '018f0000-0000-7000-8000-0000000000c2',
    before_hash: null,
    after_hash: null,
    request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
    trace_id: '4bf92f3577b34da6a3ce929d0e0e4736',
    metadata: null,
    prev_hash: `${'a'.repeat(63)}1`,
    entry_hash: `${'b'.repeat(63)}2`,
    created_at: '2026-10-18T11:42:07Z',
  };
}

const LAST_ENTRY = auditEntry(1046, 'user');

/**
 * The SDK -> adapter -> `HttpClient` pipeline is asynchronous, so a request is registered after
 * the call starts — and after a *page* is flushed the next one waits on the blob parse, which is
 * a macrotask in jsdom, so yielding only microtasks stalls the second page.
 */
async function takeRequest(
  httpMock: HttpTestingController,
  match: string | ((request: HttpRequest<unknown>) => boolean),
): Promise<TestRequest> {
  // A string match is a URL prefix: a cursor page's `url` stays the bare path while its
  // `urlWithParams` carries `?cursor=…`.
  const matches = (candidate: HttpRequest<unknown>): boolean =>
    typeof match === 'string' ? candidate.url.startsWith(match) : match(candidate);
  for (let attempt = 0; attempt < 50; attempt += 1) {
    const [request] = httpMock.match(matches);
    if (request !== undefined) {
      return request;
    }
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  }
  throw new Error('no HTTP request reached HttpTestingController');
}

/** Answers one page with a body, so the SDK's parser sees the served media type. */
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

const EMPTY_PAGE = { items: [], next_cursor: null };

describe('AuditDataService (09.7.1-T6)', () => {
  let httpMock: HttpTestingController;
  let service: AuditDataService;

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
    service = TestBed.inject(AuditDataService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('list() reads one page of the org audit URL and passes the cursor it is given', async () => {
    const page = service.list('o-1');
    const first = await takeRequest(httpMock, AUDIT_URL);
    expect(first.request.method).toBe('GET');
    expect(first.request.url).toBe(AUDIT_URL);
    // No filter and no cursor: not even an empty `?`.
    expect(new URL(first.request.urlWithParams).search).toBe('');
    const body = { items: [LAST_ENTRY], next_cursor: 'audit-1041' };
    flushPage(first, body);
    await expect(page).resolves.toEqual(body);

    const second = service.list('o-1', {}, 'audit-1041');
    const request = await takeRequest(httpMock, AUDIT_URL);
    // The client folds the query into the URL it hands the request, so the cursor is checked on
    // the parsed search rather than by comparing the bare path.
    const cursorUrl = new URL(request.request.urlWithParams);
    expect(cursorUrl.pathname).toBe(new URL(AUDIT_URL).pathname);
    expect(cursorUrl.searchParams.get('cursor')).toBe('audit-1041');
    flushPage(request, EMPTY_PAGE);
    await expect(second).resolves.toEqual(EMPTY_PAGE);
  });

  it('list() sends every filter it is given as its own query parameter', async () => {
    const page = service.list(
      'o-1',
      {
        actor_type: 'platform_operator',
        action: 'role.grant',
        object_type: 'membership',
        from: '2026-10-01T00:00:00Z',
        to: '2026-11-01T00:00:00Z',
        limit: 200,
      },
      'audit-1046',
    );

    const request = await takeRequest(httpMock, AUDIT_URL);
    const params = new URL(request.request.urlWithParams).searchParams;
    expect(params.get('actor_type')).toBe('platform_operator');
    expect(params.get('action')).toBe('role.grant');
    expect(params.get('object_type')).toBe('membership');
    expect(params.get('from')).toBe('2026-10-01T00:00:00Z');
    expect(params.get('to')).toBe('2026-11-01T00:00:00Z');
    expect(params.get('limit')).toBe('200');
    expect(params.get('cursor')).toBe('audit-1046');

    flushPage(request, EMPTY_PAGE);
    await expect(page).resolves.toEqual(EMPTY_PAGE);
  });

  it('list() leaves an empty filter off the URL instead of sending name=', async () => {
    // `actor_type` has no empty member in its enum, so `''` can only arrive for the free-text and
    // date members; the audit store omits an unset actor selection itself.
    const page = service.list('o-1', { action: '', object_type: '', from: '', to: '' });

    const request = await takeRequest(httpMock, AUDIT_URL);
    const url = new URL(request.request.urlWithParams);
    // Not merely an empty value per name: an `action=` is a filter that matches nothing.
    expect(url.search).toBe('');
    for (const name of ['actor_type', 'action', 'object_type', 'from', 'to']) {
      expect(url.searchParams.has(name), `${name} reached the URL as an empty filter`).toBe(false);
    }

    flushPage(request, EMPTY_PAGE);
    await expect(page).resolves.toEqual(EMPTY_PAGE);
  });

  it('list() surfaces a 403 as the typed forbidden error, never a page', async () => {
    const page = rejected(service.list('other-tenant'));

    flushPage(
      await takeRequest(httpMock, `${API_BASE_URL}/orgs/other-tenant/audit`),
      {
        error: {
          code: 'forbidden',
          message: 'Missing audit:read.',
          request_id: '5c9c4d0e-6a71-4b9e-9a51-2f6f1f0d3f11',
          retryable: false,
        },
      },
      403,
    );

    const cause = await page;
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('forbidden');
      expect(cause.error.code).toBe('forbidden');
    }
  });
});
