// 09.2.1-T1.2 (plan 09.2.1 §6 "Wizard step derivation", §10 T1.1/T1.2; T63): the wizard's five
// steps as data, not as a state machine the client advances.
//
// Progress is *derived* from resources the server already holds (§3 T63 note): the project, a CI
// binding or ingest token, the project's oldest run reaching a terminal collection state, the
// evidence revision frozen from that run, and a `policy_activation` row. Nothing here persists a
// step: a reload re-derives it from the same reads, which is AC1 ("resumes at the first incomplete
// step, reading resumable state from the server, not local storage").
//
// The five steps are done independently and the current step is the first not-done one, so a step
// whose done-condition became true out of order (an evidence revision on a run that is not
// terminal, say) can never hide an earlier step that is still open.

/** One wizard step, 1-based, in the order the shell renders them. */
export type StepNumber = 1 | 2 | 3 | 4 | 5;

/**
 * Where the wizard is: the first step that is not done, or `'enabled'` once all five are — the
 * terminal "project enabled and collecting" screen (plan §6).
 */
export type WizardStep = StepNumber | 'enabled';

/** Every step in render order, so a caller never spells the sequence out a second time. */
export const STEP_NUMBERS: readonly StepNumber[] = [1, 2, 3, 4, 5];

/** The done-condition of each step, as one record the derivation reads. */
export type StepsDone = Readonly<Record<StepNumber, boolean>>;

/** The frozen "nothing done" record: a fresh reader of an org with no project yet. */
export const NO_STEPS_DONE: StepsDone = { 1: false, 2: false, 3: false, 4: false, 5: false };

/**
 * The first not-done step, or `'enabled'` when every step is done (plan §6: "Current step = first
 * not-done step; all five done → wizard shows the terminal 'project enabled' screen").
 */
export function deriveCurrentStep(done: StepsDone): WizardStep {
  for (const step of STEP_NUMBERS) {
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
