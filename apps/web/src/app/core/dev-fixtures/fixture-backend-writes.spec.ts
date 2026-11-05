// The fixture answers for the step-2 writes and their typed refusals: a create, a rotate and a
// project update, each through the real interceptor, backend and SDK error mapper.

import { HttpResponse, type HttpEvent, type HttpRequest } from '@angular/common/http';
import {
  createCiBinding,
  createIngestToken,
  rotateIngestToken,
  updateProject,
} from '@evidgate/sdk';

import { isApiRequestError } from '../http/api-error';
import type { ApiGateway } from '../http/api-gateway';
import { FIXTURE_RESPONSE_HEADER } from './fixture-backend';
import {
  API_BASE_URL,
  documentOf,
  envelopeOf,
  rejectionOf,
  setupFixtureBackend,
} from './fixture-backend.test-support';
import type { FixtureToggles } from './fixture-toggles';

/**
 * The `createCiBinding` request both step-2 cases submit, in the two shapes the server validates
 * (`src/Modules/Evidgate.Modules.Identity/Ci/RefGlob.cs:74-78`): `allowed_ref_patterns` are git refs
 * and `allowed_workflow_refs` are `job_workflow_ref` values, `owner/repo/.github/workflows/<file>` at
 * a ref — never the `ref:path` pair the corpus's older T1 rows carry.
 */
const CI_BINDING_REQUEST = {
  provider: 'github_actions' as const,
  repository_id: 5012345,
  repository_full_name: 'acme/storefront-web',
  allowed_ref_patterns: ['refs/heads/main'],
  allowed_workflow_refs: ['acme/storefront-web/.github/workflows/evidgate.yml@refs/heads/main'],
};

describe('fixture backend (09.1.1-T12.1)', () => {
  let gateway: ApiGateway;
  let toggles: FixtureToggles;
  let handled: HttpRequest<unknown>[];
  let events: HttpEvent<unknown>[];

  beforeEach(() => {
    ({ gateway, toggles, handled, events } = setupFixtureBackend());
  });

  it('answers the step-2 writes: create binding, create token, rotate token (09.2.1-T2)', async () => {
    const bindingCreate = documentOf('ci-binding-create');
    const tokenCreate = documentOf('ingest-token-create');
    const tokenRotate = documentOf('ingest-token-rotate');

    const created = await gateway.call((client) =>
      createCiBinding({ client, path: { p: 'p-1' }, body: CI_BINDING_REQUEST }),
    );
    const issued = await gateway.call((client) =>
      createIngestToken({ client, path: { p: 'p-1' }, body: { name: 'CI upload' } }),
    );
    const rotated = await gateway.call((client) =>
      rotateIngestToken({ client, path: { t: '018f0000-0000-7000-8000-00000000003b' } }),
    );

    expect(created).toEqual(bindingCreate.body);
    expect(issued).toEqual(tokenCreate.body);
    expect(rotated).toEqual(tokenRotate.body);
    expect(handled.map((request) => `${request.method} ${request.url}`)).toEqual([
      `POST ${API_BASE_URL}/projects/p-1/ci-bindings`,
      `POST ${API_BASE_URL}/projects/p-1/ingest-tokens`,
      `POST ${API_BASE_URL}/ingest-tokens/018f0000-0000-7000-8000-00000000003b/rotate`,
    ]);
    // The POST rows answer a fixed document: the request body is forwarded but never echoed, which
    // is what lets the e2e suite assert the page renders the corpus's own metadata. The fetch
    // adapter hands `HttpClient` the serialized text (`http-client-fetch.ts:125-126`), so the
    // intercepted body is that string, not the object the SDK was given.
    const forwarded = handled.at(0)?.body;
    expect(typeof forwarded).toBe('string');
    expect(JSON.parse(String(forwarded))).toEqual(CI_BINDING_REQUEST);

    const answered = events.filter((event) => event instanceof HttpResponse);
    expect(answered.map((event) => event.status)).toEqual([
      bindingCreate.status,
      tokenCreate.status,
      tokenRotate.status,
    ]);
    expect(answered.map((event) => event.headers.get(FIXTURE_RESPONSE_HEADER))).toEqual([
      'ci-binding-create',
      'ingest-token-create',
      'ingest-token-rotate',
    ]);
  });

  it('maps the step-2 write refusals to typed conflict errors (09.2.1-T2)', async () => {
    toggles.setVariant('createCiBinding', 'conflict');
    const bindingRefusal = envelopeOf(documentOf('ci-binding-create-conflict'));
    const bindingCause = await rejectionOf(
      gateway.call((client) =>
        createCiBinding({ client, path: { p: 'p-1' }, body: CI_BINDING_REQUEST }),
      ),
    );

    expect(isApiRequestError(bindingCause)).toBe(true);
    if (isApiRequestError(bindingCause)) {
      expect(bindingCause.error.kind).toBe('conflict');
      expect(bindingCause.error.status).toBe(409);
      expect(bindingCause.error.code).toBe(bindingRefusal.error.code);
      expect(bindingCause.error.message).toBe(bindingRefusal.error.message);
    }

    toggles.setVariant('rotateIngestToken', 'conflict');
    const rotateRefusal = envelopeOf(documentOf('ingest-token-rotate-conflict'));
    const rotateCause = await rejectionOf(
      gateway.call((client) =>
        rotateIngestToken({ client, path: { t: '018f0000-0000-7000-8000-00000000003b' } }),
      ),
    );

    expect(isApiRequestError(rotateCause)).toBe(true);
    if (isApiRequestError(rotateCause)) {
      expect(rotateCause.error.kind).toBe('conflict');
      expect(rotateCause.error.status).toBe(409);
      expect(rotateCause.error.code).toBe(rotateRefusal.error.code);
      expect(rotateCause.error.message).toBe(rotateRefusal.error.message);
    }
  });

  it('maps a 412 fixture to kind precondition_failed with the envelope details', async () => {
    toggles.setVariant('updateProject', 'precondition_failed');
    const document = documentOf('project-update-precondition-failed');
    const envelope = envelopeOf(document);

    const cause = await rejectionOf(
      gateway.call((client) =>
        updateProject({
          client,
          path: { p: 'p-1' },
          headers: { 'If-Match': '"3"', 'Idempotency-Key': 'fixture-spec-precondition' },
          body: { name: 'Renamed by the fixture spec' },
        }),
      ),
    );

    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('precondition_failed');
      expect(cause.error.status).toBe(412);
      expect(cause.error.code).toBe(envelope.error.code);
      expect(cause.error.details).toEqual(envelope.error.details ?? []);
    }
  });
});
