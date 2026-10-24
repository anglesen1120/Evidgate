// 09.1.1-T4.1/T4.2 (plan 09.1.1 §10 T4, §5; D-S68-Q4): where the app sends a caller who has no
// destination in the URL — `/` and `/orgs/:orgId` are landing addresses, not pages.
//
// The landing answer is computed from the server's lists, never guessed: the first organization the
// server returned, its first project (server order, keyset on `(created_at, id)`) and then the
// project overview. An org with no project yet goes to onboarding — the one screen that can create
// one — and a caller with no organization at all goes to `/welcome` instead of an empty shell.

import { inject } from '@angular/core';
import { Router, type CanActivateFn, type UrlTree } from '@angular/router';

import { SessionStore } from '../session/session.store';

/** Where a caller with no organization is told what to do next. */
const WELCOME_URL = '/welcome';
/** Where the sign-in placeholder lives until 09.1.1-T5 ships the real login. */
const LOGIN_URL = '/login';
/** Where the unavailable pattern lives. */
const UNAVAILABLE_URL = '/unavailable';

/**
 * The landing address of one org: its first project's overview, or onboarding when it has none.
 * `false` means this activation must not happen — either the pair is not one the server lists (only
 * reachable through a route table without the parent `orgScopeGuard`, where a non-match is the
 * conservative answer) or a newer resolve took the scope while this one awaited.
 */
async function orgLanding(
  session: SessionStore,
  router: Router,
  orgId: string,
): Promise<boolean | UrlTree> {
  switch (await session.resolveScope(orgId, null)) {
    case 'unavailable':
      return router.createUrlTree([UNAVAILABLE_URL]);
    case 'not-found':
      return false;
    case 'superseded':
      // Another resolve took the scope while this landing awaited: a newer navigation, whose own
      // answer supersedes this redirect, or the retry button's `reload()`. Re-resolving is not an
      // option — it would write this abandoned transition's org over the newer one's scope. A
      // `canActivate` `false` cancels the activation and renders nothing (no wildcard, no 404).
      return false;
    case 'ok':
      break;
  }
  const project = session.projects()[0];
  if (project === undefined) {
    return router.createUrlTree(['/orgs', orgId, 'onboarding']);
  }
  return router.createUrlTree(['/orgs', orgId, 'projects', project.id, 'overview']);
}

/**
 * `''` — the workspace root: anonymous → `/login`, unreadable session → `/unavailable`, no org →
 * `/welcome`, otherwise the first org's landing address.
 *
 * The login redirect carries no `returnUrl`: the attempt was the root itself, and a login flow that
 * must return to `/` adds nothing a bare `/login` does not.
 */
export const landingRedirectGuard: CanActivateFn = async () => {
  const session = inject(SessionStore);
  const router = inject(Router);

  await session.bootstrap();
  const state = session.sessionState();
  if (state === 'unavailable') {
    return router.createUrlTree([UNAVAILABLE_URL]);
  }
  if (state !== 'authenticated') {
    return router.createUrlTree([LOGIN_URL]);
  }
  const org = session.organizations()[0];
  if (org === undefined) {
    return router.createUrlTree([WELCOME_URL]);
  }
  return orgLanding(session, router, org.id);
};

/**
 * `orgs/:orgId` + `''` — the landing address of the org the URL names, resolved from the segment
 * rather than from whatever scope happened to be current.
 *
 * Only an authenticated session reaches this guard: the parent route's `authGuard` and
 * `orgScopeGuard` (`canMatch`) run first and already redirect an anonymous caller to
 * `/login?returnUrl=<attempted path>` and an unreadable session to `/unavailable`, so there is no
 * anonymous branch here — one would be dead code with a `returnUrl` no guard can produce.
 */
export const orgLandingGuard: CanActivateFn = async (route) => {
  const session = inject(SessionStore);
  const router = inject(Router);

  await session.bootstrap();
  if (session.sessionState() === 'unavailable') {
    return router.createUrlTree([UNAVAILABLE_URL]);
  }
  const orgId = route.paramMap.get('orgId');
  if (orgId === null) {
    return false;
  }
  return orgLanding(session, router, orgId);
};
