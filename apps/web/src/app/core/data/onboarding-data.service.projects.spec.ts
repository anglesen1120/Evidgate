// Project reads and the create: the page limit the derivation asks for, the by-id read with its
// 404 arm, and the intent key the create replays on a caller retry.

import { HttpTestingController } from '@angular/common/http/testing';

import { isApiRequestError } from '../http/api-error';
import { createIntent } from '../http/intent';
import { OnboardingDataService } from './onboarding-data.service';
import {
  API_BASE_URL,
  JSON_HEADERS,
  PROJECT,
  PROJECT_PAGE,
  PROJECTS_URL,
  rejectionOf,
  setupOnboardingDataService,
  takeRequest,
} from './onboarding-data.service.test-support';

describe('OnboardingDataService (09.2.1-T1.1/T1.2)', () => {
  let httpMock: HttpTestingController;
  let service: OnboardingDataService;

  beforeEach(() => {
    ({ httpMock, service } = setupOnboardingDataService());
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
});
