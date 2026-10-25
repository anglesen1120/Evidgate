// 09.2.1-T1 (plan 09.2.1 §8 "Performance/security" table, §2 AC6; 09.1.1 §2 AC6 for the shell's
// WCAG 2.2 AA claim): axe-core in the real browser, with `color-contrast` enabled, over the wizard
// shell in both themes and both bundles — plus AC6's own negative check, that no PASS/FAIL verdict
// appears anywhere in the wizard.
//
// The verdict case runs on a corpus whose sample run records `decision: "FAIL"`: the wizard may
// render the run's state (it is collection progress) and must not render a verdict, so a failing
// run is the fixture that would expose a client-side badge (FR-09-1/AC6). Steps 2–5 are the
// pending panels of T1, which is exactly when a placeholder verdict would be easiest to leak.
import { expect, test } from '@playwright/test';

import { expectCurrentStep, onboardingUrl, shell } from './onboarding-support';
import { currentTheme, expectNoA11yViolations, LOCALES, seedStoredTheme, THEMES } from './support';

test.describe('the wizard shell is WCAG 2.2 AA clean (09.1.1 AC6, plan §8)', () => {
  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      test(`step 2 shell, ${locale} bundle, ${theme} tokens`, async ({ page }) => {
        await seedStoredTheme(page, theme);
        await page.goto(onboardingUrl(locale, 'onboarding-project'));
        await expect(shell(page)).toBeVisible();
        await expectCurrentStep(page, 2);
        await expect(page.locator('[data-testid="onboarding-stepper"]')).toBeVisible();
        // The evidence-panel slot is part of the shell in every state, empty on the pending steps:
        // its absence would be a missing target, not an invisible one.
        await expect(page.locator('[data-testid="onboarding-evidence-panel"]')).toHaveCount(1);
        // The scanned content is keyed on the shell's own step heading, not on the placeholder body:
        // steps 2–5 render `onboarding-step-pending` only until 09.2.1-T2…T5 replace it with their
        // real pages, and a scan keyed on the placeholder would go red the day step 2 ships.
        await expect(page.locator('#onboarding-step-title')).toBeVisible();
        await expect(page.locator('html')).toHaveAttribute('lang', locale);
        // The theme the labels claims: a scan of the wrong token set would be a false pass.
        expect(await currentTheme(page)).toBe(theme);

        await expectNoA11yViolations(page, `onboarding step 2 ${locale}/${theme}`);
      });
    }
  }
});

test.describe('the wizard renders no client-computed verdict (AC6)', () => {
  for (const locale of LOCALES) {
    test(`no PASS/FAIL text on a failing sample run, ${locale} bundle`, async ({ page }) => {
      await page.goto(onboardingUrl(locale, 'onboarding-run-complete'));
      await expect(shell(page)).toBeVisible();
      await expectCurrentStep(page, 4);

      const rendered = await page.locator('body').innerText();
      expect(
        rendered,
        'the wizard rendered a PASS/FAIL verdict; the corpus run records FAIL and only ' +
          'GET /v1/runs/{r}/decision may turn that into a badge (FR-09-1)',
      ).not.toMatch(/\b(PASS|FAIL)\b/);
    });
  }
});
