// The five panels retain their navigation order. Only project, run, evidence and activation
// are required; credential management is available without gating any of them.

/** One wizard step, 1-based, in the order the shell renders them. */
export type StepNumber = 1 | 2 | 3 | 4 | 5;

/**
 * Where the wizard is: the first step that is not done, or `'enabled'` once all required steps are — the
 * terminal "project enabled and collecting" screen (plan §6).
 */
export type WizardStep = StepNumber | 'enabled';

/** Every step in render order, so a caller never spells the sequence out a second time. */
export const STEP_NUMBERS: readonly StepNumber[] = [1, 2, 3, 4, 5];
export const REQUIRED_STEPS: readonly StepNumber[] = [1, 3, 4, 5];

/** The done-condition of each step, as one record the derivation reads. */
export type StepsDone = Readonly<Record<StepNumber, boolean>>;

/** The frozen "nothing done" record: a fresh reader of an org with no project yet. */
export const NO_STEPS_DONE: StepsDone = { 1: false, 2: false, 3: false, 4: false, 5: false };

/** Resume at the first unmet requirement, irrespective of optional credentials. */
export function deriveCurrentStep(done: StepsDone): WizardStep {
  for (const step of REQUIRED_STEPS) {
    if (!done[step]) {
      return step;
    }
  }
  return 'enabled';
}

/**
 * The step the shell opens on for a derived position: the current step, or the last one for the
 * terminal screen — there is no sixth body to render, so `'enabled'` shows step 5's panel with the
 * terminal copy (T1.3 owns that copy).
 */
export function viewStepFor(current: WizardStep): StepNumber {
  return current === 'enabled' ? 5 : current;
}
