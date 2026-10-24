// 09.1.1-T4.1/T4.2 (plan 09.1.1 §10 T4.2, §2 AC4; 00 §9.2; D-S68-Q4): the org/project scope
// guards. The URL names an org and a project; the store answers only from what the server listed,
// and these guards turn that answer into a router decision.
//
// A `not-found` is a boolean `false`, not a redirect: `CanMatchFn` returning `false` simply does
// not match, so the wildcard route takes the URL and renders the SAME 404 page an unmatched route
// gets. That is the point — a foreign id must be indistinguishable from a URL that never existed,
// never a 403 that confirms the id belongs to someone else (00 §9.2, D-S68-Q3).
//
// `superseded` is also a `false`, but it is not a refusal: the store answered it because a newer
// navigation (or the retry button's `reload()`) took the scope while this guard awaited, and the
// router has already abandoned this transition. `NavigationTransitions` pipes every navigation
// through one `switchMap`, so a later navigation unsubscribes the older guard chain and the value
// this guard returns is discarded — it can never become the wildcard's 404. Cancelling is still the
// right answer for the one case the router did not cancel (a `reload()` overtaking a navigation):
// matching would mount a route whose scope now belongs to somebody else's request.
//
// Both guards resolve through the store only: `orgScopeGuard` reads the org and its project list
// once, and `projectScopeGuard` then answers from that loaded list (`resolveScope` sees the org is
// already current), so one navigation performs one project-list read however many guards it runs.

import { inject } from '@angular/core';
import { Router, type CanMatchFn } from '@angular/router';

import { SessionStore } from '../session/session.store';
import { signInTree } from './auth.guard';

/** Where the unavailable pattern lives. */
const UNAVAILABLE_URL = '/unavailable';

/**
 * Matches `orgs/:orgId` for an org the server listed for this caller.
 *
 * The id is `segments[1]`: `CanMatchFn` receives every URL segment this route still has to consume,
 * which for `orgs/:orgId/projects/:projectId/<segment>` is the whole tail
 * (`['orgs', '<orgId>', 'projects', '<projectId>', '<segment>']`), so only index 1 is the org.
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
    case 'superseded':
      // Not a refusal: a newer navigation owns the scope and the router has dropped this one (see
      // the file comment). `false` is fail-closed for the case the router kept it alive.
      return false;
  }
};

/**
 * Matches `projects/:projectId` for a project the server listed under the org the URL already
 * resolved. The org is `currentOrg` — the parent route's `orgScopeGuard` put it there — so this
 * guard checks the project belongs to *that* org, which is what makes a project id from another
 * organization a `not-found` rather than a cross-tenant read.
 *
 * The parent already read the org's project list, so this call is answered from the store's cache:
 * it resolves the project for the shell (and for `NavViewService`) without a second read. The id is
 * `segments[1]` of the segments this route still has to consume (`['projects', '<projectId>', …]`).
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
    case 'superseded':
      // As in `orgScopeGuard`: the router has already dropped this transition, and matching a
      // scope a newer navigation owns would render the wrong org's screen.
      return false;
  }
};
