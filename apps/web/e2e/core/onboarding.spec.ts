// The short onboarding path of the core flow: from an org with no project to the evidence step,
// one seeded server state per navigation (`onboarding-empty` → `onboarding-project` →
// `onboarding-connected` → `onboarding-evidence`). Each navigation asserts the position the wizard
// derived from that state, that the steps before it are done, and that Next/Back follow the
// *viewed* step rather than the furthest step the wizard has seen. Runs in both locale bundles.
import { expect, test } from '@playwright/test';

import { SEED_STEP, type OnboardingSeed } from '../shell/onboarding-support';
import { LOCALES } from '../shell/support';
import { OnboardingWizard } from './pages/onboarding-wizard.page';

/** The seeds of the short path, in wizard order. Step 4 has no seed of its own on this path. */
const CORE_PATH: readonly OnboardingSeed[] = [
  'onboarding-empty',
  'onboarding-project',
  'onboarding-connected',
  'onboarding-evidence',
];

for (const locale of LOCALES) {
  test.describe(`onboarding: empty org to the evidence step (${locale})`, () => {
    for (const seed of CORE_PATH) {
      test(`${seed} opens at its derived step with earlier steps done`, async ({ page }) => {
        const wizard = new OnboardingWizard(page);
        const step = SEED_STEP[seed];

        await wizard.open(locale, seed);
        await wizard.expectStep(step);
        await wizard.expectCompletedPrefix(step);

        // Nothing this seed's server state has not proved can be advanced past, and only step 1 has
        // no step to return to.
        await wizard.expectNextEnabled(false);
        await wizard.expectBackEnabled(step > 1);
      });
    }

    test('Back reopens a completed step and Next walks forward again', async ({ page }) => {
      const wizard = new OnboardingWizard(page);

      await wizard.open(locale, 'onboarding-evidence');
      await wizard.expectStep(5);
      await wizard.expectNextEnabled(false);
      expect(
        await wizard.next.evaluate((control) => control.tagName),
        'Next is a native button, so "gated" is not an aria-disabled costume',
      ).toBe('BUTTON');

      // The evidence seed has step 4 done and step 5 still open, so the same Next is enabled once
      // the viewed step is step 4: the gate follows the view, not a one-way latch.
      await wizard.retreatTo(4);
      await wizard.expectNextEnabled(true);
      await wizard.advanceTo(5);
      await wizard.expectNextEnabled(false);
    });
  });
}
