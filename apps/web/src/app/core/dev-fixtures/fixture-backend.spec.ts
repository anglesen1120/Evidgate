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
  getPolicyActivation,
  getPolicyRevision,
  cancelRun,
  getRun,
  isErrorEnvelope,
  listCiBindings,
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
import { queryParameter } from './fixture-routes';
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

/**
 * The `Storefront web` project both paged CI-binding fixtures belong to: the project the Secrets
 * tab reads (`018f0000-0000-7000-8000-000000000011`, as `ingest-tokens-list-settings`).
 */
const BINDINGS_PROJECT = '018f0000-0000-7000-8000-000000000011';

/** A cursor no catalog row declares: the cursor fallback case's subject. */
const UNKNOWN_CURSOR = '08df2cdfd69f6800-018f0000-0000-7000-8000-0000000000ff';

/**
 * The `next_cursor` a list page names — the cursor the next `Load more` follows — or `null` when the
 * page ends the walk. Read from the document, never hard-coded, so a fixture edit cannot make the
 * cursor cases below assert something else.
 */
function nextCursorOf(document: FixtureDocument): string | null {
  const body = document.body as { next_cursor?: string | null } | undefined;
  const cursor = body?.next_cursor;
  if (cursor !== null && typeof cursor !== 'string') {
    throw new Error('the document carries no list page `next_cursor`');
  }
  return cursor;
}

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
  it('serves each run-detail success variant through GET /runs/{r}', async () => {
    for (const name of ['run-detail', 'run-detail-ref-format', 'run-detail-null-evidence']) {
      const document = documentOf(name);
      toggles.setVariant('getRun', document.variant);
      const response = await gateway.call((client) => getRun({ client, path: { r: 'detail-id' } }));
      expect(response).toEqual(document.body);
      expect(handled.at(-1)?.url).toBe(`${API_BASE_URL}/runs/detail-id`);
    }
  });
  it('keeps foreign and missing run 404s identical, and maps 403 separately', async () => {
    toggles.setVariant('getRun', 'detail-not-found');
    for (const id of ['foreign-id', 'missing-id']) {
      const failure = await rejectionOf(
        gateway.call((client) => getRun({ client, path: { r: id } })),
      );
      expect(isApiRequestError(failure)).toBe(true);
      if (isApiRequestError(failure)) {
        expect(failure.error.kind).toBe('not_found');
        expect(failure.error.status).toBe(404);
        expect(failure.error.message).not.toContain(id);
      }
    }
    toggles.setVariant('getRun', 'detail-forbidden');
    const denied = await rejectionOf(
      gateway.call((client) => getRun({ client, path: { r: 'denied' } })),
    );
    expect(isApiRequestError(denied) && denied.error.kind).toBe('forbidden');
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

  // 09.7.1-F15 note (4): `listCiBindings` is the one CURSOR-KEYED route. The first request carries
  // no cursor and gets the first page by its variant; the cursor that page names gets the second
  // page; the second page ends the walk. That is the exact sequence the Secrets tab's Load more
  // performs, which is why a browser can now exercise it against the corpus.
  it('answers the page the request cursor names, so Load more reaches the last page (09.7.1-F15)', async () => {
    toggles.setVariant('listCiBindings', 'settings-page-1');
    const first = documentOf('ci-bindings-list-settings-page-1');
    const second = documentOf('ci-bindings-list-settings-page-2');
    const cursor = nextCursorOf(first);
    expect(
      cursor,
      'the first page names no cursor, so Load more has nothing to follow',
    ).toBeTruthy();
    expect(nextCursorOf(second), 'the second page must end the walk').toBeNull();

    const pageOne = await gateway.call((client) =>
      listCiBindings({ client, path: { p: BINDINGS_PROJECT }, query: { limit: 200 } }),
    );
    const pageTwo = await gateway.call((client) =>
      listCiBindings({
        client,
        path: { p: BINDINGS_PROJECT },
        query: { limit: 200, ...(cursor === null ? {} : { cursor }) },
      }),
    );

    expect(pageOne).toEqual(first.body);
    expect(pageTwo).toEqual(second.body);
    expect(pageTwo).not.toEqual(pageOne);
    // The cursor travels in the URL the SDK built (the adapter forwards it to `HttpClient`
    // unchanged), which is what `queryParameter` reads — `HttpRequest.params` is empty here.
    expect(queryParameter(handled.at(0)?.url ?? '', 'cursor')).toBeNull();
    expect(queryParameter(handled.at(1)?.url ?? '', 'cursor')).toBe(cursor);
    const answered = events.filter((event) => event instanceof HttpResponse);
    expect(answered.map((event) => event.headers.get(FIXTURE_RESPONSE_HEADER))).toEqual([
      'ci-bindings-list-settings-page-1',
      'ci-bindings-list-settings-page-2',
    ]);
  });

  // A cursor no page declares is not a refusal: the variant answers, exactly as an unknown revision
  // id does, so a stale cursor shows the review page instead of a 501 no UI state expects.
  it('keeps the variant selection for a cursor no page declares (09.7.1-F15)', async () => {
    toggles.setVariant('listCiBindings', 'settings-page-1');
    const first = documentOf('ci-bindings-list-settings-page-1');

    const stale = await gateway.call((client) =>
      listCiBindings({
        client,
        path: { p: BINDINGS_PROJECT },
        query: { limit: 200, cursor: UNKNOWN_CURSOR },
      }),
    );

    expect(stale).toEqual(first.body);
    const answered = events.filter((event) => event instanceof HttpResponse);
    expect(answered.map((event) => event.headers.get(FIXTURE_RESPONSE_HEADER))).toEqual([
      'ci-bindings-list-settings-page-1',
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

  it('carries a 429 fixture’s Retry-After into the typed error (AC-F63-a)', async () => {
    // The document AC-F63-a's wait is built from, through the whole path this spec exists to prove:
    // fixture `HttpBackend` -> `ApiGateway`'s error mapper -> `ApiError.retryAfterSeconds`. The
    // interval the panel counts down is therefore the corpus header, not a number a spec wrote down.
    toggles.setVariant('getPolicyActivation', 'rate-limited');
    const document = documentOf('policy-activation-read-rate-limited');
    const envelope = envelopeOf(document);

    const cause = await rejectionOf(
      gateway.call((client) => getPolicyActivation({ client, path: { p: 'p-1' } })),
    );

    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('rate_limited');
      expect(cause.error.status).toBe(429);
      expect(cause.error.code).toBe(envelope.error.code);
      expect(cause.error.requestId).toBe(envelope.error.request_id);
      expect(cause.error.retryAfterSeconds).toBe(Number(document.headers['Retry-After']));
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
    const cause = await rejectionOf(
      gateway.call((client) =>
        cancelRun({
          client,
          path: { r: 'r-1' },
          headers: { 'Idempotency-Key': 'fixture-spec-uncovered' },
        }),
      ),
    );

    expect(handled.at(0)?.url).toBe(`${API_BASE_URL}/runs/r-1/cancel`);
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
