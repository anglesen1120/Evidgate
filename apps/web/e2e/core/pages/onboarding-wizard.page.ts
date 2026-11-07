import { expect, type Locator, type Page } from '@playwright/test';

import {
  backButton,
  currentStepItem,
  expectCurrentStep,
  expectStepDone,
  expectStepperShape,
  nextButton,
  onboardingUrl,
  shell,
  STEP_NUMBERS,
  type OnboardingSeed,
  type StepNumber,
} from '../../shell/onboarding-support';
import type { ShellLocale } from '../../shell/support';

/**
 * The five-step onboarding wizard as the core-flow specs drive it: open one seeded server state,
 * assert the position the wizard derived from it, then move between *viewed* steps. The stepper
 * DOM contract (one `aria-current` item, `data-done`, native `disabled`) stays in the shell
 * suite's helpers, so this page object composes them instead of restating their selectors.
 */
export class OnboardingWizard {
  constructor(private readonly page: Page) {}

  get root(): Locator {
    return shell(this.page);
  }

  get currentStep(): Locator {
    return currentStepItem(this.page);
  }

  get next(): Locator {
    return nextButton(this.page);
  }

  get back(): Locator {
    return backButton(this.page);
  }

  /** Opens one seed's address in one locale bundle and waits for the wizard shell. */
  async open(locale: ShellLocale, seed: OnboardingSeed): Promise<void> {
    await this.page.goto(onboardingUrl(locale, seed));
    await expect(this.root).toBeVisible();
  }

  /** The seed resolved to `step`: exactly one current item, five items with valid done flags. */
  async expectStep(step: StepNumber): Promise<void> {
    await expectCurrentStep(this.page, step);
    await expectStepperShape(this.page);
  }

  /** The derivation is a prefix: earlier steps are done, `step` and every later one are not. */
  async expectCompletedPrefix(step: StepNumber): Promise<void> {
    for (const number of STEP_NUMBERS) {
      await expectStepDone(this.page, number, number < step);
    }
  }

  /** Next advances only a viewed step whose done-condition holds; step 5 has no step after it. */
  async expectNextEnabled(enabled: boolean): Promise<void> {
    if (enabled) {
      await expect(this.next).toBeEnabled();
      return;
    }
    await expect(this.next).toBeDisabled();
  }

  /** Back moves off any step but the first, whatever that step's done-condition says. */
  async expectBackEnabled(enabled: boolean): Promise<void> {
    if (enabled) {
      await expect(this.back).toBeEnabled();
      return;
    }
    await expect(this.back).toBeDisabled();
  }

  /** Clicks Next and asserts the wizard re-rendered at `step`; Next is a no-op when gated. */
  async advanceTo(step: StepNumber): Promise<void> {
    await this.next.click();
    await this.expectStep(step);
  }

  /** Clicks Back and asserts the wizard re-rendered at `step`. */
  async retreatTo(step: StepNumber): Promise<void> {
    await this.back.click();
    await this.expectStep(step);
  }
}
