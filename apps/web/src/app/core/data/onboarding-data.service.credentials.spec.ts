// Step 2's secret-bearing creates: the binding and the ingest token, each with the tenant header
// or the body the contract asks for, plus the rotation and the refusal arms they surface.

import { HttpTestingController } from '@angular/common/http/testing';
import type { CiBindingCreatedResponse, IngestTokenCreatedResponse } from '@evidgate/sdk';

import { isApiRequestError } from '../http/api-error';
import {
  EXACT_MATCH_PROFILE,
  JSON_HEADERS,
  takeRequestWith,
} from '../../shared/testing/http-data.testing';
import { OnboardingDataService } from './onboarding-data.service';
import {
  API_BASE_URL,
  BINDING_BODY,
  BINDING_CREATED,
  NO_BINDINGS,
  NO_TOKENS,
  TOKEN_BODY,
  TOKEN_CREATED,
  TOKEN_VIEW,
  rejectionOf,
  setupOnboardingDataService,
} from './onboarding-data.service.test-support';

const takeRequest = takeRequestWith(EXACT_MATCH_PROFILE);

describe('OnboardingDataService (09.2.1-T1.1/T1.2)', () => {
  let httpMock: HttpTestingController;
  let service: OnboardingDataService;

  beforeEach(() => {
    ({ httpMock, service } = setupOnboardingDataService());
  });

  afterEach(() => {
    httpMock.verify();
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
});
