// 09.1.1-T4.1/T4.2 (plan 09.1.1 §10 T4.2, §2 AC4; 00 §9.2; D-S68-Q4): the org/project scope
// guards. The URL names an org and a project; the store answers only from what the server listed,
// and these guards turn that answer into a router decision.
//
// A `not-found` is a boolean `false`, not a redirect: `CanMatchFn` returning `false` simply does
// not match, so the wildcard route takes the URL and renders the SAME 404 page an unmatched route
// gets. That is the point — a foreign id must be indistinguishable from a URL that never existed,
// never a 403 that confirms the id belongs to someone else (00 §9.2, D-S68-Q3).

import { inject } from '@angular/core';
import { Router, type CanMatchFn } from '@angular/router';

import { SessionStore } from '../session/session.store';
import { signInTree } from './auth.guard';

/** Where the unavailable pattern lives. */
const UNAVAILABLE_URL = '/unavailable';

/**
 * Matches `orgs/:orgId` for an org the server listed for this caller.
 *
 * The id is `segments[1]`: `CanMatchFn` receives the segments this route still has to consume, so
 * `['orgs', '<orgId>']` arrives here and nothing else does.
 */
export const orgScopeGuard: CanMatchFn = async (_route, segments) => {
  const session = inject(SessionStore);
  const router = inject(Router);

  await session.bootstrap();
  const state = session.sessionState();
  if (state === 'unavailable') {
    return router.createUrlTree([UNAVAILABLE_URL]);
  }
  if (state !== 'authenticated') {
    return signInTree(router, segments);
  }

  const orgId = segments[1]?.path;
  if (orgId === undefined) {
    return false;
  }
  switch (await session.resolveScope(orgId, null)) {
    case 'ok':
      return true;
    case 'unavailable':
      return router.createUrlTree([UNAVAILABLE_URL]);
    case 'not-found':
      return false;
  }
};

/**
 * Matches `projects/:projectId` for a project the server listed under the org the URL already
 * resolved. The org is `currentOrg` — the parent route's `orgScopeGuard` put it there — so this
 * guard checks the project belongs to *that* org, which is what makes a project id from another
 * organization a `not-found` rather than a cross-tenant read.
 */
export const projectScopeGuard: CanMatchFn = async (_route, segments) => {
  const session = inject(SessionStore);
  const router = inject(Router);

  await session.bootstrap();
  const state = session.sessionState();
  if (state === 'unavailable') {
    return router.createUrlTree([UNAVAILABLE_URL]);
  }
  if (state !== 'authenticated') {
    return signInTree(router, segments);
  }

  const projectId = segments[1]?.path;
  const org = session.currentOrg();
  if (projectId === undefined || org === null) {
    // No resolved org means the parent scope never matched; `false` lets the wildcard render 404.
    return false;
  }
  switch (await session.resolveScope(org.id, projectId)) {
    case 'ok':
      return true;
    case 'unavailable':
      return router.createUrlTree([UNAVAILABLE_URL]);
    case 'not-found':
      return false;
  }
};
