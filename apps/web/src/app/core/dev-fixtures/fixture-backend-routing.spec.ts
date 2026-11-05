// The id-keyed and cursor-keyed fixture routes, and the policy reads whose headers or fallbacks a
// UI state reads: each case drives the real interceptor, backend and SDK path, as the sibling
// fixture-backend specs do.

import { HttpResponse, type HttpEvent, type HttpRequest } from '@angular/common/http';
import {
  getPolicyActivation,
  getPolicyHead,
  getPolicyRevision,
  listCiBindings,
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
import type { FixtureDocument } from './fixture-catalog';
import { queryParameter } from './fixture-routes';
import type { FixtureToggles } from './fixture-toggles';

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
    ({ gateway, toggles, handled, events } = setupFixtureBackend());
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

  it('serves the head revision read with the quoted ETag the publish compares against', async () => {
    const document = documentOf('policy-head-read-ok');
    const head = await gateway.call((client) => getPolicyHead({ client, path: { p: 'p-1' } }));

    expect(head).toEqual(document.body);
    expect(handled.at(0)?.url).toBe(`${API_BASE_URL}/projects/p-1/policies/head`);
    const answered = events.find((event) => event instanceof HttpResponse);
    expect(answered instanceof HttpResponse).toBe(true);
    if (answered instanceof HttpResponse) {
      expect(answered.headers.get(FIXTURE_RESPONSE_HEADER)).toBe('policy-head-read-ok');
      // The tab's `If-Match` source must be the corpus header, not a value the spec computes:
      // the adapter and `ApiGateway` are part of what this case proves.
      expect(answered.headers.get('ETag')).toBe(document.headers['ETag']);
    }
  });

  it('maps the head read’s 404 to a typed not_found that names no project', async () => {
    toggles.setVariant('getPolicyHead', 'not-found');
    const document = documentOf('policy-head-read-not-found');
    const envelope = envelopeOf(document);

    const cause = await rejectionOf(
      gateway.call((client) => getPolicyHead({ client, path: { p: 'p-unreachable' } })),
    );

    expect(isApiRequestError(cause)).toBe(true);
    if (isApiRequestError(cause)) {
      expect(cause.error.kind).toBe('not_found');
      expect(cause.error.status).toBe(404);
      expect(cause.error.code).toBe(envelope.error.code);
      expect(cause.error.message).toBe(envelope.error.message);
      expect(cause.error.message).not.toContain('p-unreachable');
      expect(cause.error.requestId).toBe(envelope.error.request_id);
    }
  });
});
