// 09.1.1-T4.1 (plan 09.1.1 §10 T4.1, §2 AC4; 00 §9.2; D-S68-Q1/Q4): the route gate for one
// server-declared permission code.
//
// AC4 is the contract: a route guarded for `run:read` is not matched when the server-declared set
// lacks that code, and the user lands on the 403 pattern. The set itself is a convenience — the
// API answers every call with its own authorisation — so this guard never widens access, it only
// keeps a route a caller cannot use out of the router's match table.
//
// The refusal carries no attempt in it: `/forbidden` has no path or query echo, because the
// attempted URL may name a resource of another tenant (00 §9.2, D-S68-Q3).

import { inject } from '@angular/core';
import { Router, type CanMatchFn } from '@angular/router';

import { SessionStore } from '../session/session.store';

/** Where the 403 pattern lives (09.1.1-T8 owns the full pattern; T4 ships the shell page). */
const FORBIDDEN_URL = '/forbidden';
/** Where the sign-in placeholder lives until 09.1.1-T5 ships the real login. */
const LOGIN_URL = '/login';
/** Where the unavailable pattern lives. */
const UNAVAILABLE_URL = '/unavailable';

/**
 * Matches a route only when the session is authenticated and the permission code is granted.
 *
 * - not authenticated → `/login`, with no `returnUrl` (defence in depth: the parent route's
 *   `authGuard` already handles the anonymous case and owns the return URL, and this guard only
 *   ever sees the segments still to consume, so a path built here would be a partial one);
 * - authenticated without the code → `/forbidden`;
 * - the session could not be read → `/unavailable`;
 * - authenticated with the code → match.
 */
export function permissionGuard(permission: string): CanMatchFn {
  return async () => {
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
    return session.has(permission) ? true : router.createUrlTree([FORBIDDEN_URL]);
  };
}
