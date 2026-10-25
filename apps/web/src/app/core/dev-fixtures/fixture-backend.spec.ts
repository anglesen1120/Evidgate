// 09.1.1-T12.1 (plan 09.1.1 §2 AC10, §10 T12; ADR-0008 shape A): the dev fixture backend against
// the real stack.
//
// Every case drives an SDK operation through `ApiGateway` and the app's own `HttpClient`, with
// `provideApi()` plus `provideFixtureBackend()` and nothing else — no `provideHttpClientTesting()`,
// no stubbed `fetch`. That is what proves the two things AC10 claims: a fixture answers by
// `operationId` from `fixtures/web-api/`, and a fixture answer still travels the production path
// (`X-Request-Id`/`traceparent` interceptors -> fixture `HttpBackend` -> the blob adapter ->
// `ApiGateway`'s error mapper), so a 403 or a 412 fixture is the typed `ApiRequestError` a UI state
// review expects rather than a raw status.
//
// The absolute base URL is the same-origin form of `/v1` because Node's `Request` (the one `ng
// test` runs on) refuses the relative URL the browser resolves. Bodies are never hard-coded here:
// each assertion reads the catalog document the fixture directory holds, so changing a fixture's
// content cannot make this spec silently assert something else.
//
// `FIXTURE_BACKEND` is spied on to observe the request the interceptors produced and the events the
// backend emitted: the response header `X-Evidgate-Fixture` and the request's `X-Request-Id` are
// not visible through the SDK's resolved value, and reaching for them any other way would mean
// duplicating the chain this spec exists to prove.

import { HttpClient, HttpResponse, type HttpEvent, type HttpRequest } from '@angular/common/http';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  createCiBinding,
  createIngestToken,
  getPolicyRevision,
  getRunDecision,
  isErrorEnvelope,
  listPermissions,
  listRoles,
  listRuns,
  rotateIngestToken,
  updateProject,
  type ErrorEnvelope,
} from '@evidgate/sdk';
import { firstValueFrom, tap } from 'rxjs';

import { isApiRequestError } from '../http/api-error';
import { provideApi } from '../http/api-client';
import { ApiGateway } from '../http/api-gateway';
import { API_BASE_PATH } from '../http/api-origin';
import {
  FIXTURE_BACKEND,
  FIXTURE_NOT_FOUND_CODE,
  FIXTURE_RESPONSE_HEADER,
  provideFixtureBackend,
} from './fixture-backend';
import { FIXTURE_CATALOG, type FixtureDocument } from './fixture-catalog';
import { FixtureToggles } from './fixture-toggles';

const API_BASE_URL = `${location.origin}${API_BASE_PATH}`;

/** RFC 4122 v4, the only form `newUuid()` mints for `X-Request-Id`. */
const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

/** W3C trace context, the form `requestIdInterceptor` sets. */
const TRACEPARENT = /^00-[0-9a-f]{32}-[0-9a-f]{16}-01$/;

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

/** One catalog document by its corpus directory name; a missing one fails the spec loudly. */
function documentOf(name: string): FixtureDocument {
  const entry = FIXTURE_CATALOG.find((candidate) => candidate.name === name);
  if (entry === undefined) {
    throw new Error(`fixtures/web-api has no fixture named "${name}"`);
  }
  return entry.document;
}

/** The 00 §9.2 envelope a refusal fixture carries, validated before its fields are asserted on. */
function envelopeOf(document: FixtureDocument): ErrorEnvelope {
  if (!isErrorEnvelope(document.body)) {
    throw new Error(
      `fixture ${document.operation_id}/${document.variant} carries no 00 §9.2 envelope`,
    );
  }
  return document.body;
}

/** The rejection of `promise`, or a failure when it resolved — the shape every refusal case reads. */
async function rejectionOf<T>(promise: Promise<T>): Promise<unknown> {
  try {
    await promise;
  } catch (error: unknown) {
    return error;
  }
  throw new Error('expected the call to reject');
}

/**
 * The revision id a `getPolicyRevision` document answers for: its own `body.id`, the member the
 * id-keyed selection of 09.7.1-F15 note 3 compares against the request's `{id}` path segment.
 */
function revisionIdOf(document: FixtureDocument): string {
  const body = document.body as { id?: unknown } | undefined;
  if (typeof body?.id !== 'string') {
    throw new Error('the document carries no revision `body.id`, so it cannot be read by id');
  }
  return body.id;
}

/** An id no corpus document of `getPolicyRevision` carries: the fallback case's subject. */
const UNKNOWN_REVISION_ID = '018f0000-0000-7000-8000-00000000a0ff';

describe('fixture backend (09.1.1-T12.1)', () => {
  let gateway: ApiGateway;
  let toggles: FixtureToggles;
  let handled: HttpRequest<unknown>[];
  let events: HttpEvent<unknown>[];

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideApi(API_BASE_URL),
        provideFixtureBackend(),
      ],
    });
    gateway = TestBed.inject(ApiGateway);
    toggles = TestBed.inject(FixtureToggles);
    handled = [];
    events = [];

    const backend = TestBed.inject(FIXTURE_BACKEND);
    const handle = backend.handle.bind(backend);
    vi.spyOn(backend, 'handle').mockImplementation((request: HttpRequest<unknown>) => {
      handled.push(request);
      return handle(request).pipe(tap((event) => events.push(event)));
    });
  });

  it('answers listPermissions from its fixture and names the fixture in a header', async () => {
    const document = documentOf('permissions-list');
    const body = await gateway.call((client) => listPermissions({ client }));

    expect(body).toEqual(document.body);
    const response = events.find((event) => event instanceof HttpResponse);
    expect(response).toBeInstanceOf(HttpResponse);
    if (response instanceof HttpResponse) {
      expect(response.status).toBe(document.status);
      expect(response.headers.get(FIXTURE_RESPONSE_HEADER)).toBe('permissions-list');
      expect(response.headers.get('Content-Type')).toBe('application/json');
    }
  });

  it('resolves a path template: listRuns reaches its fixture through /projects/{p}/runs', async () => {
    const document = documentOf('runs-list');
    const page = await gateway.call((client) => listRuns({ client, path: { p: 'p-1' } }));

    expect(page).toEqual(document.body);
    expect(handled.at(0)?.url).toBe(`${API_BASE_URL}/projects/p-1/runs`);
    const response = events.find((event) => event instanceof HttpResponse);
    if (response instanceof HttpResponse) {
      expect(response.headers.get(FIXTURE_RESPONSE_HEADER)).toBe('runs-list');
    }
  });

  // 09.7.1-F15 note (3): `getPolicyRevision` is the one ID-KEYED route. Each corpus revision
  // document carries its own `body.id`, so two different revision ids must answer two different
  // documents — the property the browser's revision diff and the 412 dialog's server column need.
  it('answers the revision the path id names, with two ids giving two bodies (09.7.1-F15)', async () => {
    const previous = documentOf('policy-revision-previous');
    const head = documentOf('policy-revision-head');
    const previousId = revisionIdOf(previous);
    const headId = revisionIdOf(head);
    expect(previousId).not.toBe(headId);

    const before = await gateway.call((client) =>
      getPolicyRevision({ client, path: { id: previousId } }),
    );
    const after = await gateway.call((client) =>
      getPolicyRevision({ client, path: { id: headId } }),
    );

    expect(before).toEqual(previous.body);
    expect(after).toEqual(head.body);
    expect(before).not.toEqual(after);
    expect(handled.map((request) => `${request.method} ${request.url}`)).toEqual([
      `GET ${API_BASE_URL}/policies/${previousId}`,
      `GET ${API_BASE_URL}/policies/${headId}`,
    ]);
    const answered = events.filter((event) => event instanceof HttpResponse);
    expect(answered.map((event) => event.headers.get(FIXTURE_RESPONSE_HEADER))).toEqual([
      'policy-revision-previous',
      'policy-revision-head',
    ]);
  });

  // The order, in one case: a matching id wins over a variant that selects another document, and
  // that same variant still answers when no document carries the id.
  it('lets a matching id win over the variant and falls back to it otherwise (09.7.1-F15)', async () => {
    toggles.setVariant('getPolicyRevision', 'first');
    const head = documentOf('policy-revision-head');
    const first = documentOf('policy-revision-first');
    expect(revisionIdOf(head)).not.toBe(revisionIdOf(first));

    const byId = await gateway.call((client) =>
      getPolicyRevision({ client, path: { id: revisionIdOf(head) } }),
    );
    const fallback = await gateway.call((client) =>
      getPolicyRevision({ client, path: { id: UNKNOWN_REVISION_ID } }),
    );

    // The id won: the variant still names the FIRST revision, and the answer is still the head.
    expect(byId).toEqual(head.body);
    // No document holds the unknown id, so the selected variant answers, exactly as before.
    expect(fallback).toEqual(first.body);
    const answered = events.filter((event) => event instanceof HttpResponse);
    expect(answered.map((event) => event.headers.get(FIXTURE_RESPONSE_HEADER))).toEqual([
      'policy-revision-head',
      'policy-revision-first',
    ]);
  });

  // A variant still opens its own document for an id no revision carries: the review switch is
  // unchanged for every id the corpus does not know, including the default `ok`.
  it('keeps the variant selection for an id no document carries (09.7.1-F15)', async () => {
    const head = documentOf('policy-revision-head');
    const previous = documentOf('policy-revision-previous');

    const defaulted = await gateway.call((client) =>
      getPolicyRevision({ client, path: { id: UNKNOWN_REVISION_ID } }),
    );
    toggles.setVariant('getPolicyRevision', 'previous');
    const selected = await gateway.call((client) =>
      getPolicyRevision({ client, path: { id: UNKNOWN_REVISION_ID } }),
    );

    expect(defaulted).toEqual(head.body);
    expect(selected).toEqual(previous.body);
    const answered = events.filter((event) => event instanceof HttpResponse);
    expect(answered.map((event) => event.headers.get(FIXTURE_RESPONSE_HEADER))).toEqual([
      'policy-revision-head',
      'policy-revision-previous',
    ]);
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

  it('answers the selected variant: listRuns "forbidden" is a typed forbidden error', async () => {
    toggles.setVariant('listRuns', 'forbidden');
    const document = documentOf('runs-list-forbidden');
    const envelope = envelopeOf(document);

    const cause = await rejectionOf(
      gateway.call((client) => listRuns({ client, path: { p: 'p-1' } })),
    );

    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('forbidden');
      expect(cause.error.status).toBe(403);
      expect(cause.error.code).toBe(envelope.error.code);
      expect(cause.error.message).toBe(envelope.error.message);
      expect(cause.error.requestId).toBe(envelope.error.request_id);
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

  it('answers 501 fixture_not_found for an operation no fixture covers', async () => {
    // 09.2.1-T1 routes `getRun` (the wizard reads it), so the required op for "uncovered" moved to
    // its neighbour `GET /runs/{r}/decision`: one segment longer than the routed template, and
    // deliberately unrouted — the corpus answers runs, not their decisions (09.2.1-T3 owns that).
    const cause = await rejectionOf(
      gateway.call((client) => getRunDecision({ client, path: { r: 'r-1' } })),
    );

    expect(handled.at(0)?.url).toBe(`${API_BASE_URL}/runs/r-1/decision`);
    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('unexpected');
      expect(cause.error.status).toBe(501);
      expect(cause.error.code).toBe(FIXTURE_NOT_FOUND_CODE);
      expect(cause.error.message).toContain('fixtures/web-api');
      expect(cause.error.retryable).toBe(false);
    }
  });

  it('answers 501 for a variant no fixture carries instead of substituting a body', async () => {
    toggles.setVariant('listRoles', 'no-such-variant');

    const cause = await rejectionOf(gateway.call((client) => listRoles({ client })));

    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('unexpected');
      expect(cause.error.status).toBe(501);
      expect(cause.error.code).toBe(FIXTURE_NOT_FOUND_CODE);
    }
  });

  it('sits behind the HttpClient interceptors: the backend sees X-Request-Id and traceparent', async () => {
    await gateway.call((client) => listRoles({ client }));

    const request = handled.at(0);
    expect(request?.headers.get('X-Request-Id')).toMatch(UUID_V4);
    expect(request?.headers.get('traceparent')).toMatch(TRACEPARENT);
  });

  it('copies a json body, so a caller that mutates it cannot alter the catalog', async () => {
    const http = TestBed.inject(HttpClient);
    const document = documentOf('permissions-list');
    const original = structuredClone(document.body);
    const url = `${API_BASE_URL}/permissions`;

    const first = await firstValueFrom(
      http.get<Record<string, unknown>>(url, { responseType: 'json' }),
    );
    first['items'] = [];
    first['mutated_by_the_spec'] = true;

    const second = await firstValueFrom(
      http.get<Record<string, unknown>>(url, { responseType: 'json' }),
    );

    expect(second).not.toBe(first);
    expect(second).toEqual(original);
    expect(document.body).toEqual(original);
  });

  it('applies a latency toggle before answering', async () => {
    vi.useFakeTimers();
    try {
      const document = documentOf('permissions-list');
      toggles.setLatency('listPermissions', 50);
      let answered = false;
      const pending = gateway
        .call((client) => listPermissions({ client }))
        .then((body) => {
          answered = true;
          return body;
        });

      // The SDK -> adapter -> HttpClient path is microtasks only, so yielding to the microtask
      // queue is enough to reach the backend's `timer`; the loop stops exactly when it is armed.
      for (let turn = 0; turn < 100 && vi.getTimerCount() === 0; turn += 1) {
        await Promise.resolve();
      }
      expect(vi.getTimerCount()).toBeGreaterThan(0);
      expect(answered).toBe(false);

      await vi.advanceTimersByTimeAsync(49);
      expect(answered).toBe(false);

      await vi.advanceTimersByTimeAsync(1);
      await expect(pending).resolves.toEqual(document.body);
    } finally {
      vi.useRealTimers();
    }
  });
});
