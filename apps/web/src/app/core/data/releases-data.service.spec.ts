import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ReleaseDetail } from '@evidgate/sdk';

import { isApiRequestError } from '../http/api-error';
import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import {
  EXACT_MATCH_PROFILE,
  JSON_HEADERS,
  takeRequestWith,
} from '../../shared/testing/http-data.testing';
import { ReleasesDataService } from './releases-data.service';

const takeRequest = takeRequestWith(EXACT_MATCH_PROFILE);
const API_BASE_URL = `${location.origin}${API_BASE_PATH}`;
const RELEASE_URL = `${API_BASE_URL}/releases/rel-1`;

const RELEASE: ReleaseDetail = {
  release_id: 'rel-1',
  project_id: 'p-1',
  name: 'Web 2026.11',
  state: 'active',
  created_by: 'user:018f0000-0000-7000-8000-000000000001',
  created_at: '2026-11-01T00:00:00Z',
  version: 2,
  components: [
    {
      component_key: 'web',
      digest: `sha256:${'a'.repeat(64)}`,
      environment_revision_id: '018f0000-0000-7000-8000-00000000e001',
    },
    {
      component_key: 'worker',
      digest: `sha256:${'b'.repeat(64)}`,
      environment_revision_id: '018f0000-0000-7000-8000-00000000e002',
    },
  ],
  bound_runs: [
    {
      run_id: '018f0000-0000-7000-8000-000000000071',
      component_key: 'web',
      bound_at: '2026-11-01T00:00:01Z',
    },
  ],
};

describe('ReleasesDataService', () => {
  let httpMock: HttpTestingController;
  let service: ReleasesDataService;

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
    service = TestBed.inject(ReleasesDataService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('get() calls GET on the release URL and returns the served document', async () => {
    const release = service.get('rel-1');
    const request = await takeRequest(httpMock, RELEASE_URL);
    expect(request.request.method).toBe('GET');
    request.flush(JSON.stringify(RELEASE), { headers: JSON_HEADERS });
    await expect(release).resolves.toEqual(RELEASE);
  });

  it('get() sends no Idempotency-Key, being a read', async () => {
    const release = service.get('rel-1');
    const request = await takeRequest(httpMock, RELEASE_URL);
    expect(request.request.headers.has('Idempotency-Key')).toBe(false);
    request.flush(JSON.stringify(RELEASE), { headers: JSON_HEADERS });
    await release;
  });

  it('get() surfaces a 404 envelope as a typed not_found refusal', async () => {
    const release = service.get('other-tenant');
    const envelope = {
      error: {
        code: 'not_found',
        message: 'Unknown release.',
        request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
        retryable: false,
      },
    };
    (await takeRequest(httpMock, `${API_BASE_URL}/releases/other-tenant`)).flush(
      JSON.stringify(envelope),
      { status: 404, statusText: '', headers: { 'Content-Type': 'application/json' } },
    );

    const cause: unknown = await release.then(
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
