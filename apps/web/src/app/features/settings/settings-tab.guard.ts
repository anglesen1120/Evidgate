// 09.7.1-T1.2 (plan 09.7.1 §2 AC1, §5 route table, §8 S2/S3; 00 §9.2; D-S68-Q1/Q4): the per-tab
// route gate of Settings.
//
// AC1's shape: a tab the caller may not use is absent from the strip AND its route does not match,
// so typing `/orgs/:orgId/settings/policy` renders the not-found pattern. That is why this guard
// returns a plain `false` and never a `UrlTree`: a `false` leaves the match table, the router falls
// through to the wildcard route and the 404 pattern answers — exactly as it does for a path that
// exists nowhere. A redirect to `/forbidden` (what `permissionGuard` does for an entry the sidebar
// still shows) would instead confirm that the destination exists, which is the existence leak
// 00 §9.2 forbids. A tab is either offered or as absent as an unknown segment; there is no
// "exists but you may not" answer at this level.
//
// The decision itself is `isSettingsTabAvailable`, the same predicate the tab strip filters with,
// so the strip and the match table can never disagree. It reads the server-declared permission set
// only (`SessionStore.has`, fed by the `EffectivePermissionsSource` seam): a role label confers no
// privilege on the client (D-S68-Q1), and the API pipeline stays the authority (plan §8 S2).

import { inject } from '@angular/core';
import type { CanMatchFn } from '@angular/router';

import { SessionStore } from '../../core/session/session.store';
import { isSettingsTabAvailable, type SettingsTab } from './settings-tabs';

/**
 * Matches one Settings tab's child route only when the tab exists for this caller.
 *
 * The bootstrap is awaited like every other shell guard (`phase.guard.ts`), so a tab never matches
 * while the session is still unknown: an unauthenticated or unreadable session settles with the
 * empty permission set, and the tab is therefore unmatched rather than rendered empty.
 */
export function settingsTabGuard(tab: SettingsTab): CanMatchFn {
  return async () => {
    const session = inject(SessionStore);
    await session.bootstrap();
    return isSettingsTabAvailable(tab, (code) => session.has(code));
  };
}
