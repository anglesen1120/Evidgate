// 09.1.1-T12.1 (plan 09.1.1 §2 AC10, §10 T12; contracts/openapi.yaml): the closed list of
// operations the dev fixture backend answers, and the request -> operation resolution that makes
// the answer independent of the SDK's argument order or query string.
//
// The list is deliberately hand-written and small: a fixture corpus entry is keyed by an
// `operationId` of `contracts/openapi.yaml`, so an operation is servable exactly when this table
// names it and `fixture-catalog.ts` carries a document for it. Everything else is answered with
// `501 fixture_not_found` (see `fixture-backend.ts`), which keeps "the app called an operation no
// fixture covers" visible instead of returning a plausible empty page.
//
// Resolution is method + path only, against the same-origin `/v1` path the app calls (ADR-0008
// rule 1): the origin is irrelevant to a fixture, and a query parameter never selects a fixture by
// itself — a `limit` or a filter changes nothing in the body the corpus holds, and a `cursor` only
// answers the page a catalog row explicitly declares for it (note (4) below). Templates such as
// `/projects/{p}/runs` match one non-empty path segment per `{name}`, so `/projects/p-1/runs`
// resolves and `/projects/p-1/runs/r-1` does not.
//
// A `POST` row answers its corpus document whatever request body arrives (09.2.1-T2): the corpus
// is a *response* corpus, so a create's answer never depends on what was sent and, being fixed,
// cannot change a later list read — the wizard's "created" and "resumed" states are separate
// documents (see `fixture-catalog.ts`). The four secret-bearing creates (`createCiBinding`,
// `createIngestToken`, `rotateIngestToken`, `createApiToken`) take no `Idempotency-Key` in
// `contracts/openapi.yaml`, so the backend ignores the header the app's interceptor still adds.
// The invite pair below takes one (`createMembership`, `resendMembershipInvite`) and answers a
// secret too; a fixture neither validates nor replays a key, so the header changes nothing here
// either.
//
// 09.7.1-F6 routes the role edit's three calls. They follow the same response-corpus rule, with one
// consequence worth stating: the `{m}` and `{r}` path parameters are matched against the route
// template only, never against the body of the document that answers — so
// `getMembership`/`grantMembershipRole` answer the one membership their fixture describes
// (`qa@acme.test`, the corpus page's second row) whatever id a request names, and the page shows
// that document's roles after a confirmed change. `grantMembershipRole` and `revokeMembershipRole`
// do take `Idempotency-Key` and `X-XSRF-Token` in the contract, and the reference client sends both
// (`core/http/idempotency.interceptor.ts`, the XSRF interceptor); a fixture neither validates nor
// replays a key, so the "a confirmed change is never resent" rule is pinned by the store's own spec
// rather than here. `revokeMembershipRole` is the second `DELETE` row: five segments, so it cannot
// collide with `removeMember`'s four.
//
// 09.7.1-T3 (F12) routes the pending invite's two lifecycle calls. `resendMembershipInvite`
// (`POST …/memberships/{m}/resend`) is a secret-bearing answer: its 202 `InviteIssuedView` carries
// the freshly rotated `invite_token`, and the contract has it take `Idempotency-Key` — the fixture
// backend neither validates nor replays that key, so the corpus document is the `ok` answer every
// time and the replay state (`?fixture.variant.resendMembershipInvite=replay`, the stored body with
// `invite_token: null`) is a separate document a review selects, not something the fixture infers.
// `cancelMembershipInvite` (`POST …/memberships/{m}/cancel-invite`) answers 204 with no body, like
// `removeMember`, and its five segments cannot be confused with `resend`'s five: the last segment is
// a literal of each template, so neither row matches the other's path.
//
// Dev-only, like the rest of `core/dev-fixtures`: see the module comment of `fixture-toggles.ts`.
//
// 09.7.1-T4 routes the Settings > Policy tab's three calls, all of them response-corpus rows: the
// append (`POST …/projects/{p}/policies`), the one-revision read (`GET /policies/{id}`) and the
// revision list (`GET /policies/{id}/revisions`). The two `/policies` templates have three and
// four segments, so neither matches the other, and neither can collide with the project-scoped
// rows (`…/policies` is the append's literal last segment). The corpus pages carry the `ETag` the
// contract declares (the head revision id, quoted, exactly as `PolicyEndpoints.cs` sets it), so
// the tab's `If-Match` source comes from a real header; the append answers the `201` with the
// same header on the revision it created, and the `200` (identical body already stored) with the
// head's.
//
// ID-keyed routes compare a path parameter to the resource-id field declared on the route.
// Policy revisions use `id`; releases use `release_id`. Unknown ids fall back to the URL variant.
//
// 09.7.1-F15 note (4) is the same selection one dimension over, on the QUERY string: `listCiBindings`
// declares `cursorQueryParameter: 'cursor'`, and a request that carries a cursor some catalog row
// declares for the page it holds is answered by THAT row. It closes residual (1) of the S86
// low-notes row in `STATUS.md` ("the ci-binding list has no Playwright load-more case because
// `ci-bindings-list-settings` ends with `next_cursor: null`; a cursor-page fixture seam is needed").
// The rule above — "query parameters never select a fixture" — held because no corpus row declared
// one; a cursor page is the one case where the query string *is* the identity of the answer, so the
// corpus spells it out per page instead of the backend guessing. The order stays the id-keyed rule's:
// a matching cursor wins over the variant, and a cursor no document declares (a stale one, or the
// first request, which carries none) falls back to the variant exactly as before. Only one row
// declared it until 09.3.1-T1; a second one needs its own pin in `fixture-contract.spec.ts`.
//
// 09.3.1-T1 adds the second cursor-keyed row: `listRuns` declares the same `cursor` parameter, and
// `run-list-page-2` declares the `next_cursor` `run-list-page-1` names, so the Runs list's Load more
// walks both corpus pages in a browser instead of the first page's cursor being answered with the
// first page again (which the store refuses as a repeated-cursor contract violation). The pin in
// `fixture-contract.spec.ts` names both rows explicitly, one per walk.

import { API_BASE_PATH } from '../http/api-origin';

/** The HTTP methods an operation of `contracts/openapi.yaml` is served with here. */
export type FixtureHttpMethod = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';

/** One servable operation: the corpus `operationId` and the wire method + path template. */
export interface FixtureRoute {
  readonly operationId: string;
  readonly method: FixtureHttpMethod;
  /** Path template relative to the API base path, `{name}` marking a path parameter. */
  readonly path: string;
  /**
   * The `{name}` path parameter whose request value selects the answer among this operation's
   * catalog documents by their own `body.id`, BEFORE the variant is consulted (09.7.1-F15 note
   * 3). Absent on every id-agnostic row, where the variant alone selects the document.
   */
  readonly idKeyParameter?: string;
  /** Body field carrying the route's resource id, when it is not `id`. */
  readonly idKeyField?: string;
  /**
   * The *query* parameter whose request value selects the answer among this operation's catalog
   * documents — the same selection as `idKeyParameter`, one dimension over (09.7.1-F15 note 4).
   * The value is compared against the `cursor` a catalog row declares for the page it holds, so a
   * cursor-paged list shows a second page only for the cursor the previous page actually named.
   * Absent on every route whose document does not depend on a query parameter — which is all of
   * them but `listCiBindings`, `listRuns` and `listCases`; the module comment holds the rule. The
   * catalog walk (`catalog-cases-list` → `catalog-cases-page-2`) is pinned in
   * `fixture-case-list.spec.ts`, beside the filter rules that page also answers.
   */
  readonly cursorQueryParameter?: string;
}

/**
 * Every operation the fixture corpus answers. The path and method are `contracts/openapi.yaml`'s,
 * relative to its servers URL `/v1`; a contract change that moves one of these must move this row
 * too (`apps/web/scripts/record-openapi-fixture.mjs --check` and the contract smoke catch the
 * SDK side of the same drift).
 */
export const FIXTURE_ROUTES: readonly FixtureRoute[] = [
  { operationId: 'listPermissions', method: 'GET', path: '/permissions' },
  { operationId: 'listRoles', method: 'GET', path: '/roles' },
  { operationId: 'listMyOrganizations', method: 'GET', path: '/orgs' },
  { operationId: 'createProject', method: 'POST', path: '/orgs/{o}/projects' },
  { operationId: 'listProjects', method: 'GET', path: '/orgs/{o}/projects' },
  { operationId: 'getProject', method: 'GET', path: '/projects/{p}' },
  {
    operationId: 'listSecurityEvents',
    method: 'GET',
    path: '/orgs/{o}/security-events',
    cursorQueryParameter: 'cursor',
  },
  {
    operationId: 'listCases',
    method: 'GET',
    path: '/projects/{p}/cases',
    cursorQueryParameter: 'cursor',
  },
  { operationId: 'updateCase', method: 'PATCH', path: '/cases/{c}' },
  { operationId: 'createCiBinding', method: 'POST', path: '/projects/{p}/ci-bindings' },
  {
    operationId: 'listCiBindings',
    method: 'GET',
    path: '/projects/{p}/ci-bindings',
    cursorQueryParameter: 'cursor',
  },
  { operationId: 'createIngestToken', method: 'POST', path: '/projects/{p}/ingest-tokens' },
  { operationId: 'listIngestTokens', method: 'GET', path: '/projects/{p}/ingest-tokens' },
  { operationId: 'rotateIngestToken', method: 'POST', path: '/ingest-tokens/{t}/rotate' },
  // The project API-token pair. One path, two verbs, so the method is what tells them apart; the
  // list's `ok` page ends the walk (`next_cursor: null`), so it is not cursor-keyed.
  { operationId: 'createApiToken', method: 'POST', path: '/projects/{p}/api-tokens' },
  { operationId: 'listApiTokens', method: 'GET', path: '/projects/{p}/api-tokens' },
  { operationId: 'listMemberships', method: 'GET', path: '/orgs/{o}/memberships' },
  { operationId: 'getMembership', method: 'GET', path: '/orgs/{o}/memberships/{m}' },
  { operationId: 'createMembership', method: 'POST', path: '/orgs/{o}/memberships' },
  { operationId: 'removeMember', method: 'DELETE', path: '/orgs/{o}/memberships/{m}' },
  {
    operationId: 'resendMembershipInvite',
    method: 'POST',
    path: '/orgs/{o}/memberships/{m}/resend',
  },
  {
    operationId: 'cancelMembershipInvite',
    method: 'POST',
    path: '/orgs/{o}/memberships/{m}/cancel-invite',
  },
  { operationId: 'grantMembershipRole', method: 'POST', path: '/orgs/{o}/memberships/{m}/roles' },
  {
    operationId: 'revokeMembershipRole',
    method: 'DELETE',
    path: '/orgs/{o}/memberships/{m}/roles/{r}',
  },
  { operationId: 'listAudit', method: 'GET', path: '/orgs/{o}/audit' },
  {
    operationId: 'listRuns',
    method: 'GET',
    path: '/projects/{p}/runs',
    cursorQueryParameter: 'cursor',
  },
  // 09.3.1-T2: detail states are selected explicitly by variant, not inferred from the run id.
  { operationId: 'getRun', method: 'GET', path: '/runs/{r}' },
  // The run-detail decision read: its state is selected by variant (`ok` is the pending body),
  // never inferred from the run id, the same way `getRun`'s is.
  { operationId: 'getRunDecision', method: 'GET', path: '/runs/{r}/decision' },
  // The evidence revision list. It is not cursor-keyed — the `ok` page ends the walk
  // (`next_cursor: null`) — so no request value can select a different document.
  {
    operationId: 'listEvidenceRevisions',
    method: 'GET',
    path: '/runs/{run_id}/evidence-revisions',
  },
  // 09.3.1-T4: the attempts keyset walk; `run-attempts-page-2` declares page 1's cursor.
  {
    operationId: 'listRunAttempts',
    method: 'GET',
    path: '/runs/{r}/attempts',
    cursorQueryParameter: 'cursor',
  },
  { operationId: 'updateProject', method: 'PATCH', path: '/projects/{p}' },
  // The evidence retention pair: one path, two verbs, so the method is what tells them apart and
  // neither row can match the other's request (the read takes no body, the upsert a closed one).
  { operationId: 'getRetentionPolicy', method: 'GET', path: '/projects/{p}/retention' },
  { operationId: 'setRetentionPolicy', method: 'PUT', path: '/projects/{p}/retention' },
  { operationId: 'createPolicyRevision', method: 'POST', path: '/projects/{p}/policies' },
  {
    operationId: 'activatePolicyRevision',
    method: 'POST',
    path: '/projects/{p}/policy-activation',
  },
  // 09.2.1-T5.2: step 5's read, the same path as the activation POST — the method is what tells
  // them apart, and neither row can match the other's request.
  { operationId: 'getPolicyActivation', method: 'GET', path: '/projects/{p}/policy-activation' },
  // The Policy tab's head read. Its template has four segments where the append's has three and
  // the revision read's two, so no row can match another's path; the path never names a revision,
  // so both answers are selected by variant. The `ok` document is the summary of the same head the
  // revision read issues, with the same quoted `ETag`.
  { operationId: 'getPolicyHead', method: 'GET', path: '/projects/{p}/policies/head' },
  { operationId: 'getPolicyRevision', method: 'GET', path: '/policies/{id}', idKeyParameter: 'id' },
  {
    operationId: 'getRelease',
    method: 'GET',
    path: '/releases/{id}',
    idKeyParameter: 'id',
    idKeyField: 'release_id',
  },
  { operationId: 'listPolicyRevisions', method: 'GET', path: '/policies/{id}/revisions' },
  // The pinned revision read of the composition screen. Its states are selected by variant, the
  // same way the run detail's are: the id in the path never picks the document.
  { operationId: 'getSuiteRevision', method: 'GET', path: '/suite-revisions/{r}' },
];

const API_PREFIX = `${API_BASE_PATH}/`;

/** One non-empty path segment, percent-decoded when the encoding is valid. */
function decodeSegment(segment: string): string {
  try {
    return decodeURIComponent(segment);
  } catch {
    // A malformed escape is not a path any route template can match; comparing the raw segment
    // keeps the answer deterministic (`404`-like below) instead of throwing out of the backend.
    return segment;
  }
}

/**
 * The request path relative to the API base path — `/projects/p-1/runs` for
 * `https://host/v1/projects/p-1/runs` — or `null` for a URL that is not under `/v1` at all.
 *
 * The relative path is what the route table is written against: it is the part of the URL the
 * contract owns, so the fixture backend never has to know the page origin a review runs on.
 */
export function apiRelativePath(url: string): string | null {
  let pathname: string;
  try {
    pathname = new URL(url).pathname;
  } catch {
    return null;
  }
  if (pathname === API_BASE_PATH || pathname === API_PREFIX) {
    return '/';
  }
  if (!pathname.startsWith(API_PREFIX)) {
    return null;
  }
  return pathname.slice(API_BASE_PATH.length);
}

/** True when every segment of `template` matches: a `{name}` takes one segment, the rest is literal. */
function pathMatches(template: string, requestSegments: readonly string[]): boolean {
  const templateSegments = template.split('/').filter((segment) => segment.length > 0);
  if (templateSegments.length !== requestSegments.length) {
    return false;
  }
  return templateSegments.every((segment, index) => {
    const requestSegment = requestSegments[index];
    if (requestSegment === undefined) {
      return false;
    }
    const isParameter = segment.startsWith('{') && segment.endsWith('}');
    return isParameter ? requestSegment.length > 0 : segment === requestSegment;
  });
}

/**
 * The request value of path parameter `name` — the decoded segment at the index `{name}` holds in
 * `template` — or `undefined` when the template has no such parameter or the path does not match
 * the template's segment count. Used only for a route that declares an `idKeyParameter`
 * (09.7.1-F15 note 3), which selects the answer by the document's own `body.id`.
 */
export function pathParameter(
  template: string,
  relativePath: string,
  name: string,
): string | undefined {
  const templateSegments = template.split('/').filter((segment) => segment.length > 0);
  const requestSegments = relativePath.split('/').filter((segment) => segment.length > 0);
  if (templateSegments.length !== requestSegments.length) {
    return undefined;
  }
  const index = templateSegments.indexOf(`{${name}}`);
  const segment = index === -1 ? undefined : requestSegments[index];
  return segment === undefined ? undefined : decodeSegment(segment);
}

/**
 * The route for one request, or `undefined` when no fixture covers it (the backend's
 * `501 fixture_not_found`). `method` is compared case-insensitively, the way `HttpRequest.method`
 * arrives after the SDK adapter upper-cases it.
 */
export function matchFixtureRoute(method: string, relativePath: string): FixtureRoute | undefined {
  const requestSegments = relativePath
    .split('/')
    .filter((segment) => segment.length > 0)
    .map(decodeSegment);
  const normalisedMethod = method.toUpperCase();
  return FIXTURE_ROUTES.find(
    (route) => route.method === normalisedMethod && pathMatches(route.path, requestSegments),
  );
}

/**
 * The request value of query parameter `name` in `url`, or `null` when the URL carries none — the
 * query-side counterpart of {@link pathParameter}, read for a route that declares a
 * `cursorQueryParameter` (09.7.1-F15 note 4).
 *
 * It reads the URL, not `HttpRequest.params`: the SDK's fetch adapter hands `HttpClient` a URL that
 * already holds the query string (`core/http/http-client-fetch.ts`), so `params` is empty even
 * though the request carries a cursor. Reading both would mean two sources for one fact; the URL is
 * the one the request is actually made with, and `urlWithParams` is exactly that URL.
 */
export function queryParameter(url: string, name: string): string | null {
  const queryStart = url.indexOf('?');
  if (queryStart < 0) {
    return null;
  }
  const hashStart = url.indexOf('#', queryStart);
  const search = hashStart < 0 ? url.slice(queryStart + 1) : url.slice(queryStart + 1, hashStart);
  return new URLSearchParams(search).get(name);
}
