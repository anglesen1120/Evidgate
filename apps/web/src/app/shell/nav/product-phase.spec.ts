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

  it('keeps earlier phases when a later one is active', () => {
    expect(isPhaseActive('G1', 'G3')).toBe(true);
    expect(isPhaseActive('G2', 'G3')).toBe(true);
    expect(isPhaseActive('G3', 'G3')).toBe(true);
    expect(isPhaseActive('G4', 'G3')).toBe(false);
  });

  it('serves every phase when the last one is active', () => {
    expect(isPhaseActive('G1', 'G4')).toBe(true);
    expect(isPhaseActive('G2', 'G4')).toBe(true);
    expect(isPhaseActive('G3', 'G4')).toBe(true);
    expect(isPhaseActive('G4', 'G4')).toBe(true);
  });
});
