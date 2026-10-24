// 09.1.1-T4.1 (plan 09.1.1 §10 T4, §5; AC4/AC5; D-S68-Q4): the session guard every shell route
// composes first. It awaits the one bootstrap, then answers with a decision the router itself
// owns: a `UrlTree` redirect, never a boolean `false` that would leave the user on a blank outlet.
//
// The return URL is built from the matched segments, so it is always a same-origin absolute path
// this app produced — never a value echoed from the query string or a `Location` header. A login
// flow that later reads `returnUrl` must still accept only paths starting with a single `/`.

import { inject } from '@angular/core';
import { Router, type CanMatchFn, type UrlSegment, type UrlTree } from '@angular/router';

import { SessionStore } from '../session/session.store';

/** Where the unavailable pattern lives; the service-unavailable page retries the bootstrap. */
const UNAVAILABLE_URL = '/unavailable';
/** Where the sign-in placeholder lives until 09.1.1-T5 ships the real login. */
const LOGIN_URL = '/login';

/**
 * The path the router was trying to match, as a same-origin absolute path
 * (`['orgs', 'o-1', 'runs']` becomes `/orgs/o-1/runs`).
 *
 * Only the segments the match consumed are used: a query string or fragment is not part of a route
 * identity and must not travel through a login round trip, and each segment is re-encoded so a
 * segment can never introduce a second `/` or an authority of its own.
 */
export function attemptedPath(segments: readonly UrlSegment[]): string {
  return `/${segments.map((segment) => encodeURIComponent(segment.path)).join('/')}`;
}

/** The login redirect for an anonymous session, carrying the attempted path as `returnUrl`. */
export function signInTree(router: Router, segments: readonly UrlSegment[]): UrlTree {
  return router.createUrlTree([LOGIN_URL], { queryParams: { returnUrl: attemptedPath(segments) } });
}

/**
 * Matches a route only for an authenticated session (AC5).
 *
 * - `unknown` never escapes: the guard awaits `bootstrap()`, which settles it;
 * - `anonymous` → `/login?returnUrl=<attempted path>`;
 * - `unavailable` → `/unavailable`, whose page retries the bootstrap;
 * - `authenticated` → match.
 */
export const authGuard: CanMatchFn = async (_route, segments) => {
  const session = inject(SessionStore);
  const router = inject(Router);

  await session.bootstrap();

  switch (session.sessionState()) {
    case 'authenticated':
      return true;
    case 'unavailable':
      return router.createUrlTree([UNAVAILABLE_URL]);
    case 'anonymous':
      return signInTree(router, segments);
    case 'unknown':
      // `bootstrap()` settles the state, so this is unreachable; treating it as anonymous keeps
      // the guard fail-closed if a future store change ever leaves it unsettled.
      return signInTree(router, segments);
  }
};
