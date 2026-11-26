// The fixture answers for the step-2 writes and their typed refusals: a create, a rotate and a
// project update, each through the real interceptor, backend and SDK error mapper.

import { HttpResponse, type HttpEvent, type HttpRequest } from '@angular/common/http';
import {
  createIngestToken,
  rotateIngestToken,
  setRetentionPolicy,
  type RetentionPolicy,
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

describe('fixture backend (09.1.1-T12.1)', () => {
  let gateway: ApiGateway;
  let toggles: FixtureToggles;
  let handled: HttpRequest<unknown>[];
  let events: HttpEvent<unknown>[];

  beforeEach(() => {
    ({ gateway, toggles, handled, events } = setupFixtureBackend());
  });

  it('answers the step-2 token issue and rotation', async () => {
    const tokenCreate = documentOf('ingest-token-create');
    const tokenRotate = documentOf('ingest-token-rotate');

    const issued = await gateway.call((client) =>
      createIngestToken({ client, path: { p: 'p-1' }, body: { name: 'CI upload' } }),
    );
    const rotated = await gateway.call((client) =>
      rotateIngestToken({ client, path: { t: '018f0000-0000-7000-8000-00000000003b' } }),
    );

    expect(issued).toEqual(tokenCreate.body);
    expect(rotated).toEqual(tokenRotate.body);
    expect(handled.map((request) => `${request.method} ${request.url}`)).toEqual([
      `POST ${API_BASE_URL}/projects/p-1/ingest-tokens`,
      `POST ${API_BASE_URL}/ingest-tokens/018f0000-0000-7000-8000-00000000003b/rotate`,
    ]);
    // The POST rows answer a fixed document: the request body is forwarded but never echoed, which
    // is what lets the e2e suite assert the page renders the corpus's own metadata. The fetch
    // adapter hands `HttpClient` the serialized text (`http-client-fetch.ts:125-126`), so the
    // intercepted body is that string, not the object the SDK was given.
    const forwarded = handled.at(0)?.body;
    expect(typeof forwarded).toBe('string');
    expect(JSON.parse(String(forwarded))).toEqual({ name: 'CI upload' });

    const answered = events.filter((event) => event instanceof HttpResponse);
    expect(answered.map((event) => event.status)).toEqual([tokenCreate.status, tokenRotate.status]);
    expect(answered.map((event) => event.headers.get(FIXTURE_RESPONSE_HEADER))).toEqual([
      'ingest-token-create',
      'ingest-token-rotate',
    ]);
  });

  it('maps a rotation refusal to a typed conflict error', async () => {
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

  it('serves the retention out_of_range 422 from the corpus with its envelope details', async () => {
    toggles.setVariant('setRetentionPolicy', 'out_of_range');
    const envelope = envelopeOf(documentOf('retention-put-out-of-range'));

    const cause = await rejectionOf(
      gateway.call((client) =>
        setRetentionPolicy({
          client,
          path: { p: 'p-1' },
          body: { artifact_days: 100, metadata_days: 120 },
        }),
      ),
    );

    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.status).toBe(422);
      expect(cause.error.code).toBe(envelope.error.code);
      expect(cause.error.details).toEqual(envelope.error.details ?? []);
    }
  });

  it('refuses a malformed ok retention write instead of answering the corpus 200', async () => {
    const missingWindows: Partial<RetentionPolicy> = {};
    const cause = await rejectionOf(
      gateway.call((client) =>
        setRetentionPolicy({
          client,
          path: { p: 'p-1' },
          body: missingWindows as RetentionPolicy,
        }),
      ),
    );

    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) expect(cause.error.status).toBe(501);
  });
});
