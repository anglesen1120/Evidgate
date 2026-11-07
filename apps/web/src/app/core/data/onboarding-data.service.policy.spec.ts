// Step 5's policy surface: the stored activation read with its null and error arms, the
// revision create, and the activation the caller's intent key covers.

import { HttpTestingController } from '@angular/common/http/testing';
import type { PolicyActivationDto, PolicyRevisionDto } from '@evidgate/sdk';

import { isApiRequestError } from '../http/api-error';
import { createIntent } from '../http/intent';
import { OnboardingDataService } from './onboarding-data.service';
import {
  ACTIVATION,
  ACTIVATION_STATE,
  API_BASE_URL,
  JSON_HEADERS,
  POLICY_BODY,
  POLICY_REVISION,
  POLICY_REVISION_ID,
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

  it('readPolicyActivation() reads the stored activation by project id', async () => {
    const read = service.readPolicyActivation('p-1');
    const request = await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/policy-activation`);
    expect(request.request.method).toBe('GET');
    // A read takes no Idempotency-Key and no XSRF header: the contract asks for neither.
    expect(request.request.headers.has('Idempotency-Key')).toBe(false);
    request.flush(JSON.stringify(ACTIVATION_STATE), { headers: JSON_HEADERS });

    await expect(read).resolves.toEqual(ACTIVATION_STATE);
  });

  it('readPolicyActivation() answers null for the contract 404 and only for it', async () => {
    const read = service.readPolicyActivation('p-1');
    (await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/policy-activation`)).flush(
      JSON.stringify({
        error: {
          code: 'not_found',
          message: 'the requested resource does not exist',
          request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
          retryable: false,
          details: [],
        },
      }),
      { status: 404, statusText: '', headers: JSON_HEADERS },
    );

    // One undifferentiated 404 (00 §9.2): never activated, unknown, soft-deleted and foreign all
    // read here, so the service reports "no activation" rather than a not-found error.
    await expect(read).resolves.toBeNull();
  });

  it('readPolicyActivation() re-throws a 403 unchanged, never as an absent activation', async () => {
    const read = service.readPolicyActivation('p-1');
    (await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/policy-activation`)).flush(
      JSON.stringify({
        error: {
          code: 'forbidden',
          message: 'Missing permission policy:read.',
          request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
          retryable: false,
          details: [],
        },
      }),
      { status: 403, statusText: '', headers: JSON_HEADERS },
    );

    const cause = await rejectionOf(read);
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('forbidden');
      expect(cause.error.status).toBe(403);
    }
  });

  it('readPolicyActivation() re-throws a 500 unchanged, so no UI state is invented', async () => {
    const read = service.readPolicyActivation('p-1');
    (await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/policy-activation`)).flush(
      JSON.stringify({
        error: {
          code: 'internal_error',
          message: 'the request could not be completed',
          request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
          retryable: true,
          details: [],
        },
      }),
      { status: 500, statusText: '', headers: JSON_HEADERS },
    );

    const cause = await rejectionOf(read);
    // 500 is the mapper's `unexpected` arm: the status's meaning depends on the body, and the
    // service passes the gateway's error through instead of reading it as "no activation".
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.status).toBe(500);
      expect(cause.error.code).toBe('internal_error');
    }
  });

  it('createPolicyRevision() posts the closed body and reads the stored revision', async () => {
    const created = service.createPolicyRevision('p-1', POLICY_BODY);
    const request = await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/policies`);
    expect(request.request.method).toBe('POST');
    // The SDK serialises the body, so the assertion compares the same text the wire carries.
    expect(request.request.body).toBe(JSON.stringify(POLICY_BODY));
    // Nothing is asserted about `Idempotency-Key` here: the service passes none (this route's
    // idempotency is the content hash), and the header on the wire is the platform interceptor's
    // fallback for untyped mutations (`core/http/idempotency.interceptor.ts`), which owns that
    // behaviour. The activation case below is the one that pins a caller-supplied key.
    request.flush(JSON.stringify(POLICY_REVISION), {
      status: 201,
      statusText: '',
      headers: JSON_HEADERS,
    });

    const answer: PolicyRevisionDto = await created;
    expect(answer.id).toBe(POLICY_REVISION_ID);
    expect(answer.revision_no).toBe(4);
  });

  it('activatePolicy() sends the caller intent key and answers the advisory pointer', async () => {
    const intent = createIntent();
    const activated = service.activatePolicy('p-1', POLICY_REVISION_ID, intent);
    const request = await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/policy-activation`);
    expect(request.request.method).toBe('POST');
    // The mode is the service's own decision: the wizard never offers `enforcing` (07.7.1).
    expect(request.request.body).toBe(
      JSON.stringify({ policy_revision_id: POLICY_REVISION_ID, mode: 'advisory' }),
    );
    expect(request.request.headers.get('Idempotency-Key')).toBe(intent.idempotencyKey);
    request.flush(JSON.stringify(ACTIVATION), { headers: JSON_HEADERS });

    const answer: PolicyActivationDto = await activated;
    expect(answer.mode).toBe('advisory');
    expect(answer.changed).toBe(true);
    expect(answer.policy_revision_id).toBe(POLICY_REVISION_ID);
  });

  it('activatePolicy() surfaces a 409 envelope as a typed conflict, never a value', async () => {
    const activated = service.activatePolicy('p-1', POLICY_REVISION_ID, createIntent());
    const envelope = {
      error: {
        code: 'invalid_transition',
        message: 'A retired policy revision can never be activated again.',
        request_id: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
        retryable: false,
      },
    };
    (await takeRequest(httpMock, `${API_BASE_URL}/projects/p-1/policy-activation`)).flush(
      JSON.stringify(envelope),
      { status: 409, statusText: '', headers: JSON_HEADERS },
    );

    const cause = await rejectionOf(activated);
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('conflict');
      expect(cause.error.code).toBe('invalid_transition');
    }
  });
});
