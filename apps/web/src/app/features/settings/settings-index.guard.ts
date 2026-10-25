// 09.7.1-T1.2 (plan 09.7.1 §2 AC1, §5 route table; docs/design/web/settings.md states `empty`):
// the landing decision inside the Settings shell.
//
// `/orgs/:orgId/settings` itself has no content: the shell is a tab strip plus an outlet, so the
// index child answers one of two ways and never renders a screen of its own —
//
//   1. the caller has at least one available tab → redirect to the FIRST one in strip order, so the
//      URL names the destination that is showing (a tab strip with nothing selected is a state the
//      design does not have);
//   2. no tab is available → `true`, and the index route renders the "no settings destinations"
//      state. A redirect would have nowhere to go, and a 404 would be a lie: the shell exists for
//      this caller, it just has nothing in it.
//
// Both decisions come from `availableSettingsTabs`, the same filter the tab strip renders with, so
// the redirect can never point at a tab the strip does not offer. The session is already
// bootstrapped by the entry's guards (`phaseGuard`/`permissionGuard` on the `settings` route), and
// `has()` on an unsettled session answers `false` for every code — the empty branch is the safe one
// either way.

import { inject } from '@angular/core';
import { createUrlTreeFromSnapshot, type CanActivateFn } from '@angular/router';

import { SessionStore } from '../../core/session/session.store';
import { availableSettingsTabs } from './settings-tabs';

/**
 * Redirects the shell's index route to the first available tab, or lets it render the no-tabs state.
 *
 * The tree is built from the index route's PARENT snapshot with `createUrlTreeFromSnapshot`, the
 * public API for exactly this: a guard receives route snapshots, while `UrlCreationOptions.relativeTo`
 * takes an `ActivatedRoute`. Relative to the shell route the tab segment resolves to
 * `/orgs/<id>/settings/<segment>`.
 */
export const settingsIndexGuard: CanActivateFn = (route) => {
  const session = inject(SessionStore);

  const first = availableSettingsTabs((code) => session.has(code))[0];
  if (first === undefined) {
    return true;
  }
  return createUrlTreeFromSnapshot(route.parent ?? route, [first.segment]);
};
