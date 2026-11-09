import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { HealingSwitchResolutionView, HealingSwitchView } from '@evidgate/sdk';

import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { createIntent } from '../http/intent';
import { JSON_HEADERS, takeRequest } from '../../shared/testing/http-data.testing';
import { HealingSwitchDataService } from './healing-switch-data.service';

const API_BASE_URL = `${location.origin}${API_BASE_PATH}`;
const HEALING_URL = `${API_BASE_URL}/healing/switch`;

const RESOLUTION: HealingSwitchResolutionView = {
  system: {
    scope: 'system',
    project_id: null,
    mode: 'shadow',
    reason: null,
    set_by: null,
    at: '2026-11-01T00:00:00Z',
    version: 3,
  },
  tenant: {
    scope: 'tenant',
    project_id: null,
    mode: 'disabled',
    reason: 'Region outage',
    set_by: 'member-1',
    at: '2026-11-02T00:00:00Z',
    version: 7,
  },
  project: null,
  resolved: 'disabled',
  dependency_health: { llm: null, knowledge_store: null },
};

const STORED: HealingSwitchView = {
  scope: 'tenant',
  project_id: null,
  mode: 'disabled',
  reason: 'Region outage',
  set_by: 'member-1',
  at: '2026-11-02T00:00:00Z',
  version: 8,
};

function flush(request: TestRequest, body: unknown): void {
  request.flush(JSON.stringify(body), { status: 200, statusText: '', headers: JSON_HEADERS });
}

describe('HealingSwitchDataService generated operations', () => {
  let service: HealingSwitchDataService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideApi(API_BASE_URL),
        provideHttpClientTesting(),
        provideZonelessChangeDetection(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    service = TestBed.inject(HealingSwitchDataService);
    httpMock = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    httpMock.verify();
    TestBed.resetTestingModule();
  });

  it('reads the resolution for the named project and sends no mutation headers', async () => {
    const pending = service.read('p-1');
    // The shared helper captures the request asynchronously; the exact endpoint is asserted next,
    // because the adapter keeps the query string inside `HttpRequest.url`.
    const request = await takeRequest(httpMock, HEALING_URL);
    expect(request.request.method).toBe('GET');
    const url = new URL(request.request.urlWithParams);
    expect(`${url.origin}${url.pathname}`).toBe(HEALING_URL);
    expect(url.searchParams.get('project_id')).toBe('p-1');
    expect(request.request.headers.has('Idempotency-Key')).toBe(false);
    flush(request, RESOLUTION);
    await expect(pending).resolves.toEqual(RESOLUTION);
  });

  it('writes the scope with the intent key and answers the stored row', async () => {
    const intent = createIntent();
    const body = { scope: 'tenant' as const, mode: 'disabled', reason: 'Region outage' };
    const pending = service.set(body, intent);
    const request = await takeRequest(httpMock, HEALING_URL);
    expect(request.request.method).toBe('PUT');
    expect(request.request.urlWithParams).toBe(HEALING_URL);
    expect(request.request.body).toBe(JSON.stringify(body));
    expect(request.request.headers.get('Idempotency-Key')).toBe(intent.idempotencyKey);
    // This route has no If-Match: concurrency is the row `version` carried in the answer.
    expect(request.request.headers.has('If-Match')).toBe(false);
    flush(request, STORED);
    await expect(pending).resolves.toEqual(STORED);
  });
});
