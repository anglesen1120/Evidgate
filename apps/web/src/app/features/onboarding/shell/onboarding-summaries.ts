import type { StepNumber } from '../data/step-derivation';

/** One navigator step: the number the store derives with, and its visible label. */
export interface OnboardingStep {
  readonly number: StepNumber;
  readonly label: string;
}

/** The five documented steps of the wizard, in order (plan 09.2.1 §10 T1, decision (c)). */
export const ONBOARDING_STEPS: readonly OnboardingStep[] = [
  { number: 1, label: $localize`:@@onboarding.step.project:Create project` },
  { number: 2, label: $localize`:@@onboarding.step.ci:Connect CI` },
  { number: 3, label: $localize`:@@onboarding.step.sample:Sample run` },
  { number: 4, label: $localize`:@@onboarding.step.compare:Compare reports` },
  { number: 5, label: $localize`:@@onboarding.step.enable:Enable collection` },
];

/** The icon each step's status renders; the text beside it carries the same meaning. */
export const ONBOARDING_STEP_ICONS = {
  complete: 'lucideCircleCheck',
  required: 'lucideClock',
  upcoming: 'lucideCircle',
} as const;

/** How many of the steps the server-confirmed derivation marks done. */
export function completedStepCount(
  done: Readonly<Record<StepNumber, boolean>>,
  steps: readonly OnboardingStep[],
): number {
  let completed = 0;
  for (const step of steps) if (done[step.number]) completed += 1;
  return completed;
}

/** The completion count in words, for the card header and the bar's value text. */
export function completionSummary(completed: number, total: number): string {
  return $localize`:@@onboarding.shell.progress-count:${completed}:COMPLETED: of ${total}:TOTAL: steps complete`;
}

/** The step the visitor is looking at, with its position. Its own sentence, so it is punctuated. */
export function viewedSummary(steps: readonly OnboardingStep[], viewed: StepNumber): string {
  const total = steps.length;
  const label = steps[viewed - 1]?.label ?? '';
  return $localize`:@@onboarding.shell.viewed-summary:Viewing ${viewed}:NUMBER: of ${total}:TOTAL:: ${label}:LABEL:`;
}

/**
 * The step the server-confirmed derivation says is next, or the all-complete sentence. A sentence
 * of its own: it closes with a period, so it never reads as one phrase with the project context
 * that follows it.
 */
export function nextSummary(
  steps: readonly OnboardingStep[],
  current: StepNumber | 'enabled',
): string {
  const total = steps.length;
  if (current === 'enabled') {
    return $localize`:@@onboarding.shell.all-complete:All ${total}:TOTAL: steps complete.`;
  }
  const label = steps[current - 1]?.label ?? '';
  return $localize`:@@onboarding.shell.next-summary:Next required ${current}:NUMBER: of ${total}:TOTAL:: ${label}:LABEL:.`;
}
