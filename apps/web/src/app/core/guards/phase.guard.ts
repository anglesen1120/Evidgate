// 09.1.1-T4.1 (plan 09.1.1 §10 T4; CR-D-2 navigation rule, `docs/design/web-ui/prompts-crd2/
// 00-delta-brief.md`): a route of a phase the workspace has not reached is not matched at all.
//
// Two rules, not one: the sidebar hides the entry (`nav-view.ts` filters by the same predicate) and
// the route does not match, so an entry a phase hides cannot be reached by typing its URL either —
// it falls to the wildcard route and renders the 404 pattern, which is the honest answer for a URL
// the product does not serve yet.

import { inject } from '@angular/core';
import type { CanMatchFn } from '@angular/router';

import type { ProductPhase } from '../../shell/nav/nav.models';
import { isEntryActive, isPhaseActive } from '../../shell/nav/product-phase';
import { SessionStore } from '../session/session.store';

/**
 * Matches a route only while its phase is at most the active phase.
 *
 * It awaits the bootstrap like every other guard — not because a phase depends on the session, but
 * because the shell's guard set is one behaviour: a route never matches while the session is
 * still unknown, so a phase-hidden route and a protected route become visible in the same tick.
 */
export function phaseGuard(phase: ProductPhase): CanMatchFn {
  return async () => {
    const session = inject(SessionStore);
    await session.bootstrap();
    return isPhaseActive(phase);
  };
}

/** `phaseGuard` for a registry entry: also matches an entry the build opens early. */
export function entryPhaseGuard(entry: {
  readonly id: string;
  readonly phase: ProductPhase;
}): CanMatchFn {
  return async () => {
    const session = inject(SessionStore);
    await session.bootstrap();
    return isEntryActive(entry);
  };
}
