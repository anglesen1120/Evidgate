import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideApi } from '../http/api-client';
import { JSON_HEADERS, takeRequest } from '../../shared/testing/http-data.testing';
import { CaseListDataService } from './case-list-data.service';
import type { CaseDto, CaseListResponse } from '@evidgate/sdk';
import catalogCases from '../../../../../../fixtures/web-api/catalog-cases-list/input.json';
import catalogCaseOwner from '../../../../../../fixtures/web-api/catalog-case-owner/input.json';

const CASE_LIST_FIXTURE = catalogCases.body as CaseListResponse;
const OWNER_CASE = catalogCaseOwner.body as CaseDto;
const OWNED_MEMBERSHIP = '018f0000-0000-7000-8000-0000000000c2';

describe('CaseListDataService', () => {
  let service: CaseListDataService;
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideApi(`${location.origin}/v1`),
        provideHttpClientTesting(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    service = TestBed.inject(CaseListDataService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('passes the real SDK path, exact server filters, opaque cursor and page limit', async () => {
    const pending = service.list(
      'project-1',
      { status: 'missing', framework: 'playwright', cursor: 'opaque+/=', limit: 25 },
      new AbortController().signal,
    );
    const request = await takeRequest(http, `${location.origin}/v1/projects/project-1/cases`);
    expect(request.request.method).toBe('GET');
    const params = new URL(request.request.urlWithParams).searchParams;
    expect(Object.fromEntries(params)).toEqual({
      status: 'missing',
      framework: 'playwright',
      cursor: 'opaque+/=',
      limit: '25',
    });
    request.flush(JSON.stringify(CASE_LIST_FIXTURE), { headers: JSON_HEADERS });
    await expect(pending).resolves.toEqual(CASE_LIST_FIXTURE);
  });

  it('returns a typed permission refusal instead of manufacturing rows', async () => {
    const pending = service.list('project-1', { limit: 50 }, new AbortController().signal);
    const rejected = expect(pending).rejects.toMatchObject({
      error: { kind: 'forbidden', status: 403 },
    });
    const request = await takeRequest(http, `${location.origin}/v1/projects/project-1/cases`);
    request.flush(
      JSON.stringify({
        error: { code: 'forbidden', message: 'Not granted', request_id: 'request-1', details: [] },
      }),
      { status: 403, statusText: 'Forbidden', headers: JSON_HEADERS },
    );
    await rejected;
  });

  it('sends the owner PATCH with the quoted ETag and an idempotency key', async () => {
    const pending = service.update(
      OWNER_CASE.id,
      { owner_membership_id: OWNED_MEMBERSHIP },
      new AbortController().signal,
      '"3"',
    );
    const request = await takeRequest(http, `${location.origin}/v1/cases/${OWNER_CASE.id}`);
    expect(request.request.method).toBe('PATCH');
    expect(request.request.headers.get('If-Match')).toBe('"3"');
    expect(request.request.headers.get('Idempotency-Key')).toBeTruthy();
    expect(request.request.body).toEqual({ owner_membership_id: OWNED_MEMBERSHIP });
    request.flush(JSON.stringify(OWNER_CASE), {
      headers: { ...JSON_HEADERS, ETag: '"4"' },
    });
    await expect(pending).resolves.toEqual(OWNER_CASE);
  });

  it('surfaces a stale ETag as the typed precondition failure', async () => {
    const pending = service.update(
      OWNER_CASE.id,
      { owner_membership_id: OWNED_MEMBERSHIP },
      new AbortController().signal,
      '"2"',
    );
    const rejected = expect(pending).rejects.toMatchObject({
      error: { kind: 'precondition_failed', status: 412 },
    });
    const request = await takeRequest(http, `${location.origin}/v1/cases/${OWNER_CASE.id}`);
    request.flush(
      JSON.stringify({
        error: {
          code: 'stale',
          message: 'The case changed since it was read.',
          request_id: 'request-2',
          retryable: false,
          details: [{ path: 'version', reason: 'stale' }],
        },
      }),
      { status: 412, statusText: 'Precondition Failed', headers: JSON_HEADERS },
    );
    await rejected;
  });
});
