// 09.2.1-T1 (plan 09.2.1 §8 "Performance/security" table, §2 AC6; 09.1.1 §2 AC6 for the shell's
// WCAG 2.2 AA claim): axe-core in the real browser, with `color-contrast` enabled, over the wizard
// shell in both themes and both bundles — plus AC6's own negative check, that no PASS/FAIL verdict
// appears anywhere in the wizard.
//
// 09.2.1-T2 replaced step 2's placeholder with the connect page, so the scan now covers that page in
// its three states: empty, with a one-time value revealed (§8: the slot adds a read-only control,
// two buttons and a live region to whatever the shell already had), and with the server's metadata
// rows after a resume (AC7). Steps 3–5 are still the placeholders T3…T5 own.
//
// The verdict case runs on a corpus whose sample run records `decision: "FAIL"`: the wizard may
// render the run's state (it is collection progress) and must not render a verdict, so a failing
// run is the fixture that would expose a client-side badge (FR-09-1/AC6).
import { expect, test } from '@playwright/test';

import {
  connectCiPage,
  expectCurrentStep,
  onboardingUrl,
  revealValue,
  shell,
} from './onboarding-support';
import {
  activeElement,
  currentTheme,
  expectNoA11yViolations,
  LOCALES,
  seedStoredTheme,
  THEMES,
} from './support';

/** The token name the reveal cases type before submitting the token form. */
const TOKEN_NAME = 'CI upload (a11y)';

test.describe('the wizard shell is WCAG 2.2 AA clean (09.1.1 AC6, plan §8)', () => {
  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      test(`step 2 shell, ${locale} bundle, ${theme} tokens`, async ({ page }) => {
        await seedStoredTheme(page, theme);
        await page.goto(onboardingUrl(locale, 'onboarding-project'));
        await expect(shell(page)).toBeVisible();
        await expectCurrentStep(page, 2);
        await expect(page.locator('[data-testid="onboarding-stepper"]')).toBeVisible();
        // The evidence-panel slot is part of the shell in every state, empty on the steps no page
        // owns yet: its absence would be a missing target, not an invisible one.
        await expect(page.locator('[data-testid="onboarding-evidence-panel"]')).toHaveCount(1);
        // Step 2's own page is what is scanned here (T2); the heading stays the scan's anchor so the
        // same case keeps covering the shell when T3…T5 replace their placeholders.
        await expect(connectCiPage(page)).toBeVisible();
        await expect(page.locator('#onboarding-step-title')).toBeVisible();
        await expect(page.locator('html')).toHaveAttribute('lang', locale);
        // The theme the labels claims: a scan of the wrong token set would be a false pass.
        expect(await currentTheme(page)).toBe(theme);

        await expectNoA11yViolations(page, `onboarding step 2 ${locale}/${theme}`);
      });
    }
  }

  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      test(`step 2 with the one-time value revealed, ${locale} bundle, ${theme} tokens`, async ({
        page,
      }) => {
        await seedStoredTheme(page, theme);
        await page.goto(onboardingUrl(locale, 'onboarding-project'));
        await expect(connectCiPage(page)).toBeVisible();
        expect(await currentTheme(page)).toBe(theme);

        await page.locator('[data-testid="connect-token-name"]').fill(TOKEN_NAME);
        await page.locator('[data-testid="connect-token-submit"]').click();
        // The reveal is the state §8 adds: a read-only value control, Copy, Dismiss and a live
        // region, all of which must be reachable and contrast-safe against both token sets.
        await expect(revealValue(page)).toBeVisible();
        await expect(page.locator('[data-testid="reveal-copy-status"]')).toBeVisible();

        await expectNoA11yViolations(page, `onboarding step 2 reveal ${locale}/${theme}`);
      });
    }
  }

  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      test(`step 2 with the server's metadata rows, ${locale} bundle, ${theme} tokens`, async ({
        page,
      }) => {
        await seedStoredTheme(page, theme);
        // The token seed resumes at step 3; Back opens step 2 with the row the server answered, the
        // state AC7 calls the metadata-only redisplay.
        await page.goto(onboardingUrl(locale, 'onboarding-token'));
        await expectCurrentStep(page, 3);
        expect(await currentTheme(page)).toBe(theme);
        await page.locator('[data-testid="onboarding-back"]').click();
        await expectCurrentStep(page, 2);
        await expect(page.locator('[data-testid="connect-token-row"]')).toHaveCount(1);
        await expect(page.locator('[data-testid="connect-token-rotate"]')).toBeVisible();

        await expectNoA11yViolations(page, `onboarding step 2 metadata ${locale}/${theme}`);
      });
    }
  }
});

test.describe('the reveal is keyboard-reachable in reading order (§8)', () => {
  test('Tab moves from the value to Copy, then to Dismiss', async ({ page }) => {
    await page.goto(onboardingUrl('en', 'onboarding-project'));
    await expect(connectCiPage(page)).toBeVisible();
    await page.locator('[data-testid="connect-token-name"]').fill(TOKEN_NAME);
    await page.locator('[data-testid="connect-token-submit"]').click();
    await expect(revealValue(page)).toBeVisible();

    // Start on the value itself: the slot's own order must be value → Copy → Dismiss, so a
    // keyboard user reaches the action the warning recommends before the one that destroys it.
    await revealValue(page).focus();
    await page.keyboard.press('Tab');
    await expect(page.locator('[data-testid="reveal-copy"]')).toBeFocused();
    expect((await activeElement(page)).className, 'the first Tab left the reveal').toContain(
      'state-panel__action',
    );

    await page.keyboard.press('Tab');
    await expect(page.locator('[data-testid="reveal-dismiss"]')).toBeFocused();
  });
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
