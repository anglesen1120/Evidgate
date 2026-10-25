// 09.2.1-T1.3 (plan 09.2.1 §6 derivation, §2 AC1; decision (c)): the five seeds of
// `fixtures/web-api/` each resolve the documented step, and the Next/Back controls are gated by the
// *viewed* step's done-condition rather than by "the wizard has advanced once".
//
// The derivation is a prefix: steps before the current one are done, the current one is not. A step
// marked done ahead of the server state it stands for would let Next walk past work nobody did, so
// each seed asserts both halves.
import { expect, test } from '@playwright/test';

import {
  backButton,
  expectCurrentStep,
  expectStepDone,
  expectStepperShape,
  nextButton,
  onboardingUrl,
  SEED_STEP,
  shell,
  type OnboardingSeed,
  type StepNumber,
} from './onboarding-support';

/** Every seed of the corpus, in step order. */
const SEEDS: readonly OnboardingSeed[] = [
  'onboarding-empty',
  'onboarding-project',
  'onboarding-connected',
  'onboarding-run-complete',
  'onboarding-evidence',
];

test.describe('each corpus seed resolves the documented step (AC1)', () => {
  for (const seed of SEEDS) {
    const step = SEED_STEP[seed];

    test(`${seed} shows step ${step} as current`, async ({ page }) => {
      await page.goto(onboardingUrl('en', seed));
      await expect(shell(page)).toBeVisible();
      await expectCurrentStep(page, step);
      await expectStepperShape(page);

      for (let done = 1; done < step; done += 1) {
        await expectStepDone(page, done as StepNumber, true);
      }
      for (let open = step; open <= 5; open += 1) {
        await expectStepDone(page, open, false);
      }
    });
  }
});

test.describe('Next and Back are gated by the viewed step (AC1, plan §10 T1.3)', () => {
  test('Next is natively disabled on a not-done step and Back steps back', async ({ page }) => {
    await page.goto(onboardingUrl('en', 'onboarding-connected'));
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 3);

    const next = nextButton(page);
    await expect(next).toBeDisabled();
    // Native `disabled`, not an `aria-disabled` costume: a keyboard user must be unable to submit
    // the step's advance at all.
    expect(await next.evaluate((control) => control.tagName)).toBe('BUTTON');

    const back = backButton(page);
    await expect(back).toBeEnabled();
    await back.click();
    await expectCurrentStep(page, 2);

    // Step 2 is done and the wizard is not on its last step, so the same control is enabled here:
    // the gate follows the viewed step, it is not a one-way "already advanced" latch.
    await expect(nextButton(page)).toBeEnabled();
  });

  test('Back is disabled on step 1, the first step there is nothing to return to', async ({
    page,
  }) => {
    await page.goto(onboardingUrl('en', 'onboarding-empty'));
    await expect(shell(page)).toBeVisible();
    await expectCurrentStep(page, 1);
    await expect(backButton(page)).toBeDisabled();
    await expect(nextButton(page)).toBeDisabled();
  });
});
