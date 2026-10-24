// 09.1.1-T4.1 (plan 09.1.1 §10 T4.1; CR-D-2 navigation rule): the phase predicate the sidebar and
// the route table share. A later phase is hidden AND unmatched; an earlier or equal phase is
// served, so a build that reaches G3 keeps every G1/G2 screen it already had.

import { ACTIVE_PHASE, isPhaseActive } from './product-phase';

describe('product phase (09.1.1-T4.1)', () => {
  it('serves G1 in this build', () => {
    expect(ACTIVE_PHASE).toBe('G1');
  });

  it('counts the active phase as reached', () => {
    expect(isPhaseActive('G1')).toBe(true);
  });

  it('hides every later phase in a G1 build', () => {
    expect(isPhaseActive('G2')).toBe(false);
    expect(isPhaseActive('G3')).toBe(false);
    expect(isPhaseActive('G4')).toBe(false);
  });

  // The ordering itself (`PHASE_ORDER`) becomes observable only once `ACTIVE_PHASE` moves past G1;
  // with a fixed G1 build, "G1 is served, later phases are not" is the whole contract. The case
  // that pins "a later build keeps the earlier phases" belongs here when that build exists.
});
