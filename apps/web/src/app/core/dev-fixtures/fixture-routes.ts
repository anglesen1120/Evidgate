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
// rule 1): the origin is irrelevant to a fixture, and query parameters never select a fixture — a
// `cursor`, a `limit` or a filter changes nothing in the body the corpus holds. Templates such as
// `/projects/{p}/runs` match one non-empty path segment per `{name}`, so `/projects/p-1/runs`
// resolves and `/projects/p-1/runs/r-1` does not.
//
// Dev-only, like the rest of `core/dev-fixtures`: see the module comment of `fixture-toggles.ts`.

import { API_BASE_PATH } from '../http/api-origin';

/** The HTTP methods an operation of `contracts/openapi.yaml` is served with here. */
export type FixtureHttpMethod = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';

/** One servable operation: the corpus `operationId` and the wire method + path template. */
export interface FixtureRoute {
  readonly operationId: string;
  readonly method: FixtureHttpMethod;
  /** Path template relative to the API base path, `{name}` marking a path parameter. */
  readonly path: string;
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
  { operationId: 'listCiBindings', method: 'GET', path: '/projects/{p}/ci-bindings' },
  { operationId: 'listIngestTokens', method: 'GET', path: '/projects/{p}/ingest-tokens' },
  { operationId: 'listRuns', method: 'GET', path: '/projects/{p}/runs' },
  { operationId: 'getRun', method: 'GET', path: '/runs/{r}' },
  { operationId: 'updateProject', method: 'PATCH', path: '/projects/{p}' },
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
