// 09.2.1-T1 (plan 09.2.1 §2 AC1, §6 "a reload resumes at the derived step", §8 failure path): a
// progress read the server refuses must render the shell's T8 pattern, never a step.
//
// Two layers answer a refusal here, and both are asserted because a wizard that swallowed either one
// would claim a step nobody earned:
//
//   1. the wizard's own read, at a project address — `getProject` answers 403/404 and the shell
//      renders the forbidden / not-found pattern inside `onboarding-shell` (steps 3–4 are still the
//      placeholders T3/T4 own, and step 2 only renders for a project the wizard could read, so a
//      "step 2" here would be a fabrication);
//   2. the org's scope read, at the onboarding root — a 403 or 404 on `listProjects` is answered by
//      the shell's scope guard as the same not-found page (00 §9.2 never distinguishes "you may
//      not" from "it is not there"), so the wizard does not render at all.
import { expect, test } from '@playwright/test';

import {
  CREATED_PROJECT,
  expectNoStepDone,
  orgRefusalUrl,
  refusalUrl,
  shell,
} from './onboarding-support';

test.describe('a refused progress read renders the T8 pattern (AC1)', () => {
  test('a 403 on the wizard project read renders the forbidden pattern', async ({ page }) => {
    await page.goto(refusalUrl('en', 'onboarding-forbidden', CREATED_PROJECT));

    await expect(shell(page)).toBeVisible();
    await expect(page.locator('app-forbidden-pattern').first()).toBeVisible();
    // The refusal is a 403, not a missing resource: the wizard must not answer "not found".
    await expect(page.locator('app-not-found-pattern')).toHaveCount(0);
    await expectNoStepDone(page);
  });

  test('a 404 on the wizard project read renders the not-found pattern', async ({ page }) => {
    await page.goto(refusalUrl('en', 'onboarding-project-not-found', CREATED_PROJECT));

    await expect(shell(page)).toBeVisible();
    await expect(page.locator('app-not-found-pattern').first()).toBeVisible();
    await expect(page.locator('app-forbidden-pattern')).toHaveCount(0);
    await expectNoStepDone(page);
  });

  test('a refused org scope read never shows a step, whatever it answered', async ({ page }) => {
    for (const failure of ['onboarding-forbidden', 'onboarding-project-not-found'] as const) {
      await page.goto(orgRefusalUrl('en', failure));

      // 00 §9.2: a 403 and a 404 on the scope read are one answer on purpose.
      await expect(page.locator('app-not-found-pattern').first()).toBeVisible();
      await expect(
        page.locator('[data-testid="onboarding-shell"] [data-testid="onboarding-stepper"]'),
        `${failure} let the wizard render a stepper`,
      ).toHaveCount(0);
      await expectNoStepDone(page);
    }
  });
});
