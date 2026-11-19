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
// `FIXTURE_BACKEND` is spied on (in `setupFixtureBackend()`) to observe the request the interceptors
// produced and the events the backend emitted: the response header `X-Evidgate-Fixture` and the
// request's `X-Request-Id` are not visible through the SDK's resolved value, and reaching for them
// any other way would mean duplicating the chain this spec exists to prove.

import { HttpClient, HttpRequest, HttpResponse, type HttpEvent } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import {
  getRun,
  listPermissions,
  listRoles,
  listRuns,
  postRunEvents,
  queueAuthoringChange,
  type AuthoringChangeV1,
} from '@evidgate/sdk';
import { firstValueFrom } from 'rxjs';

import { isApiRequestError } from '../http/api-error';
import type { ApiGateway } from '../http/api-gateway';
import {
  FIXTURE_BACKEND,
  FIXTURE_NOT_FOUND_CODE,
  FIXTURE_RESPONSE_HEADER,
} from './fixture-backend';
import {
  API_BASE_URL,
  documentOf,
  envelopeOf,
  rejectionOf,
  setupFixtureBackend,
} from './fixture-backend.test-support';
import type { FixtureToggles } from './fixture-toggles';

/** RFC 4122 v4, the only form `newUuid()` mints for `X-Request-Id`. */
const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

/** W3C trace context, the form `requestIdInterceptor` sets. */
const TRACEPARENT = /^00-[0-9a-f]{32}-[0-9a-f]{16}-01$/;

describe('fixture backend (09.1.1-T12.1)', () => {
  let gateway: ApiGateway;
  let toggles: FixtureToggles;
  let handled: HttpRequest<unknown>[];
  let events: HttpEvent<unknown>[];

  beforeEach(() => {
    window.__evidgateFixtureRequests = [];
    ({ gateway, toggles, handled, events } = setupFixtureBackend());
  });

  afterEach(() => {
    delete window.__evidgateFixtureRequests;
  });

  it('records authoring queue JSON for browser assertions through the real SDK path', async () => {
    const project = '018f0000-0000-7000-8000-000000000011';
    const body: AuthoringChangeV1 = {
      target_kind: 'checklist_item',
      target_id: 'a4430000-0000-4000-8000-000000000011',
      change_type: 'comment',
      payload: { body: 'Review the routed checklist item' },
      reason: 'Browser wiring assertion',
    };
    await gateway.call((client) => queueAuthoringChange({ client, path: { p: project }, body }));
    const recorded = window.__evidgateFixtureRequests?.at(-1);
    expect(recorded).toMatchObject({
      method: 'POST',
      url: `${API_BASE_URL}/projects/${project}/authoring/changes`,
      body: JSON.stringify(body),
    });
  });

  it.each([
    ['GET', '/projects/p/authoring/changes'],
    ['POST', '/projects/p/authoring/changes:apply'],
    ['POST', '/session/sign-in'],
    ['PATCH', '/authoring/manual-cases/case'],
  ])('does not retain a body outside the queue route (%s %s)', (method, path) => {
    const backend = TestBed.inject(FIXTURE_BACKEND);
    backend.handle(new HttpRequest(method, `${API_BASE_URL}${path}`, { excluded: true }, {}));
    const recorded = window.__evidgateFixtureRequests?.at(-1);
    expect(recorded).toMatchObject({ method, url: `${API_BASE_URL}${path}` });
    expect(recorded).not.toHaveProperty('body');
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

  it('answers 501 fixture_not_found for an operation no fixture covers', async () => {
    const cause = await rejectionOf(
      gateway.call((client) => postRunEvents({ client, path: { r: 'r-1' }, body: { events: [] } })),
    );

    expect(handled.at(0)?.url).toBe(`${API_BASE_URL}/runs/r-1/events`);
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
