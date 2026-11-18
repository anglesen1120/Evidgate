import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideApi } from '../http/api-client';
import { JSON_HEADERS, takeRequest } from '../../shared/testing/http-data.testing';
import { CaseListDataService } from './case-list-data.service';
import type { CaseListResponse } from '@evidgate/sdk';
import catalogCases from '../../../../../../fixtures/web-api/catalog-cases-list/input.json';

const CASE_LIST_FIXTURE = catalogCases.body as CaseListResponse;

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
});
