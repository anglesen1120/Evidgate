// `X-Evidgate-Org` is the one tenant header the app writes: `/v1/permissions` and every
// `/v1/orgs/{o}/…` read is tenant-scoped, so a request without it answers the pre-tenant route and
// the shell renders unavailable after sign-in. It is derived here, never hand-built at a call site.
import { DOCUMENT } from '@angular/common';
import type { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';

import { TenantStore } from '../session/tenant.store';
import { isApiRequestUrl } from './api-origin';

/** The tenant header name: one spelling, shared by the interceptor and its specs. */
export const ORG_HEADER = 'X-Evidgate-Org';

/**
 * Routes answered before a tenant exists. Both are exact paths: `/v1/orgs` is the caller's own list
 * and `/v1/ci/token` CI's pre-tenant exchange, while `/v1/orgs/{o}/…` IS tenant-scoped.
 */
const PRE_TENANT_PATHS: Readonly<Record<string, true>> = {
  '/v1/orgs': true,
  '/v1/ci/token': true,
};

/** Route families whose first segment already identifies a non-tenant caller. */
const PRE_TENANT_PREFIXES: readonly string[] = ['/v1/auth', '/v1/webhooks'];

/** The `{o}` of a `/v1/orgs/{o}` path, which is also the tenant of every `/v1/orgs/{o}/…` read. */
const ORG_SCOPE_PATH = /^\/v1\/orgs\/([^/]+)/;

/**
 * The `{o}` of a `/v1/orgs/{o}` path as a header value, or `null` when the segment is not an id.
 * A path is caller-supplied text and URLs carry encodings a header cannot: `%0d%0a` would split the
 * request, `%E2%82%AC` is not ASCII. Only the id charset an org id is spelled in may leave here;
 * anything else lets the caller fall back to the store instead of writing a malformed header.
 */
const ORG_ID_CHARSET = /^[0-9a-fA-F-]{1,64}$/;

function isPreTenant(pathname: string): boolean {
  if (PRE_TENANT_PATHS[pathname] === true) {
    return true;
  }
  return PRE_TENANT_PREFIXES.some(
    (prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`),
  );
}

/** A path segment as an id: percent-decoded when it is valid, otherwise the segment itself. */
function decodeSegment(segment: string): string {
  try {
    return decodeURIComponent(segment);
  } catch {
    return segment;
  }
}

function pathOrgId(pathname: string): string | null {
  const match = ORG_SCOPE_PATH.exec(pathname);
  if (match === null) {
    return null;
  }
  const segment = match[1];
  const decoded = segment === undefined ? null : decodeSegment(segment);
  return decoded !== null && ORG_ID_CHARSET.test(decoded) ? decoded : null;
}

/**
 * Adds the tenant header to a tenant-scoped API request. The org named by the path wins over the
 * store, so a route that already states its tenant cannot be scoped by a stale one; a request that
 * carries the header keeps its own value, and an unknown tenant adds nothing.
 */
export const tenantOrgInterceptor: HttpInterceptorFn = (request, next) => {
  const pageHref = inject(DOCUMENT).location.href;
  // Never a foreign origin or a path outside the API: a tenant id is not shared with another host.
  if (!isApiRequestUrl(request.url, pageHref)) {
    return next(request);
  }
  const { pathname } = new URL(request.url, pageHref);
  if (isPreTenant(pathname) || request.headers.has(ORG_HEADER)) {
    return next(request);
  }
  const orgId = pathOrgId(pathname) ?? inject(TenantStore).orgId();
  if (orgId === null) {
    return next(request);
  }
  return next(request.clone({ headers: request.headers.set(ORG_HEADER, orgId) }));
};
