// 09.2.1-T1.2 (plan 09.2.1 §9 "five independent seed combinations each resolve the documented
// step", §10 T1.1/T1.2; AC1): the derivation itself, as pure data. The seeds below are the five
// resource combinations of plan §6 in done-condition form — nothing created, project only,
// project + binding, project + token, project + terminal run without an evidence revision, and the
// same run with one — plus the two properties the shell relies on: the first not-done step wins,
// and a later step done out of order never hides an earlier open one.

import {
  deriveCurrentStep,
  NO_STEPS_DONE,
  STEP_NUMBERS,
  viewStepFor,
  type StepNumber,
  type StepsDone,
} from './step-derivation';

/** A done-record seeded with exactly the steps listed. */
function done(...steps: readonly StepNumber[]): StepsDone {
  return {
    1: steps.includes(1),
    2: steps.includes(2),
    3: steps.includes(3),
    4: steps.includes(4),
    5: steps.includes(5),
  };
}

describe('step derivation (09.2.1-T1.2)', () => {
  it('lists the five steps in render order', () => {
    expect(STEP_NUMBERS).toEqual([1, 2, 3, 4, 5]);
  });

  it('starts on step 1 when nothing is done', () => {
    // Seed 1: an org with no project — the create-project step.
    expect(deriveCurrentStep(NO_STEPS_DONE)).toBe(1);
  });

  it('resolves step 2 when only the project exists', () => {
    // Seed 2: project created, nothing connected yet.
    expect(deriveCurrentStep(done(1))).toBe(2);
  });

  it('resolves step 3 when the project has a CI binding', () => {
    // Seed 3: either connect path advances identically (AC2).
    expect(deriveCurrentStep(done(1, 2))).toBe(3);
  });

  it('resolves step 3 when the project has an ingest token instead', () => {
    // Seed 4: the token path of step 2 — the same done-condition, so the same step.
    expect(deriveCurrentStep(done(1, 2))).toBe(3);
  });

  it('resolves step 4 when a terminal sample run exists without an evidence revision', () => {
    // Seed 5: the run reached complete/incomplete, no evidence revision frozen from it yet.
    expect(deriveCurrentStep(done(1, 2, 3))).toBe(4);
  });

  it('resolves step 5 once the sample run has an evidence revision', () => {
    // Seed 6: the comparison panel now has something to read.
    expect(deriveCurrentStep(done(1, 2, 3, 4))).toBe(5);
  });

  it('reports enabled only when all five are done', () => {
    expect(deriveCurrentStep(done(1, 2, 3, 4, 5))).toBe('enabled');
  });

  it('returns the first not-done step even when a later one is done', () => {
    // A project with an evidence revision but no CI connection: the ordering must not skip step 2.
    expect(deriveCurrentStep(done(1, 4))).toBe(2);
  });

  it('opens step 5 for the terminal screen, and the step itself otherwise', () => {
    expect(viewStepFor('enabled')).toBe(5);
    expect(viewStepFor(1)).toBe(1);
    expect(viewStepFor(3)).toBe(3);
    expect(viewStepFor(5)).toBe(5);
  });
});
