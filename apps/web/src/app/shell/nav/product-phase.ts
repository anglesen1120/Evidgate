// 09.1.1-T4.1 (plan 09.1.1 §10 T4, §5; CR-D-2 navigation rule): the product phase and the one
// predicate the sidebar and the route table share.
//
// Until a release flag exists, the workspace's phase is a build constant. `ACTIVE_PHASE` is the
// single place it is written: the navigation registry's visible set (`nav-view.ts`) and
// `phaseGuard` both read it through `isPhaseActive`, so an entry can never be visible in a phase
// whose route does not match, or the reverse.

import type { ProductPhase } from './nav.models';
import { REVIEW_OPEN_ENTRIES } from './review-entries';

/** Phase order, earliest first (CR-D-2: G1 Author → G2 Run → G3 Learn and repair → G4 Expand). */
const PHASE_ORDER: readonly ProductPhase[] = ['G1', 'G2', 'G3', 'G4'];

/**
 * The phase this build serves. G1 (Author) is the first shipped phase; a later phase's entries are
 * hidden AND their routes do not match, never disabled or teased (`00-delta-brief.md`).
 */
export const ACTIVE_PHASE: ProductPhase = 'G1';

/**
 * `true` when `phase` is at most the active phase, i.e. when the phase has already been reached.
 * An entry of a later phase answers `false` and is therefore both hidden and unmatched.
 *
 * The active phase is the build's `ACTIVE_PHASE` and never a call argument (review L5): a caller
 * that could pass one would turn the registry's visibility and the route table's match set into
 * two independent decisions on the same navigation.
 */
export function isPhaseActive(phase: ProductPhase): boolean {
  return PHASE_ORDER.indexOf(phase) <= PHASE_ORDER.indexOf(ACTIVE_PHASE);
}

/**
 * `isPhaseActive` for a registry entry, plus the entries the build opens early
 * (`review-entries.ts`; empty in production). The sidebar and `entryPhaseGuard` both call this.
 */
export function isEntryActive(entry: {
  readonly id: string;
  readonly phase: ProductPhase;
}): boolean {
  return isPhaseActive(entry.phase) || REVIEW_OPEN_ENTRIES.some((open) => open.id === entry.id);
}
