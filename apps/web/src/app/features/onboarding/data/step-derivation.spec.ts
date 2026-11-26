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

  it('opens first run for a project without credentials', () => {
    expect(deriveCurrentStep(done(1))).toBe(3);
  });

  it('resolves step 4 when a terminal sample run exists without an evidence revision', () => {
    // Seed 5: the run reached complete/incomplete, no evidence revision frozen from it yet.
    expect(deriveCurrentStep(done(1, 2, 3))).toBe(4);
  });

  it('resolves step 5 once the sample run has an evidence revision', () => {
    // Seed 6: the comparison panel now has something to read.
    expect(deriveCurrentStep(done(1, 2, 3, 4))).toBe(5);
  });

  it('reports enabled when all required steps are done without credentials', () => {
    expect(deriveCurrentStep(done(1, 3, 4, 5))).toBe('enabled');
  });

  it('returns the first not-done step even when a later one is done', () => {
    expect(deriveCurrentStep(done(1, 4))).toBe(3);
  });

  it('opens step 5 for the terminal screen, and the step itself otherwise', () => {
    expect(viewStepFor('enabled')).toBe(5);
    expect(viewStepFor(1)).toBe(1);
    expect(viewStepFor(3)).toBe(3);
    expect(viewStepFor(5)).toBe(5);
  });
});
