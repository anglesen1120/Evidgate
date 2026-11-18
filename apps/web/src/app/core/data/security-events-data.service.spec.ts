import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { isApiRequestError } from '../http/api-error';
import { JSON_HEADERS, takeRequest } from '../../shared/testing/http-data.testing';
import { SecurityEventsDataService } from './security-events-data.service';

const baseUrl = `${location.origin}${API_BASE_PATH}`;
const url = `${baseUrl}/orgs/org1/security-events`;
describe('SecurityEventsDataService', () => {
  let http: HttpTestingController;
  let data: SecurityEventsDataService;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideApi(baseUrl),
        provideHttpClientTesting(),
        provideZonelessChangeDetection(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    data = TestBed.inject(SecurityEventsDataService);
  });
  afterEach(() => {
    http.verify();
    TestBed.resetTestingModule();
  });
  it('sends exact supported filters and the opaque cursor through the SDK', async () => {
    const query = {
      project_id: 'project1',
      type: 'support_access',
      severity: 'critical',
      from: '2026-01-01T00:00:00Z',
      to: '2026-02-01T00:00:00Z',
      cursor: 'opaque+cursor',
      limit: 50,
    } as const;
    const pending = data.list('org1', query);
    const request = await takeRequest(http, url);
    const params = new URL(request.request.urlWithParams).searchParams;
    for (const [key, value] of Object.entries(query)) expect(params.get(key)).toBe(String(value));
    const body = { items: [], next_cursor: null };
    request.flush(JSON.stringify(body), { headers: JSON_HEADERS });
    await expect(pending).resolves.toEqual(body);
  });
  it('omits unselected filters on the wire', async () => {
    const pending = data.list('org1', { limit: 50 });
    const request = await takeRequest(http, url);
    expect([...new URL(request.request.urlWithParams).searchParams.keys()]).toEqual(['limit']);
    request.flush(JSON.stringify({ items: [], next_cursor: null }), { headers: JSON_HEADERS });
    await pending;
  });
  it('preserves a forbidden response as an error instead of an empty page', async () => {
    const pending = data.list('org1', { limit: 50 }).catch((error: unknown) => error);
    const request = await takeRequest(http, url);
    request.flush(
      JSON.stringify({
        error: { code: 'forbidden', message: 'Denied', request_id: 'request1', retryable: false },
      }),
      { status: 403, statusText: 'Forbidden', headers: JSON_HEADERS },
    );
    const error = await pending;
    expect(isApiRequestError(error)).toBe(true);
    if (isApiRequestError(error)) expect(error.error.kind).toBe('forbidden');
  });
});
